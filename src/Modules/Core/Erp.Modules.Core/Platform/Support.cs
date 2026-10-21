using System.Net;
using System.Text.Json;
using Erp.Infrastructure.Outbox;
using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.Core.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.Core.Platform;

// Support requests from customers to the vendor. Platform tables (no RLS): the customer side filters tenant_id itself in Mine().

public enum SupportTicketType { Problem, Question, Suggestion, Requirement }

public enum SupportPriority { Low, Normal, High, Urgent }

public enum SupportStatus { New, InProgress, WaitingCustomer, Solved, Closed }

public enum SupportAuthor { Customer, Admin }

public sealed class SupportTicket : Entity
{
    public Guid TenantId { get; init; }
    public required string TicketNo { get; init; }
    public SupportTicketType Type { get; set; }
    public SupportPriority Priority { get; set; }
    public SupportStatus Status { get; set; }
    public required string Subject { get; set; }
    public required string Language { get; init; }
    public string? Page { get; init; }
    public string? Device { get; init; }
    public Guid RequesterId { get; init; }
    public required string RequesterName { get; init; }
    public string? RequesterEmail { get; init; }
    public Guid? AssignedAdminId { get; set; }
    public Guid? DuplicateOfId { get; set; }
    public DateTimeOffset ResponseDueAt { get; init; }
    public DateTimeOffset? FirstResponseAt { get; set; }
    public DateTimeOffset? SolvedAt { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
    public bool UnreadByCustomer { get; set; }
    public bool UnreadByAdmin { get; set; } = true;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class SupportMessage : Entity
{
    public Guid TicketId { get; init; }
    public SupportAuthor AuthorKind { get; init; }
    public Guid? AuthorUserId { get; init; }
    public Guid? AuthorAdminId { get; init; }
    public required string AuthorName { get; init; }
    public required string Body { get; init; }
    public bool IsInternal { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class SupportAttachment : Entity
{
    public Guid TicketId { get; init; }
    public Guid MessageId { get; init; }
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public int SizeBytes { get; init; }
    public required byte[] Content { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class SupportSavedReply : Entity
{
    public required string Title { get; set; }
    public required string BodyAr { get; set; }
    public required string BodyEn { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

internal sealed class SupportTicketConfiguration : IEntityTypeConfiguration<SupportTicket>
{
    public void Configure(EntityTypeBuilder<SupportTicket> b) => b.ToTable("support_ticket", "core");
}

internal sealed class SupportMessageConfiguration : IEntityTypeConfiguration<SupportMessage>
{
    public void Configure(EntityTypeBuilder<SupportMessage> b) => b.ToTable("support_message", "core");
}

internal sealed class SupportAttachmentConfiguration : IEntityTypeConfiguration<SupportAttachment>
{
    public void Configure(EntityTypeBuilder<SupportAttachment> b) => b.ToTable("support_attachment", "core");
}

internal sealed class SupportSavedReplyConfiguration : IEntityTypeConfiguration<SupportSavedReply>
{
    public void Configure(EntityTypeBuilder<SupportSavedReply> b) => b.ToTable("support_saved_reply", "core");
}

public sealed record SupportFile(string FileName, string ContentType, byte[] Content);

public sealed record NewTicketInput(SupportTicketType Type, SupportPriority Priority, string Subject, string Body, string Language, string? Page, string? Device,
    IReadOnlyList<SupportFile> Files);

public sealed record SupportTicketRow(Guid Id, string TicketNo, Guid TenantId, string? CustomerCode, string? CustomerName, string Type, string Priority,
    string Status, string Subject, string RequesterName, DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt, DateTimeOffset ResponseDueAt,
    DateTimeOffset? FirstResponseAt, bool Overdue, bool Unread, string? DuplicateOfNo);

public sealed record SupportAttachmentDto(Guid Id, string FileName, string ContentType, int SizeBytes);

public sealed record SupportMessageDto(Guid Id, string AuthorKind, string AuthorName, string Body, bool IsInternal, DateTimeOffset CreatedAt,
    IReadOnlyList<SupportAttachmentDto> Attachments);

public sealed record SupportTicketDetail(SupportTicketRow Ticket, string Language, string? Page, string? Device, string? RequesterEmail,
    IReadOnlyList<SupportMessageDto> Messages, IReadOnlyList<SupportTicketRow> Duplicates);

public sealed record SupportCounts(int Open, int New, int Overdue, int Unread);

public sealed record TicketUpdateRequest(SupportStatus? Status, SupportPriority? Priority, SupportTicketType? Type, string? DuplicateOfNo, bool ClearDuplicate = false);

public sealed record SuggestionRow(Guid Id, string TicketNo, string Subject, string Type, string Status, int Shops, int Requests, DateTimeOffset FirstAskedAt,
    DateTimeOffset LastAskedAt);

public sealed record SavedReplyRequest(string Title, string BodyAr, string BodyEn);

public sealed record SavedReplyDto(Guid Id, string Title, string BodyAr, string BodyEn);

/// <summary>
/// Customers' requests to the vendor: problems, questions, suggestions and new requirements, with a conversation, files, a response
/// target from the plan and emails both ways. Staff users open requests (employees on self-service cannot); a user sees their own
/// requests, and owners/managers (core.company:manage) see all of their company's.
/// </summary>
internal sealed class SupportService(ErpDbContext db, ITenantContext tenant, IPermissionService permissions, IOutbox outbox, IClock clock,
    IServiceScopeFactory scopes)
{
    public const string ToAdminsMessage = "core.support_to_admins";
    public const string ToCustomerMessage = "core.support_to_customer";
    public const int MaxFiles = 5;
    public const int MaxFileBytes = 5 * 1024 * 1024;
    public const int SolvedDaysBeforeClosing = 7;

    private static readonly Dictionary<string, string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".webp"] = "image/webp", [".gif"] = "image/gif",
        [".pdf"] = "application/pdf", [".txt"] = "text/plain", [".csv"] = "text/csv",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
    };

    // Customer side ------------------------------------------------------------------------------------------------------------

    /// <summary>The current tenant's tickets that this user may see. Every customer-side query starts here.</summary>
    private async Task<IQueryable<SupportTicket>> MineAsync(CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        var q = db.Set<SupportTicket>().Where(t => t.TenantId == tenantId);
        return await SeesAllAsync(ct) ? q : q.Where(t => t.RequesterId == tenant.ActingUserId);
    }

    private async Task<bool> SeesAllAsync(CancellationToken ct) => await permissions.HasAsync(CorePermissions.CompanyManage, ct);

    private async Task RequireStaffAsync(CancellationToken ct)
    {
        var granted = await permissions.GetPermissionsAsync(tenant.ActingUserId, ct);
        if (granted.Count == 0 || granted.All(p => p.StartsWith("ess.", StringComparison.Ordinal)))
            throw new ForbiddenException("Support requests are sent by the company's staff.");
    }

    public async Task<List<SupportTicketRow>> MyTicketsAsync(CancellationToken ct)
    {
        await RequireStaffAsync(ct);
        var tickets = await (await MineAsync(ct)).AsNoTracking().OrderByDescending(t => t.LastActivityAt).Take(200).ToListAsync(ct);
        return await RowsAsync(tickets, forAdmin: false, ct);
    }

    public async Task<int> UnreadAsync(CancellationToken ct)
    {
        var granted = await permissions.GetPermissionsAsync(tenant.ActingUserId, ct);
        if (granted.Count == 0 || granted.All(p => p.StartsWith("ess.", StringComparison.Ordinal))) return 0;
        return await (await MineAsync(ct)).CountAsync(t => t.UnreadByCustomer, ct);
    }

    public async Task<SupportTicketDetail> MyTicketAsync(Guid id, CancellationToken ct)
    {
        await RequireStaffAsync(ct);
        var ticket = await (await MineAsync(ct)).SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Support request", id);
        if (ticket.UnreadByCustomer)
        {
            ticket.UnreadByCustomer = false;
            await db.SaveChangesAsync(ct);
        }
        return await DetailAsync(ticket, forAdmin: false, ct);
    }

    public Task<SupportTicketDetail> CreateAsync(NewTicketInput input, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            await RequireStaffAsync(token);
            var subject = Limit(Guard.Required(input.Subject, "subject"), 200)!;
            var body = Limit(Guard.Required(input.Body, "body"), 10_000)!;
            var files = CheckFiles(input.Files);
            var tenantId = tenant.RequireTenantId();
            var user = await db.Set<AppUser>().AsNoTracking().SingleAsync(u => u.Id == tenant.ActingUserId, token);
            var hours = await (from s in db.Set<Subscription>()
                               join p in db.Set<Plan>() on s.PlanId equals p.Id
                               where s.TenantId == tenantId
                               select (int?)p.SupportResponseHours).SingleOrDefaultAsync(token) ?? 24;
            var number = await db.Database.SqlQuery<long>($"SELECT nextval('core.support_ticket_no') AS \"Value\"").SingleAsync(token);
            var now = clock.UtcNow;
            var language = input.Language == "en" ? "en" : "ar";
            var ticket = db.Add(new SupportTicket
            {
                TenantId = tenantId, TicketNo = $"SUP-{number}", Type = input.Type, Priority = input.Priority, Status = SupportStatus.New, Subject = subject,
                Language = language, Page = Limit(input.Page, 300), Device = Limit(input.Device, 300), RequesterId = user.Id,
                RequesterName = language == "ar" ? user.DisplayNameAr : user.DisplayNameEn, RequesterEmail = user.Email,
                ResponseDueAt = now.AddHours(hours), LastActivityAt = now, UnreadByAdmin = true, CreatedAt = now
            }).Entity;
            var message = AddMessage(ticket, SupportAuthor.Customer, user.Id, null, ticket.RequesterName, body, false, files, now);
            outbox.Enqueue(ToAdminsMessage, new { ticketId = ticket.Id, messageId = message.Id, kind = "new" });
            await db.SaveChangesAsync(token);
            return await DetailAsync(ticket, forAdmin: false, token);
        }, ct);

    /// <summary>The customer answers. A request waiting for them, or already solved, goes back to the vendor; a closed one needs a new request.</summary>
    public Task<SupportTicketDetail> CustomerReplyAsync(Guid id, string? text, IReadOnlyList<SupportFile> files, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            await RequireStaffAsync(token);
            var ticket = await (await MineAsync(token)).SingleOrDefaultAsync(t => t.Id == id, token) ?? throw new NotFoundException("Support request", id);
            if (ticket.Status == SupportStatus.Closed)
                throw new BusinessRuleException("support.closed", "This request is closed. Please open a new request.", "هذا الطلب مغلق. يرجى فتح طلب جديد.");
            var checkedFiles = CheckFiles(files);
            var body = Limit(text, 10_000);
            if (string.IsNullOrWhiteSpace(body) && checkedFiles.Count == 0)
                throw new BusinessRuleException("support.empty", "Write a message or attach a file.", "اكتب رسالة أو أرفق ملفاً.");
            var user = await db.Set<AppUser>().AsNoTracking().SingleAsync(u => u.Id == tenant.ActingUserId, token);
            var now = clock.UtcNow;
            var message = AddMessage(ticket, SupportAuthor.Customer, user.Id, null, ticket.Language == "ar" ? user.DisplayNameAr : user.DisplayNameEn,
                body ?? "", false, checkedFiles, now);
            if (ticket.Status is SupportStatus.WaitingCustomer or SupportStatus.Solved)
            {
                ticket.Status = SupportStatus.InProgress;
                ticket.SolvedAt = null;
            }
            ticket.UnreadByAdmin = true;
            ticket.LastActivityAt = now;
            ticket.UpdatedAt = now;
            outbox.Enqueue(ToAdminsMessage, new { ticketId = ticket.Id, messageId = message.Id, kind = "reply" });
            await db.SaveChangesAsync(token);
            return await DetailAsync(ticket, forAdmin: false, token);
        }, ct);

    /// <summary>The customer says it is solved (or withdraws it).</summary>
    public async Task<SupportTicketDetail> CustomerSolveAsync(Guid id, CancellationToken ct)
    {
        await RequireStaffAsync(ct);
        var ticket = await (await MineAsync(ct)).SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Support request", id);
        if (ticket.Status is not (SupportStatus.Solved or SupportStatus.Closed))
        {
            ticket.Status = SupportStatus.Solved;
            ticket.SolvedAt = clock.UtcNow;
            ticket.LastActivityAt = clock.UtcNow;
            ticket.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return await DetailAsync(ticket, forAdmin: false, ct);
    }

    public async Task<SupportAttachment> MyAttachmentAsync(Guid id, CancellationToken ct)
    {
        await RequireStaffAsync(ct);
        var mine = await MineAsync(ct);
        return await (from a in db.Set<SupportAttachment>().AsNoTracking()
                      join m in db.Set<SupportMessage>() on a.MessageId equals m.Id
                      where a.Id == id && !m.IsInternal && mine.Any(t => t.Id == a.TicketId)
                      select a).SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Attachment", id);
    }

    // Back-office side ---------------------------------------------------------------------------------------------------------

    public async Task<List<SupportTicketRow>> AdminTicketsAsync(string? status, string? type, Guid? tenantId, string? search, CancellationToken ct)
    {
        var q = db.Set<SupportTicket>().AsNoTracking();
        if (status == "open") q = q.Where(t => t.Status != SupportStatus.Solved && t.Status != SupportStatus.Closed);
        else if (status == "overdue") q = q.Where(t => t.FirstResponseAt == null && t.Status != SupportStatus.Solved && t.Status != SupportStatus.Closed
                                                       && t.ResponseDueAt < clock.UtcNow);
        else if (status == "unread") q = q.Where(t => t.UnreadByAdmin);
        else if (Enum.TryParse<SupportStatus>(status, true, out var s)) q = q.Where(t => t.Status == s);
        if (Enum.TryParse<SupportTicketType>(type, true, out var ty)) q = q.Where(t => t.Type == ty);
        if (tenantId is { } tid) q = q.Where(t => t.TenantId == tid);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            q = q.Where(t => EF.Functions.ILike(t.Subject, $"%{term}%") || EF.Functions.ILike(t.TicketNo, $"%{term}%")
                             || EF.Functions.ILike(t.RequesterName, $"%{term}%"));
        }
        var tickets = await q.OrderByDescending(t => t.UnreadByAdmin).ThenByDescending(t => t.LastActivityAt).Take(500).ToListAsync(ct);
        return await RowsAsync(tickets, forAdmin: true, ct);
    }

    public async Task<SupportCounts> CountsAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var open = db.Set<SupportTicket>().Where(t => t.Status != SupportStatus.Solved && t.Status != SupportStatus.Closed);
        return new SupportCounts(await open.CountAsync(ct), await open.CountAsync(t => t.Status == SupportStatus.New, ct),
            await open.CountAsync(t => t.FirstResponseAt == null && t.ResponseDueAt < now, ct),
            await db.Set<SupportTicket>().CountAsync(t => t.UnreadByAdmin, ct));
    }

    public async Task<SupportTicketDetail> AdminTicketAsync(Guid id, CancellationToken ct)
    {
        var ticket = await db.Set<SupportTicket>().SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Support request", id);
        if (ticket.UnreadByAdmin)
        {
            ticket.UnreadByAdmin = false;
            await db.SaveChangesAsync(ct);
        }
        return await DetailAsync(ticket, forAdmin: true, ct);
    }

    /// <summary>
    /// The vendor answers (or adds an internal note). A public answer is the first response when none was given, sets the status
    /// (waiting for the customer unless another is chosen) and is emailed to the requester.
    /// </summary>
    public async Task<SupportTicketDetail> AdminReplyAsync(Guid id, Guid adminId, string? text, bool isInternal, SupportStatus? status,
        IReadOnlyList<SupportFile> files, CancellationToken ct)
    {
        var tenantId = await db.Set<SupportTicket>().Where(t => t.Id == id).Select(t => (Guid?)t.TenantId).SingleOrDefaultAsync(ct)
                       ?? throw new NotFoundException("Support request", id);
        var checkedFiles = CheckFiles(files);
        var body = Limit(text, 10_000);
        if (string.IsNullOrWhiteSpace(body) && checkedFiles.Count == 0)
            throw new BusinessRuleException("support.empty", "Write a message or attach a file.", "اكتب رسالة أو أرفق ملفاً.");
        // In a scope bound to the customer, so the email joins the customer's outbox in the same transaction as the answer.
        await using (var scope = scopes.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, null, null);
            var sdb = scope.ServiceProvider.GetRequiredService<ErpDbContext>();
            var sOutbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
            await sdb.InTransactionAsync(async token =>
            {
                var ticket = await sdb.Set<SupportTicket>().SingleAsync(t => t.Id == id, token);
                var admin = await sdb.Set<PlatformAdmin>().AsNoTracking().SingleAsync(a => a.Id == adminId, token);
                var now = clock.UtcNow;
                var message = AddMessage(ticket, SupportAuthor.Admin, null, admin.Id, admin.DisplayName, body ?? "", isInternal, checkedFiles, now, sdb);
                ticket.AssignedAdminId ??= admin.Id;
                if (!isInternal)
                {
                    ticket.FirstResponseAt ??= now;
                    ticket.UnreadByCustomer = true;
                    ticket.LastActivityAt = now;
                    SetStatus(ticket, status ?? SupportStatus.WaitingCustomer, now);
                    if (!string.IsNullOrEmpty(ticket.RequesterEmail))
                        sOutbox.Enqueue(ToCustomerMessage, new { ticketId = ticket.Id, messageId = message.Id });
                }
                else if (status is { } s) SetStatus(ticket, s, now);
                ticket.UpdatedAt = now;
                await sdb.SaveChangesAsync(token);
                return true;
            }, ct);
        }
        db.ChangeTracker.Clear();
        return await AdminTicketAsync(id, ct);
    }

    public async Task<SupportTicketDetail> AdminUpdateAsync(Guid id, TicketUpdateRequest r, CancellationToken ct)
    {
        var ticket = await db.Set<SupportTicket>().SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Support request", id);
        var now = clock.UtcNow;
        if (r.Status is { } status) SetStatus(ticket, status, now);
        if (r.Priority is { } priority) ticket.Priority = priority;
        if (r.Type is { } type) ticket.Type = type;
        if (r.ClearDuplicate) ticket.DuplicateOfId = null;
        else if (!string.IsNullOrWhiteSpace(r.DuplicateOfNo))
        {
            var no = r.DuplicateOfNo.Trim().ToUpperInvariant();
            var target = await db.Set<SupportTicket>().AsNoTracking().SingleOrDefaultAsync(t => t.TicketNo == no, ct)
                         ?? throw new BusinessRuleException("support.duplicate_unknown", $"There is no request {no}.", $"لا يوجد طلب برقم {no}.");
            // Always point at the first request of a group, so counting needs one level only.
            var root = target.DuplicateOfId ?? target.Id;
            if (root == ticket.Id)
                throw new BusinessRuleException("support.duplicate_self", "A request cannot be a duplicate of itself.", "لا يمكن أن يكون الطلب مكرراً لنفسه.");
            ticket.DuplicateOfId = root;
            // Requests that pointed at this one follow it to the new first request.
            await db.Set<SupportTicket>().Where(t => t.DuplicateOfId == ticket.Id)
                .ExecuteUpdateAsync(x => x.SetProperty(t => t.DuplicateOfId, root), ct);
        }
        ticket.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return await DetailAsync(ticket, forAdmin: true, ct);
    }

    /// <summary>Suggestions and requirements grouped with their duplicates: how many shops asked for each, most asked first.</summary>
    public async Task<List<SuggestionRow>> SuggestionsAsync(CancellationToken ct)
    {
        var all = await db.Set<SupportTicket>().AsNoTracking()
            .Where(t => t.Type == SupportTicketType.Suggestion || t.Type == SupportTicketType.Requirement || t.DuplicateOfId != null)
            .ToListAsync(ct);
        var byId = all.ToDictionary(t => t.Id);
        var missingRoots = all.Where(t => t.DuplicateOfId is { } d && !byId.ContainsKey(d)).Select(t => t.DuplicateOfId!.Value).Distinct().ToList();
        foreach (var t in await db.Set<SupportTicket>().AsNoTracking().Where(t => missingRoots.Contains(t.Id)).ToListAsync(ct)) byId[t.Id] = t;
        return byId.Values.GroupBy(t => t.DuplicateOfId ?? t.Id)
            .Where(g => byId.TryGetValue(g.Key, out var root) && root.Type is SupportTicketType.Suggestion or SupportTicketType.Requirement)
            .Select(g =>
            {
                var root = byId[g.Key];
                return new SuggestionRow(root.Id, root.TicketNo, root.Subject, root.Type.ToString(), root.Status.ToString(), g.Select(t => t.TenantId).Distinct().Count(),
                    g.Count(), g.Min(t => t.CreatedAt), g.Max(t => t.CreatedAt));
            })
            .OrderByDescending(r => r.Shops).ThenByDescending(r => r.Requests).ThenByDescending(r => r.LastAskedAt).ToList();
    }

    public async Task<SupportAttachment> AdminAttachmentAsync(Guid id, CancellationToken ct) =>
        await db.Set<SupportAttachment>().AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct) ?? throw new NotFoundException("Attachment", id);

    public async Task<List<SavedReplyDto>> SavedRepliesAsync(CancellationToken ct) =>
        await db.Set<SupportSavedReply>().AsNoTracking().OrderBy(r => r.Title).Select(r => new SavedReplyDto(r.Id, r.Title, r.BodyAr, r.BodyEn)).ToListAsync(ct);

    public async Task<List<SavedReplyDto>> SaveReplyAsync(Guid? id, SavedReplyRequest r, CancellationToken ct)
    {
        var title = Limit(Guard.Required(r.Title, "title"), 100)!;
        var reply = id is null
            ? db.Add(new SupportSavedReply { Title = title, BodyAr = "", BodyEn = "", CreatedAt = clock.UtcNow }).Entity
            : await db.Set<SupportSavedReply>().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Saved reply", id);
        reply.Title = title;
        (reply.BodyEn, reply.BodyAr) = Bilingual.Require(r.BodyEn, r.BodyAr);
        reply.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return await SavedRepliesAsync(ct);
    }

    public async Task<List<SavedReplyDto>> DeleteReplyAsync(Guid id, CancellationToken ct)
    {
        await db.Set<SupportSavedReply>().Where(x => x.Id == id).ExecuteDeleteAsync(ct);
        return await SavedRepliesAsync(ct);
    }

    /// <summary>Daily: requests solved more than a week ago without a reply are closed.</summary>
    public async Task<int> CloseSolvedAsync(CancellationToken ct)
    {
        var before = clock.UtcNow.AddDays(-SolvedDaysBeforeClosing);
        return await db.Set<SupportTicket>().Where(t => t.Status == SupportStatus.Solved && t.SolvedAt < before)
            .ExecuteUpdateAsync(x => x.SetProperty(t => t.Status, SupportStatus.Closed).SetProperty(t => t.UpdatedAt, clock.UtcNow), ct);
    }

    // Shared ---------------------------------------------------------------------------------------------------------------------

    private static void SetStatus(SupportTicket ticket, SupportStatus status, DateTimeOffset now)
    {
        if (status == ticket.Status) return;
        ticket.Status = status;
        ticket.SolvedAt = status is SupportStatus.Solved or SupportStatus.Closed ? ticket.SolvedAt ?? now : null;
    }

    private SupportMessage AddMessage(SupportTicket ticket, SupportAuthor author, Guid? userId, Guid? adminId, string name, string body, bool isInternal,
        IReadOnlyList<SupportFile> files, DateTimeOffset now, ErpDbContext? context = null)
    {
        var target = context ?? db;
        var message = target.Add(new SupportMessage
        {
            TicketId = ticket.Id, AuthorKind = author, AuthorUserId = userId, AuthorAdminId = adminId, AuthorName = name, Body = body.Trim(),
            IsInternal = isInternal, CreatedAt = now
        }).Entity;
        foreach (var f in files)
            target.Add(new SupportAttachment
            {
                TicketId = ticket.Id, MessageId = message.Id, FileName = f.FileName, ContentType = f.ContentType, SizeBytes = f.Content.Length,
                Content = f.Content, CreatedAt = now
            });
        return message;
    }

    /// <summary>Screenshots, PDFs and simple sheets only, at most <see cref="MaxFiles"/> of <see cref="MaxFileBytes"/> each. The type comes from the extension.</summary>
    public static List<SupportFile> CheckFiles(IReadOnlyList<SupportFile>? files)
    {
        var list = files ?? [];
        if (list.Count > MaxFiles)
            throw new BusinessRuleException("support.too_many_files", $"Attach up to {MaxFiles} files.", $"أرفق {MaxFiles} ملفات كحد أقصى.");
        return list.Select(f =>
        {
            var name = Path.GetFileName(f.FileName ?? "").Trim();
            if (name.Length is 0 or > 200 || !AllowedTypes.TryGetValue(Path.GetExtension(name), out var type))
                throw new BusinessRuleException("support.file_type", $"{name}: attach pictures (PNG, JPG), PDF, Excel or text files.",
                    $"{name}: أرفق صوراً (PNG أو JPG) أو ملفات PDF أو Excel أو نصية.");
            if (f.Content.Length == 0 || f.Content.Length > MaxFileBytes)
                throw new BusinessRuleException("support.file_size", $"{name}: each file must be under 5 MB.", $"{name}: يجب ألا يزيد حجم الملف عن 5 ميغابايت.");
            return new SupportFile(name, type, f.Content);
        }).ToList();
    }

    private async Task<List<SupportTicketRow>> RowsAsync(List<SupportTicket> tickets, bool forAdmin, CancellationToken ct)
    {
        var tenantIds = tickets.Select(t => t.TenantId).Distinct().ToList();
        var customers = forAdmin
            ? await db.Set<Tenant>().AsNoTracking().Where(t => tenantIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => (t.Code, t.NameEn), ct)
            : [];
        var duplicateIds = tickets.Where(t => t.DuplicateOfId != null).Select(t => t.DuplicateOfId!.Value).Distinct().ToList();
        var numbers = await db.Set<SupportTicket>().AsNoTracking().Where(t => duplicateIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.TicketNo, ct);
        var now = clock.UtcNow;
        return tickets.Select(t =>
        {
            var customer = customers.TryGetValue(t.TenantId, out var c) ? c : default;
            return new SupportTicketRow(t.Id, t.TicketNo, t.TenantId, customer.Code, customer.NameEn, t.Type.ToString(), t.Priority.ToString(), t.Status.ToString(),
                t.Subject, t.RequesterName, t.CreatedAt, t.LastActivityAt, t.ResponseDueAt, t.FirstResponseAt,
                t.FirstResponseAt is null && t.Status is not (SupportStatus.Solved or SupportStatus.Closed) && t.ResponseDueAt < now,
                forAdmin ? t.UnreadByAdmin : t.UnreadByCustomer, t.DuplicateOfId is { } d ? numbers.GetValueOrDefault(d) : null);
        }).ToList();
    }

    private async Task<SupportTicketDetail> DetailAsync(SupportTicket ticket, bool forAdmin, CancellationToken ct)
    {
        var messages = await db.Set<SupportMessage>().AsNoTracking().Where(m => m.TicketId == ticket.Id && (forAdmin || !m.IsInternal))
            .OrderBy(m => m.CreatedAt).ToListAsync(ct);
        var messageIds = messages.Select(m => m.Id).ToList();
        var files = (await db.Set<SupportAttachment>().AsNoTracking().Where(a => messageIds.Contains(a.MessageId))
                .Select(a => new { a.MessageId, Dto = new SupportAttachmentDto(a.Id, a.FileName, a.ContentType, a.SizeBytes) }).ToListAsync(ct))
            .ToLookup(a => a.MessageId, a => a.Dto);
        var row = (await RowsAsync([ticket], forAdmin, ct))[0];
        var duplicates = forAdmin
            ? await RowsAsync(await db.Set<SupportTicket>().AsNoTracking().Where(t => t.DuplicateOfId == ticket.Id).OrderBy(t => t.CreatedAt).ToListAsync(ct), true, ct)
            : [];
        return new SupportTicketDetail(row, ticket.Language, ticket.Page, ticket.Device, forAdmin ? ticket.RequesterEmail : null,
            messages.Select(m => new SupportMessageDto(m.Id, m.AuthorKind.ToString(), m.AuthorName, m.Body, m.IsInternal, m.CreatedAt, files[m.Id].ToList())).ToList(),
            duplicates);
    }

    private static string? Limit(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        return v.Length > max ? v[..max] : v;
    }

    public static async Task<List<SupportFile>> ReadFilesAsync(IFormFileCollection? files, CancellationToken ct)
    {
        var list = new List<SupportFile>();
        foreach (var f in (IEnumerable<IFormFile>?)files ?? [])
        {
            if (f.Length > MaxFileBytes)
                throw new BusinessRuleException("support.file_size", $"{f.FileName}: each file must be under 5 MB.", $"{f.FileName}: يجب ألا يزيد حجم الملف عن 5 ميغابايت.");
            using var ms = new MemoryStream();
            await f.CopyToAsync(ms, ct);
            list.Add(new SupportFile(f.FileName, f.ContentType, ms.ToArray()));
        }
        return list;
    }
}

/// <summary>Emails every active platform administrator about a new request or a customer's reply.</summary>
internal sealed class SupportToAdminsEmailHandler(ErpDbContext db, IEmailSender email, IConfiguration configuration) : IOutboxHandler
{
    public string MessageType => SupportService.ToAdminsMessage;

