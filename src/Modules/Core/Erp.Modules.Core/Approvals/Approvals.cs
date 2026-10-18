using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.Core.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.Core.Approvals;

public enum ApprovalStatus { Pending, Approved, Rejected, Withdrawn }

public enum ApprovalDecision { Approved, Rejected }

public sealed class ApprovalRule : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string DocType { get; init; }
    public int StepNo { get; init; }
    public decimal MinAmount { get; init; }
    public required string RoleCode { get; init; }
}

public sealed class ApprovalRequest : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string DocType { get; init; }
    public Guid DocId { get; init; }
    public required string DocNo { get; init; }
    public decimal Amount { get; init; }
    public required string SummaryEn { get; init; }
    public required string SummaryAr { get; init; }
    public ApprovalStatus Status { get; set; }
    public int CurrentStep { get; set; }
    public Guid RequestedBy { get; init; }
    public DateTimeOffset RequestedAt { get; init; }
    public DateTimeOffset? DecidedAt { get; set; }
    public List<ApprovalStep> Steps { get; init; } = [];
}

public sealed class ApprovalStep : TenantEntity
{
    public Guid RequestId { get; init; }
    public int StepNo { get; init; }
    public required string RoleCode { get; init; }
    public ApprovalDecision? Decision { get; set; }
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? Comment { get; set; }
}

internal sealed class ApprovalRuleConfiguration : IEntityTypeConfiguration<ApprovalRule>
{
    public void Configure(EntityTypeBuilder<ApprovalRule> b) => b.ToTable("approval_rule", "core");
}

internal sealed class ApprovalRequestConfiguration : IEntityTypeConfiguration<ApprovalRequest>
{
    public void Configure(EntityTypeBuilder<ApprovalRequest> b)
    {
        b.ToTable("approval_request", "core");
        b.HasMany(x => x.Steps).WithOne().HasForeignKey(x => x.RequestId);
    }
}

internal sealed class ApprovalStepConfiguration : IEntityTypeConfiguration<ApprovalStep>
{
    public void Configure(EntityTypeBuilder<ApprovalStep> b) => b.ToTable("approval_step", "core");
}

public sealed record ApprovalStepRuleDto(decimal MinAmount, string RoleCode);
public sealed record ApprovalDocumentTypeDto(string DocType, string NameEn, string NameAr, IReadOnlyList<ApprovalStepRuleDto> Steps);
public sealed record ApprovalSettingsDto(bool AllowSelfApproval);
public sealed record ApprovalStepDto(int StepNo, string RoleCode, string? Decision, Guid? DecidedBy, string? DecidedByName, DateTimeOffset? DecidedAt,
    string? Comment);
public sealed record ApprovalRequestDto(Guid Id, string DocType, string DocTypeEn, string DocTypeAr, Guid DocId, string DocNo, decimal Amount,
    string SummaryEn, string SummaryAr, string Status, int CurrentStep, Guid RequestedBy, string? RequestedByName, DateTimeOffset RequestedAt,
    DateTimeOffset? DecidedAt, IReadOnlyList<ApprovalStepDto> Steps, bool CanDecide);
public sealed record ApprovalDecisionRequest(string? Comment);