    public async Task HandleAsync(Guid messageId, JsonElement payload, CancellationToken ct)
    {
        var ticket = await db.Set<SupportTicket>().AsNoTracking().SingleOrDefaultAsync(t => t.Id == payload.GetProperty("ticketId").GetGuid(), ct);
        var message = await db.Set<SupportMessage>().AsNoTracking().SingleOrDefaultAsync(m => m.Id == payload.GetProperty("messageId").GetGuid(), ct);
        if (ticket is null || message is null) return;
        var to = await db.Set<PlatformAdmin>().AsNoTracking().Where(a => a.IsActive).Select(a => a.Email).ToListAsync(ct);
        if (to.Count == 0) return;
        var customer = await db.Set<Tenant>().AsNoTracking().Where(t => t.Id == ticket.TenantId).Select(t => t.Code + " · " + t.NameEn).SingleAsync(ct);
        var isNew = payload.GetProperty("kind").GetString() == "new";
        var link = SupportEmail.Link(configuration, $"/backoffice/support/{ticket.Id}");
        var subject = $"{(isNew ? "New" : "Reply")} {ticket.TicketNo} [{ticket.Type}/{ticket.Priority}] {customer}: {ticket.Subject}";
        await email.SendAsync(SupportEmail.Build(to, subject, "ltr",
        [
            $"{customer} — {message.AuthorName}", $"{ticket.Type} · {ticket.Priority}" + (ticket.Page is null ? "" : $" · {ticket.Page}")
        ], message.Body, link, "Open in the back-office"), ct);
    }
}

/// <summary>Emails the requester when the vendor answers, in the language the request was written in.</summary>
internal sealed class SupportToCustomerEmailHandler(ErpDbContext db, IEmailSender email, IConfiguration configuration) : IOutboxHandler
{
    public string MessageType => SupportService.ToCustomerMessage;

    public async Task HandleAsync(Guid messageId, JsonElement payload, CancellationToken ct)
    {
        var ticket = await db.Set<SupportTicket>().AsNoTracking().SingleOrDefaultAsync(t => t.Id == payload.GetProperty("ticketId").GetGuid(), ct);
        var message = await db.Set<SupportMessage>().AsNoTracking().SingleOrDefaultAsync(m => m.Id == payload.GetProperty("messageId").GetGuid(), ct);
        if (ticket is null || message is null || message.IsInternal || string.IsNullOrEmpty(ticket.RequesterEmail)) return;
        var ar = ticket.Language == "ar";
        var (statusEn, statusAr) = ticket.Status switch
        {
            SupportStatus.WaitingCustomer => ("Waiting for your reply", "بانتظار ردكم"),
            SupportStatus.Solved => ("Solved", "تم الحل"),
            SupportStatus.Closed => ("Closed", "مغلق"),
            _ => ("In progress", "قيد المعالجة")
        };
        var link = SupportEmail.Link(configuration, $"/support/{ticket.Id}");
        var subject = ar ? $"رد على طلبكم {ticket.TicketNo}: {ticket.Subject}" : $"Reply to your request {ticket.TicketNo}: {ticket.Subject}";
        await email.SendAsync(SupportEmail.Build([ticket.RequesterEmail], subject, ar ? "rtl" : "ltr",
            [ar ? $"الحالة: {statusAr}" : $"Status: {statusEn}"], message.Body, link, ar ? "فتح الطلب" : "Open the request"), ct);
    }
}

internal static class SupportEmail
{
    /// <summary>A link into the app when App:PublicUrl is set (e.g. the shop computer's Tailscale address); none otherwise.</summary>
    public static string? Link(IConfiguration configuration, string path) =>
        configuration["App:PublicUrl"] is { Length: > 0 } baseUrl ? baseUrl.TrimEnd('/') + path : null;