/// <summary>
/// Amount-based approvals. A rule is a list of steps per document type, each with a minimum amount and a role; a document needs
/// every step whose minimum it reaches, in order. Steps are copied onto the request at submission, so editing rules never changes
/// requests already in progress.
/// </summary>
internal sealed class ApprovalService(ErpDbContext db, ITenantContext tenant, IClock clock, IPermissionService permissions,
    IServiceProvider services) : IApprovalService
{
    // Resolved on use: document handlers depend on their module services, which in turn submit through this service.
    private IEnumerable<IApprovalDocument> documents => services.GetServices<IApprovalDocument>();

    public async Task<ApprovalOutcome> SubmitAsync(ApprovalSubmission s, CancellationToken ct)
    {
        db.RequireTransaction("Submitting for approval");
        Document(s.DocType);
        var companyId = tenant.RequireCompanyId();
        if (await db.Set<ApprovalRequest>().AnyAsync(r => r.DocType == s.DocType && r.DocId == s.DocId && r.Status == ApprovalStatus.Pending, ct))
            throw new ConflictException("core.approval_pending", "This document is already waiting for approval.", "هذا المستند بانتظار الاعتماد بالفعل.");
        var steps = await db.Set<ApprovalRule>().AsNoTracking()
            .Where(r => r.CompanyId == companyId && r.DocType == s.DocType && r.MinAmount <= s.Amount)
            .OrderBy(r => r.StepNo).ToListAsync(ct);
        if (steps.Count == 0) return ApprovalOutcome.Approved;

        var request = new ApprovalRequest
        {
            CompanyId = companyId, DocType = s.DocType, DocId = s.DocId, DocNo = s.DocNo, Amount = s.Amount,
            SummaryEn = Truncate(s.SummaryEn), SummaryAr = Truncate(s.SummaryAr), Status = ApprovalStatus.Pending, CurrentStep = 1,
            RequestedBy = tenant.ActingUserId, RequestedAt = clock.UtcNow
        };
        var no = 1;
        foreach (var step in steps) request.Steps.Add(new ApprovalStep { RequestId = request.Id, StepNo = no++, RoleCode = step.RoleCode });
        db.Add(request);
        await db.SaveChangesAsync(ct);
        return ApprovalOutcome.Pending;
    }

    public async Task DecideAsync(string docType, Guid docId, bool approve, string? comment, CancellationToken ct)
    {
        var request = await db.Set<ApprovalRequest>().Include(r => r.Steps)
                          .SingleOrDefaultAsync(r => r.DocType == docType && r.DocId == docId && r.Status == ApprovalStatus.Pending, ct)
                      ?? throw new ConflictException("core.approval_not_pending", "This document is not waiting for approval.",
                          "هذا المستند ليس بانتظار الاعتماد.");
        await DecideAsync(request, approve, comment, ct);
    }

    public Task DecideByIdAsync(Guid requestId, bool approve, string? comment, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var request = await db.Set<ApprovalRequest>().Include(r => r.Steps).SingleOrDefaultAsync(r => r.Id == requestId, token)
                          ?? throw new NotFoundException("Approval request", requestId);
            if (request.Status != ApprovalStatus.Pending)
                throw new ConflictException("core.approval_not_pending", "This request has already been decided.", "تم البت في هذا الطلب مسبقاً.");
            await DecideAsync(request, approve, comment, token);
        }, ct);

    private async Task DecideAsync(ApprovalRequest request, bool approve, string? comment, CancellationToken ct)
    {
        db.RequireTransaction("Deciding an approval");
        var userId = tenant.UserId ?? throw new ForbiddenException();
        var step = request.Steps.Single(s => s.StepNo == request.CurrentStep);
        if (!await CanDecideAsync(request, step, userId, ct))
            throw new ForbiddenException("You are not an approver for this step.");
        if (!approve && string.IsNullOrWhiteSpace(comment))
            throw new BusinessRuleException("core.approval_reason", "Give a reason for rejecting.", "اذكر سبب الرفض.");

        step.Decision = approve ? ApprovalDecision.Approved : ApprovalDecision.Rejected;
        step.DecidedBy = userId;
        step.DecidedAt = clock.UtcNow;
        step.Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim()[..Math.Min(comment.Trim().Length, 500)];

        var handler = Document(request.DocType);
        if (!approve)
        {
            request.Status = ApprovalStatus.Rejected;
            request.DecidedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            await handler.OnRejectedAsync(request.DocId, step.Comment, ct);
        }
        else if (request.CurrentStep == request.Steps.Count)
        {
            request.Status = ApprovalStatus.Approved;
            request.DecidedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            await handler.OnApprovedAsync(request.DocId, ct);
        }
        else
        {
            request.CurrentStep++;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task WithdrawAsync(string docType, Guid docId, CancellationToken ct)
    {
        var request = await db.Set<ApprovalRequest>()
            .SingleOrDefaultAsync(r => r.DocType == docType && r.DocId == docId && r.Status == ApprovalStatus.Pending, ct);
        if (request is null) return;
        request.Status = ApprovalStatus.Withdrawn;
        request.DecidedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Holder of the step's role (owners approve anything), not the submitter unless allowed, and not an earlier approver.</summary>
    internal async Task<bool> CanDecideAsync(ApprovalRequest request, ApprovalStep step, Guid userId, CancellationToken ct)
    {
        if (request.Status != ApprovalStatus.Pending || step.StepNo != request.CurrentStep) return false;
        var roles = await RolesOfAsync(userId, ct);
        var isOwner = (await permissions.GetPermissionsAsync(userId, ct)).Contains(DefaultRoles.Wildcard);
        if (!isOwner && !roles.Contains(step.RoleCode)) return false;
        if (request.Steps.Any(s => s.StepNo < step.StepNo && s.DecidedBy == userId)) return false;
        if (request.RequestedBy == userId)
            return await db.Set<Company>().AsNoTracking().Where(c => c.Id == request.CompanyId).Select(c => c.AllowSelfApproval).SingleAsync(ct);
        return true;
    }

    internal Task<List<string>> RolesOfAsync(Guid userId, CancellationToken ct) =>
        (from ur in db.Set<UserRole>()
         join r in db.Set<Role>() on ur.RoleId equals r.Id
         where ur.UserId == userId
         select r.Code).ToListAsync(ct);

    internal IApprovalDocument Document(string docType) =>
        documents.SingleOrDefault(d => d.DocType == docType)
        ?? throw new BusinessRuleException("core.approval_doc_type", $"Unknown document type {docType}.", $"نوع مستند غير معروف {docType}.");

    internal IEnumerable<IApprovalDocument> Documents => documents.OrderBy(d => d.NameEn);

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300];
}

/// <summary>New companies get each document type's default steps.</summary>
internal sealed class ApprovalProvisioner(ErpDbContext db, IEnumerable<IApprovalDocument> documents) : ITenantProvisioner
{
    public int Order => 90;

    public Task ProvisionAsync(TenantProvisioningContext context, CancellationToken ct)
    {
        foreach (var d in documents)
        {
            var no = 1;
            foreach (var step in d.DefaultSteps.OrderBy(s => s.MinAmount))
                db.Add(new ApprovalRule { CompanyId = context.CompanyId, DocType = d.DocType, StepNo = no++, MinAmount = step.MinAmount, RoleCode = step.RoleCode });
        }
        return Task.CompletedTask;
    }
}

internal static class ApprovalEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/approvals").WithTags("Approvals").RequireAuthorization();

        g.MapGet("/rules", ListRules).RequirePermission(CorePermissions.ApprovalsManage);
        g.MapPut("/rules/{docType}", SaveRules).RequirePermission(CorePermissions.ApprovalsManage);
        g.MapGet("/settings", async (ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            new ApprovalSettingsDto(await db.Set<Company>().Where(c => c.Id == t.RequireCompanyId()).Select(c => c.AllowSelfApproval).SingleAsync(ct)))
            .RequirePermission(CorePermissions.ApprovalsManage);
        g.MapPut("/settings", async (ApprovalSettingsDto r, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
        {
            var company = await db.Set<Company>().SingleAsync(c => c.Id == t.RequireCompanyId(), ct);
            company.AllowSelfApproval = r.AllowSelfApproval;
            await db.SaveChangesAsync(ct);
            return r;
        }).RequirePermission(CorePermissions.ApprovalsManage);

        // Anyone signed in: the inbox only lists steps the user can decide, and deciding re-checks it.
        g.MapGet("/inbox", async (ApprovalService s, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
        {
            var pending = await db.Set<ApprovalRequest>().AsNoTracking().Include(r => r.Steps)
                .Where(r => r.CompanyId == t.RequireCompanyId() && r.Status == ApprovalStatus.Pending).OrderBy(r => r.RequestedAt).ToListAsync(ct);
            var userId = t.UserId ?? throw new ForbiddenException();
            var mine = new List<ApprovalRequest>();
            foreach (var r in pending)
                if (await s.CanDecideAsync(r, r.Steps.Single(x => x.StepNo == r.CurrentStep), userId, ct)) mine.Add(r);
            return await ToDtosAsync(mine, s, db, t, ct, canDecide: true);
        });
        g.MapGet("/requests", async (ApprovalService s, ErpDbContext db, ITenantContext t, CancellationToken ct, string? docType = null,
            Guid? docId = null, bool mine = false) =>
        {
            var q = db.Set<ApprovalRequest>().AsNoTracking().Include(r => r.Steps).Where(r => r.CompanyId == t.RequireCompanyId());
            if (docType is not null) q = q.Where(r => r.DocType == docType);
            if (docId is not null) q = q.Where(r => r.DocId == docId);
            if (mine) q = q.Where(r => r.RequestedBy == t.UserId);
            var list = await q.OrderByDescending(r => r.RequestedAt).Take(200).ToListAsync(ct);
            return await ToDtosAsync(list, s, db, t, ct, canDecide: null);
        });
        g.MapPost("/{id:guid}/approve", async (Guid id, ApprovalDecisionRequest? r, ApprovalService s, CancellationToken ct) =>
        {
            await s.DecideByIdAsync(id, true, r?.Comment, ct);
            return Results.NoContent();
        });
        g.MapPost("/{id:guid}/reject", async (Guid id, ApprovalDecisionRequest r, ApprovalService s, CancellationToken ct) =>
        {
            await s.DecideByIdAsync(id, false, r.Comment, ct);
            return Results.NoContent();
        });
    }

    private static async Task<List<ApprovalDocumentTypeDto>> ListRules(ApprovalService s, ErpDbContext db, ITenantContext t, CancellationToken ct)
    {
        var rules = await db.Set<ApprovalRule>().AsNoTracking().Where(r => r.CompanyId == t.RequireCompanyId()).OrderBy(r => r.StepNo).ToListAsync(ct);
        return s.Documents.Select(d => new ApprovalDocumentTypeDto(d.DocType, d.NameEn, d.NameAr,
            rules.Where(r => r.DocType == d.DocType).Select(r => new ApprovalStepRuleDto(r.MinAmount, r.RoleCode)).ToList())).ToList();
    }

    private static Task<ApprovalDocumentTypeDto> SaveRules(string docType, List<ApprovalStepRuleDto> steps, ApprovalService s, ErpDbContext db,
        ITenantContext t, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var document = s.Document(docType);
            if (steps.Count > 9) throw new BusinessRuleException("core.approval_steps", "At most 9 steps.", "الحد الأقصى 9 خطوات.");
            var roles = await db.Set<Role>().Select(r => r.Code).ToListAsync(token);
            foreach (var step in steps)
            {
                Guard.NonNegative(step.MinAmount, "minAmount");
                if (!roles.Contains(step.RoleCode))
                    throw new BusinessRuleException("core.unknown_role", $"Unknown role {step.RoleCode}.", $"دور غير معروف {step.RoleCode}.");
            }
            var companyId = t.RequireCompanyId();
            db.RemoveRange(await db.Set<ApprovalRule>().Where(r => r.CompanyId == companyId && r.DocType == docType).ToListAsync(token));
            await db.SaveChangesAsync(token);
            var ordered = steps.OrderBy(x => x.MinAmount).ToList();
            for (var i = 0; i < ordered.Count; i++)
                db.Add(new ApprovalRule { CompanyId = companyId, DocType = docType, StepNo = i + 1, MinAmount = ordered[i].MinAmount, RoleCode = ordered[i].RoleCode });
            await db.SaveChangesAsync(token);
            return new ApprovalDocumentTypeDto(document.DocType, document.NameEn, document.NameAr, ordered);
        }, ct);

    private static async Task<List<ApprovalRequestDto>> ToDtosAsync(List<ApprovalRequest> list, ApprovalService s, ErpDbContext db, ITenantContext t,
        CancellationToken ct, bool? canDecide)
    {
        var userIds = list.Select(r => r.RequestedBy).Concat(list.SelectMany(r => r.Steps).Where(x => x.DecidedBy != null).Select(x => x.DecidedBy!.Value))
            .Distinct().ToList();
        var names = await db.Set<AppUser>().AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayNameEn, ct);
        var types = s.Documents.ToDictionary(d => d.DocType);
        var result = new List<ApprovalRequestDto>();
        foreach (var r in list)
        {
            var decide = canDecide ?? (t.UserId is { } uid && r.Status == ApprovalStatus.Pending
                && await s.CanDecideAsync(r, r.Steps.Single(x => x.StepNo == r.CurrentStep), uid, ct));
            types.TryGetValue(r.DocType, out var type);
            result.Add(new ApprovalRequestDto(r.Id, r.DocType, type?.NameEn ?? r.DocType, type?.NameAr ?? r.DocType, r.DocId, r.DocNo, r.Amount,
                r.SummaryEn, r.SummaryAr, r.Status.ToString(), r.CurrentStep, r.RequestedBy, names.GetValueOrDefault(r.RequestedBy), r.RequestedAt,
                r.DecidedAt,
                r.Steps.OrderBy(x => x.StepNo).Select(x => new ApprovalStepDto(x.StepNo, x.RoleCode, x.Decision?.ToString(), x.DecidedBy,
                    x.DecidedBy is { } d ? names.GetValueOrDefault(d) : null, x.DecidedAt, x.Comment)).ToList(), decide));
        }
        return result;
    }
}