    public static EmailMessage Build(IReadOnlyList<string> to, string subject, string dir, string[] lines, string body, string? link, string linkText)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var align = dir == "rtl" ? "right" : "left";
        var html = $"""
            <div dir="{dir}" style="font-family:Tahoma,Arial,sans-serif;font-size:14px;color:#1d2524;text-align:{align}">
              <h3 style="color:#0F5C4D;margin:0 0 8px">{E(subject)}</h3>
              {string.Concat(lines.Select(l => $"<p style=\"margin:4px 0;color:#555\">{E(l)}</p>"))}
              <div style="white-space:pre-wrap;border-inline-start:3px solid #0F5C4D;padding:8px 12px;margin:12px 0;background:#f4f7f6">{E(body)}</div>
              {(link is null ? "" : $"<p><a href=\"{E(link)}\" style=\"color:#0F5C4D\">{E(linkText)}</a></p>")}
            </div>
            """;
        var text = $"{subject}\n{string.Join("\n", lines)}\n\n{body}" + (link is null ? "" : $"\n\n{linkText}: {link}");
        return new EmailMessage(to, subject, html, text);
    }
}

internal static class SupportEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/support").WithTags("Support").RequireAuthorization();
        g.MapGet("/tickets", (SupportService s, CancellationToken ct) => s.MyTicketsAsync(ct));
        g.MapGet("/unread", async (SupportService s, CancellationToken ct) => new { Count = await s.UnreadAsync(ct) });
        g.MapGet("/tickets/{id:guid}", (Guid id, SupportService s, CancellationToken ct) => s.MyTicketAsync(id, ct));
        g.MapPost("/tickets", async ([FromForm] SupportTicketType type, [FromForm] string subject, [FromForm] string body, [FromForm] string? language,
            [FromForm] string? page, [FromForm] string? device, IFormFileCollection? files, HttpContext http, SupportService s, CancellationToken ct,
            [FromForm] SupportPriority priority = SupportPriority.Normal) =>
        {
            return await s.CreateAsync(new NewTicketInput(type, priority, subject, body, language ?? "ar", page, device ?? http.Request.Headers.UserAgent.ToString(),
                await SupportService.ReadFilesAsync(files, ct)), ct);
        }).DisableAntiforgery();
        g.MapPost("/tickets/{id:guid}/messages", async (Guid id, [FromForm] string? body, IFormFileCollection? files, SupportService s, CancellationToken ct) =>
            await s.CustomerReplyAsync(id, body, await SupportService.ReadFilesAsync(files, ct), ct)).DisableAntiforgery();
        g.MapPost("/tickets/{id:guid}/solve", (Guid id, SupportService s, CancellationToken ct) => s.CustomerSolveAsync(id, ct));
        g.MapGet("/attachments/{id:guid}", async (Guid id, SupportService s, CancellationToken ct) => FileResult(await s.MyAttachmentAsync(id, ct)));

        var bo = app.MapGroup("/api/backoffice/support").WithTags("Back-office").RequireAuthorization().AddEndpointFilter(async (context, next) =>
        {
            PlatformAuthService.AdminId(context.HttpContext);
            return await next(context);
        });
        bo.MapGet("/tickets", (SupportService s, CancellationToken ct, string? status = null, string? type = null, Guid? tenantId = null, string? search = null) =>
            s.AdminTicketsAsync(status, type, tenantId, search, ct));
        bo.MapGet("/counts", (SupportService s, CancellationToken ct) => s.CountsAsync(ct));
        bo.MapGet("/tickets/{id:guid}", (Guid id, SupportService s, CancellationToken ct) => s.AdminTicketAsync(id, ct));
        bo.MapPut("/tickets/{id:guid}", (Guid id, TicketUpdateRequest r, SupportService s, CancellationToken ct) => s.AdminUpdateAsync(id, r, ct));
        bo.MapPost("/tickets/{id:guid}/messages", async (Guid id, [FromForm] string? body, [FromForm] bool? isInternal, [FromForm] SupportStatus? status,
            IFormFileCollection? files, HttpContext http, SupportService s, CancellationToken ct) =>
            await s.AdminReplyAsync(id, PlatformAuthService.AdminId(http), body, isInternal ?? false, status, await SupportService.ReadFilesAsync(files, ct), ct))
            .DisableAntiforgery();
        bo.MapGet("/suggestions", (SupportService s, CancellationToken ct) => s.SuggestionsAsync(ct));
        bo.MapGet("/attachments/{id:guid}", async (Guid id, SupportService s, CancellationToken ct) => FileResult(await s.AdminAttachmentAsync(id, ct)));
        bo.MapGet("/saved-replies", (SupportService s, CancellationToken ct) => s.SavedRepliesAsync(ct));
        bo.MapPost("/saved-replies", (SavedReplyRequest r, SupportService s, CancellationToken ct) => s.SaveReplyAsync(null, r, ct));
        bo.MapPut("/saved-replies/{id:guid}", (Guid id, SavedReplyRequest r, SupportService s, CancellationToken ct) => s.SaveReplyAsync(id, r, ct));
        bo.MapDelete("/saved-replies/{id:guid}", (Guid id, SupportService s, CancellationToken ct) => s.DeleteReplyAsync(id, ct));
    }

    /// <summary>Pictures and PDFs open in the browser; anything else downloads. The stored type is from an allow-list, never the uploader's.</summary>
    private static IResult FileResult(SupportAttachment a)
    {
        var inline = a.ContentType.StartsWith("image/", StringComparison.Ordinal) || a.ContentType == "application/pdf";
        return Results.File(a.Content, a.ContentType, inline ? null : a.FileName);
    }
}
