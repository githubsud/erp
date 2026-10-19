using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.Sales.Documents;
using Erp.Modules.Sales.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.Sales.Crm;

public enum LeadSource { Referral, Website, WalkIn, Phone, Exhibition, SocialMedia, Other }

public enum LeadStatus { New, Contacted, Qualified, Lost, Converted }

public enum OpportunityStage { Prospecting, Qualification, Proposal, Negotiation, Won, Lost }

public enum ActivityKind { Call, Meeting, Email, Whatsapp, Visit, Task }

public enum CommissionBasis { Revenue, GrossMargin, Collections }

public sealed class Lead : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string LeadNo { get; init; }
    public required string CompanyName { get; set; }
    public string? ContactName { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public LeadSource Source { get; set; }
    public LeadStatus Status { get; set; } = LeadStatus.New;
    public Guid? OwnerUserId { get; set; }
    public Guid? CustomerId { get; set; }
    public string? Notes { get; set; }
    public string? LostReason { get; set; }
}

public sealed class Opportunity : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string OpportunityNo { get; init; }
    public required string Title { get; set; }
    public Guid? LeadId { get; set; }
    public Guid? CustomerId { get; set; }
    public OpportunityStage Stage { get; set; } = OpportunityStage.Prospecting;
    public decimal ExpectedValue { get; set; }
    public int Probability { get; set; } = 10;
    public DateOnly? ExpectedCloseDate { get; set; }
    public Guid? OwnerUserId { get; set; }
    public Guid? QuotationId { get; set; }
    public string? LostReason { get; set; }
    public DateOnly? ClosedAt { get; set; }
    public string? Notes { get; set; }
}

public sealed class CrmActivity : TenantEntity
{
    public Guid CompanyId { get; init; }
    public Guid? LeadId { get; init; }
    public Guid? OpportunityId { get; init; }
    public Guid? CustomerId { get; init; }
    public ActivityKind Kind { get; init; }
    public required string Subject { get; set; }
    public DateOnly DueDate { get; set; }
    public bool Done { get; set; }
    public DateTimeOffset? DoneAt { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string? Notes { get; set; }
}

public sealed class CommissionRate : TenantEntity
{
    public Guid CompanyId { get; init; }
    public Guid UserId { get; init; }
    public CommissionBasis Basis { get; set; }
    public decimal Percent { get; set; }
}

internal sealed class LeadConfiguration : IEntityTypeConfiguration<Lead>
{
    public void Configure(EntityTypeBuilder<Lead> b) => b.ToTable("lead", "sal");
}

internal sealed class OpportunityConfiguration : IEntityTypeConfiguration<Opportunity>
{
    public void Configure(EntityTypeBuilder<Opportunity> b) => b.ToTable("opportunity", "sal");
}

internal sealed class CrmActivityConfiguration : IEntityTypeConfiguration<CrmActivity>
{
    public void Configure(EntityTypeBuilder<CrmActivity> b) => b.ToTable("crm_activity", "sal");
}

internal sealed class CommissionRateConfiguration : IEntityTypeConfiguration<CommissionRate>
{
    public void Configure(EntityTypeBuilder<CommissionRate> b)
    {
        b.ToTable("commission_rate", "sal");
        b.Property(x => x.Percent).AsQuantity();
    }
}

public sealed record LeadRequest(string CompanyName, string? ContactName, string? Phone, string? Email, LeadSource Source, LeadStatus Status, Guid? OwnerUserId,
    string? Notes, string? LostReason);

public sealed record ConvertLeadRequest(string CustomerCode, string? NameAr, string? OpportunityTitle, decimal? ExpectedValue);

public sealed record OpportunityRequest(string Title, Guid? LeadId, Guid? CustomerId, OpportunityStage Stage, decimal ExpectedValue, int? Probability,
    DateOnly? ExpectedCloseDate, Guid? OwnerUserId, Guid? QuotationId, string? LostReason, string? Notes);

public sealed record ActivityRequest(Guid? LeadId, Guid? OpportunityId, Guid? CustomerId, ActivityKind Kind, string Subject, DateOnly DueDate, Guid? OwnerUserId,
    string? Notes);

public sealed record LeadDto(Guid Id, string LeadNo, string CompanyName, string? ContactName, string? Phone, string? Email, string Source, string Status,
    Guid? OwnerUserId, Guid? CustomerId, string? Notes, string? LostReason, DateTimeOffset CreatedAt);

public sealed record OpportunityDto(Guid Id, string OpportunityNo, string Title, Guid? LeadId, string? LeadName, Guid? CustomerId, string? CustomerNameEn,
    string? CustomerNameAr, string Stage, decimal ExpectedValue, int Probability, decimal Weighted, DateOnly? ExpectedCloseDate, Guid? OwnerUserId,
    Guid? QuotationId, string? QuotationNo, string? LostReason, DateOnly? ClosedAt, string? Notes);

public sealed record ActivityDto(Guid Id, Guid? LeadId, Guid? OpportunityId, Guid? CustomerId, string Kind, string Subject, DateOnly DueDate, bool Done,
    bool Overdue, Guid? OwnerUserId, string? Notes, string? RegardingEn);

public sealed record StageSummary(string Stage, int Count, decimal Value, decimal Weighted);

public sealed record Pipeline(IReadOnlyList<StageSummary> Stages, decimal OpenValue, decimal Weighted, int WonThisMonth, decimal WonValueThisMonth,
    decimal WinRate);

public sealed record CommissionRateRequest(Guid UserId, CommissionBasis Basis, decimal Percent);

public sealed record CommissionRow(Guid UserId, string Basis, decimal Percent, decimal Revenue, decimal GrossMargin, decimal Collections, decimal BasisAmount,
    decimal Commission);

/// <summary>Leads, opportunities and activities. Converting a lead creates the customer (and optionally its first opportunity).</summary>
internal sealed class CrmService(ErpDbContext db, ITenantContext tenant, INumberingService numbering, CustomerService customers, IClock clock)
{
    private static readonly Dictionary<OpportunityStage, int> DefaultProbability = new()
    {
        [OpportunityStage.Prospecting] = 10, [OpportunityStage.Qualification] = 25, [OpportunityStage.Proposal] = 50,
        [OpportunityStage.Negotiation] = 75, [OpportunityStage.Won] = 100, [OpportunityStage.Lost] = 0
    };

    public Task<Lead> SaveLeadAsync(Guid? id, LeadRequest r, CancellationToken ct) => db.InTransactionAsync(token => SaveLeadCoreAsync(id, r, token), ct);

    private async Task<Lead> SaveLeadCoreAsync(Guid? id, LeadRequest r, CancellationToken ct)
    {
        var lead = id is null
            ? db.Add(new Lead
            {
                CompanyId = tenant.RequireCompanyId(), LeadNo = await numbering.NextAsync("lead", "LEAD", clock.Today, ct: ct), CompanyName = "",
                OwnerUserId = tenant.UserId
            }).Entity
            : await FindLeadAsync(id.Value, ct);
        if (lead.Status == LeadStatus.Converted && r.Status != LeadStatus.Converted)
            throw new ConflictException("sal.lead_converted", "This lead is already a customer.", "هذا العميل المحتمل أصبح عميلاً.");
        if (r.Status == LeadStatus.Converted && lead.Status != LeadStatus.Converted)
            throw new BusinessRuleException("sal.lead_convert", "Use “convert to customer”.", "استخدم «تحويل إلى عميل».");
        lead.CompanyName = Guard.Required(r.CompanyName, "companyName").Trim();
        lead.ContactName = Clean(r.ContactName);
        lead.Phone = Clean(r.Phone);
        lead.Email = Clean(r.Email)?.ToLowerInvariant();
        lead.Source = r.Source;
        lead.Status = r.Status;
        lead.OwnerUserId = r.OwnerUserId ?? lead.OwnerUserId;
        lead.Notes = Clean(r.Notes);
        lead.LostReason = r.Status == LeadStatus.Lost ? Clean(r.LostReason) ?? throw new BusinessRuleException("sal.lost_reason", "Say why the lead was lost.", "اذكر سبب الخسارة.") : null;
        await db.SaveChangesAsync(ct);
        return lead;
    }

    public Task<(Lead Lead, Opportunity? Opportunity)> ConvertAsync(Guid id, ConvertLeadRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var lead = await FindLeadAsync(id, token);
            if (lead.Status is LeadStatus.Converted or LeadStatus.Lost)
                throw new ConflictException("sal.lead_closed", "Only open leads can be converted.", "يمكن تحويل العملاء المحتملين المفتوحين فقط.");
            var customer = await customers.SaveAsync(null, new CustomerRequest(r.CustomerCode, lead.CompanyName, r.NameAr ?? lead.CompanyName, "QA", null, null, lead.Email,
                lead.Phone, null, null, SalespersonUserId: lead.OwnerUserId), token);
            lead.CustomerId = customer.Id;
            lead.Status = LeadStatus.Converted;
            Opportunity? opportunity = null;
            foreach (var o in await db.Set<Opportunity>().Where(o => o.LeadId == lead.Id).ToListAsync(token)) o.CustomerId = customer.Id;
            if (!string.IsNullOrWhiteSpace(r.OpportunityTitle))
            {
                opportunity = new Opportunity
                {
                    CompanyId = lead.CompanyId, OpportunityNo = await numbering.NextAsync("opportunity", "OPP", clock.Today, ct: token), Title = r.OpportunityTitle.Trim(),
                    LeadId = lead.Id, CustomerId = customer.Id, ExpectedValue = Money.Round(r.ExpectedValue ?? 0), OwnerUserId = lead.OwnerUserId,
                    Stage = OpportunityStage.Qualification, Probability = DefaultProbability[OpportunityStage.Qualification]
                };
                db.Add(opportunity);
            }
            await db.SaveChangesAsync(token);
            return (lead, opportunity);
        }, ct);

    public Task<Opportunity> SaveOpportunityAsync(Guid? id, OpportunityRequest r, CancellationToken ct) =>
        db.InTransactionAsync(token => SaveOpportunityCoreAsync(id, r, token), ct);

    private async Task<Opportunity> SaveOpportunityCoreAsync(Guid? id, OpportunityRequest r, CancellationToken ct)
    {
        if (r.LeadId is null && r.CustomerId is null)
            throw new BusinessRuleException("sal.opportunity_party", "Link the opportunity to a lead or a customer.", "اربط الفرصة بعميل محتمل أو عميل.");
        if (r.ExpectedValue < 0) throw new BusinessRuleException("sal.opportunity_value", "The value cannot be negative.", "لا يمكن أن تكون القيمة سالبة.");
        var companyId = tenant.RequireCompanyId();
        if (r.LeadId is { } leadId && !await db.Set<Lead>().AnyAsync(l => l.Id == leadId && l.CompanyId == companyId, ct)) throw new NotFoundException("Lead", leadId);
        if (r.CustomerId is { } customerId && !await db.Set<Customer>().AnyAsync(c => c.Id == customerId && c.CompanyId == companyId, ct))
            throw new NotFoundException("Customer", customerId);
        if (r.QuotationId is { } quoteId && !await db.Set<Quotation>().AnyAsync(q => q.Id == quoteId && q.CompanyId == companyId, ct))
            throw new NotFoundException("Quotation", quoteId);
        var opportunity = id is null
            ? db.Add(new Opportunity
            {
                CompanyId = companyId, OpportunityNo = await numbering.NextAsync("opportunity", "OPP", clock.Today, ct: ct), Title = "", OwnerUserId = tenant.UserId
            }).Entity
            : await db.Set<Opportunity>().SingleOrDefaultAsync(o => o.Id == id && o.CompanyId == companyId, ct) ?? throw new NotFoundException("Opportunity", id);
        var stageChanged = opportunity.Stage != r.Stage;
        opportunity.Title = Guard.Required(r.Title, "title").Trim();
        opportunity.LeadId = r.LeadId;
        opportunity.CustomerId = r.CustomerId;
        opportunity.Stage = r.Stage;
        opportunity.ExpectedValue = Money.Round(r.ExpectedValue);
        opportunity.Probability = r.Stage is OpportunityStage.Won or OpportunityStage.Lost || r.Probability is null || stageChanged && r.Probability == opportunity.Probability
            ? DefaultProbability[r.Stage] : Math.Clamp(r.Probability.Value, 0, 100);
        opportunity.ExpectedCloseDate = r.ExpectedCloseDate;
        opportunity.OwnerUserId = r.OwnerUserId ?? opportunity.OwnerUserId;
        opportunity.QuotationId = r.QuotationId;
        opportunity.Notes = Clean(r.Notes);
        if (r.Stage == OpportunityStage.Lost && string.IsNullOrWhiteSpace(r.LostReason))
            throw new BusinessRuleException("sal.lost_reason", "Say why the opportunity was lost.", "اذكر سبب خسارة الفرصة.");
        opportunity.LostReason = r.Stage == OpportunityStage.Lost ? r.LostReason!.Trim() : null;
        opportunity.ClosedAt = r.Stage is OpportunityStage.Won or OpportunityStage.Lost ? opportunity.ClosedAt ?? clock.Today : null;
        await db.SaveChangesAsync(ct);
        return opportunity;
    }

    public async Task<CrmActivity> AddActivityAsync(ActivityRequest r, CancellationToken ct)
    {
        if (r.LeadId is null && r.OpportunityId is null && r.CustomerId is null)
            throw new BusinessRuleException("sal.activity_regarding", "Link the activity to a lead, opportunity or customer.", "اربط النشاط بعميل محتمل أو فرصة أو عميل.");
        var activity = new CrmActivity
        {
            CompanyId = tenant.RequireCompanyId(), LeadId = r.LeadId, OpportunityId = r.OpportunityId, CustomerId = r.CustomerId, Kind = r.Kind,
            Subject = Guard.Required(r.Subject, "subject").Trim(), DueDate = r.DueDate, OwnerUserId = r.OwnerUserId ?? tenant.UserId, Notes = Clean(r.Notes)
        };
        db.Add(activity);
        await db.SaveChangesAsync(ct);
        return activity;
    }

    public async Task CompleteActivityAsync(Guid id, bool done, CancellationToken ct)
    {
        var activity = await db.Set<CrmActivity>().SingleOrDefaultAsync(a => a.Id == id && a.CompanyId == tenant.RequireCompanyId(), ct)
                       ?? throw new NotFoundException("Activity", id);
        activity.Done = done;
        activity.DoneAt = done ? clock.UtcNow : null;
        await db.SaveChangesAsync(ct);
    }

    public async Task<Pipeline> PipelineAsync(Guid? ownerUserId, CancellationToken ct)
    {
        var all = await db.Set<Opportunity>().AsNoTracking()
            .Where(o => o.CompanyId == tenant.RequireCompanyId() && (ownerUserId == null || o.OwnerUserId == ownerUserId)).ToListAsync(ct);
        var stages = Enum.GetValues<OpportunityStage>().Select(s =>
        {
            var list = all.Where(o => o.Stage == s).ToList();
            return new StageSummary(s.ToString(), list.Count, list.Sum(o => o.ExpectedValue), list.Sum(o => Weighted(o)));
        }).ToList();
        var open = all.Where(o => o.Stage is not (OpportunityStage.Won or OpportunityStage.Lost)).ToList();
        var monthStart = new DateOnly(clock.Today.Year, clock.Today.Month, 1);
        var wonMonth = all.Where(o => o.Stage == OpportunityStage.Won && o.ClosedAt >= monthStart).ToList();
        var closed = all.Count(o => o.Stage is OpportunityStage.Won or OpportunityStage.Lost);
        return new Pipeline(stages, open.Sum(o => o.ExpectedValue), open.Sum(o => Weighted(o)), wonMonth.Count, wonMonth.Sum(o => o.ExpectedValue),
            closed == 0 ? 0 : Math.Round(all.Count(o => o.Stage == OpportunityStage.Won) * 100m / closed, 1));
    }

    internal static decimal Weighted(Opportunity o) => Money.Round(o.ExpectedValue * o.Probability / 100m);

    internal async Task<Lead> FindLeadAsync(Guid id, CancellationToken ct) =>
        await db.Set<Lead>().SingleOrDefaultAsync(l => l.Id == id && l.CompanyId == tenant.RequireCompanyId(), ct) ?? throw new NotFoundException("Lead", id);

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}

/// <summary>
/// Sales commission per salesperson (the customer's salesperson): a percentage of invoiced revenue less credit notes, of gross margin
/// (revenue less cost of goods), or of money collected, for a month, all in QAR.
/// </summary>
internal sealed class CommissionService(ErpDbContext db, ITenantContext tenant)
{
    public async Task<List<CommissionRow>> ReportAsync(int year, int month, CancellationToken ct)
    {
        var companyId = tenant.RequireCompanyId();
        var periodStart = new DateOnly(year, month, 1);
        var periodEnd = periodStart.AddMonths(1).AddDays(-1);
        var salesOf = await db.Set<Customer>().AsNoTracking().Where(c => c.CompanyId == companyId && c.SalespersonUserId != null)
            .ToDictionaryAsync(c => c.Id, c => c.SalespersonUserId!.Value, ct);
        var invoices = await db.Set<SalesInvoice>().AsNoTracking().Include(i => i.Lines)
            .Where(i => i.CompanyId == companyId && !i.IsOpening && i.InvoiceDate >= periodStart && i.InvoiceDate <= periodEnd
                        && (i.Status == InvoiceStatus.Posted || i.Status == InvoiceStatus.PartiallyPaid || i.Status == InvoiceStatus.Paid))
            .ToListAsync(ct);
        var notes = await db.Set<CreditNote>().AsNoTracking().Include(n => n.Lines)
            .Where(n => n.CompanyId == companyId && n.NoteDate >= periodStart && n.NoteDate <= periodEnd && n.Status == DocumentStatus.Posted).ToListAsync(ct);
        var receipts = await db.Set<CustomerReceipt>().AsNoTracking()
            .Where(r => r.CompanyId == companyId && r.ReceiptDate >= periodStart && r.ReceiptDate <= periodEnd
                        && (r.Status == ReceiptStatus.Posted || r.Status == ReceiptStatus.PdcCleared)).ToListAsync(ct);
        var rates = await db.Set<CommissionRate>().AsNoTracking().Where(r => r.CompanyId == companyId).ToDictionaryAsync(r => r.UserId, ct);

        var users = salesOf.Values.Distinct().Concat(rates.Keys).Distinct();
        return users.Select(userId =>
        {
            bool Mine(Guid customerId) => salesOf.TryGetValue(customerId, out var u) && u == userId;
            var revenue = invoices.Where(i => Mine(i.CustomerId)).Sum(i => i.TotalBase)
                          - notes.Where(n => Mine(n.CustomerId)).Sum(n => n.Lines.Sum(l => SalesRules.ToBase(l.Amount, n.ExchangeRate)));
            var cost = invoices.Where(i => Mine(i.CustomerId)).Sum(i => i.Lines.Sum(l => l.Cost)) - notes.Where(n => Mine(n.CustomerId)).Sum(n => n.CostReturned);
            var collections = receipts.Where(r => Mine(r.CustomerId)).Sum(r => r.AmountBase);
            rates.TryGetValue(userId, out var rate);
            var basis = rate?.Basis ?? CommissionBasis.Revenue;
            var amount = basis switch { CommissionBasis.GrossMargin => revenue - cost, CommissionBasis.Collections => collections, _ => revenue };
            var percent = rate?.Percent ?? 0;
            return new CommissionRow(userId, basis.ToString(), percent, revenue, revenue - cost, collections, amount, Money.Round(Math.Max(amount, 0) * percent / 100m));
        }).OrderByDescending(r => r.Commission).ToList();
    }
}

internal static class CrmEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/sales/crm").WithTags("CRM").RequireAuthorization();
        g.MapGet("/leads", async (ErpDbContext db, ITenantContext t, CancellationToken ct, string? status = null) =>
        {
            var q = db.Set<Lead>().AsNoTracking().Where(l => l.CompanyId == t.RequireCompanyId());
            if (Enum.TryParse<LeadStatus>(status, true, out var s)) q = q.Where(l => l.Status == s);
            return (await q.OrderByDescending(l => l.CreatedAt).Take(500).ToListAsync(ct)).Select(ToDto);
        }).RequirePermission(SalesPermissions.CrmUse);
        g.MapPost("/leads", async (LeadRequest r, CrmService s, CancellationToken ct) => ToDto(await s.SaveLeadAsync(null, r, ct))).RequirePermission(SalesPermissions.CrmUse);
        g.MapPut("/leads/{id:guid}", async (Guid id, LeadRequest r, CrmService s, CancellationToken ct) => ToDto(await s.SaveLeadAsync(id, r, ct)))
            .RequirePermission(SalesPermissions.CrmUse);
        g.MapPost("/leads/{id:guid}/convert", async (Guid id, ConvertLeadRequest r, CrmService s, CancellationToken ct) =>
        {
            var (lead, opportunity) = await s.ConvertAsync(id, r, ct);
            return new { Lead = ToDto(lead), OpportunityId = opportunity?.Id, lead.CustomerId };
        }).RequirePermission(SalesPermissions.CustomersManage);

        g.MapGet("/opportunities", (ErpDbContext db, ITenantContext t, CancellationToken ct, string? stage = null, Guid? customerId = null) =>
            OpportunitiesAsync(db, t, null, stage, customerId, ct)).RequirePermission(SalesPermissions.CrmUse);
        g.MapPost("/opportunities", async (OpportunityRequest r, CrmService s, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            (await OpportunitiesAsync(db, t, (await s.SaveOpportunityAsync(null, r, ct)).Id, null, null, ct)).Single()).RequirePermission(SalesPermissions.CrmUse);
        g.MapPut("/opportunities/{id:guid}", async (Guid id, OpportunityRequest r, CrmService s, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            (await OpportunitiesAsync(db, t, (await s.SaveOpportunityAsync(id, r, ct)).Id, null, null, ct)).Single()).RequirePermission(SalesPermissions.CrmUse);
        g.MapGet("/pipeline", (CrmService s, CancellationToken ct, Guid? ownerUserId = null) => s.PipelineAsync(ownerUserId, ct)).RequirePermission(SalesPermissions.CrmUse);

        g.MapGet("/activities", (ErpDbContext db, ITenantContext t, IClock clock, CancellationToken ct, bool open = true, Guid? leadId = null, Guid? opportunityId = null,
            Guid? customerId = null) => ActivitiesAsync(db, t, clock, open, leadId, opportunityId, customerId, ct)).RequirePermission(SalesPermissions.CrmUse);
        g.MapPost("/activities", async (ActivityRequest r, CrmService s, CancellationToken ct) => (await s.AddActivityAsync(r, ct)).Id).RequirePermission(SalesPermissions.CrmUse);
        g.MapPost("/activities/{id:guid}/done", async (Guid id, CrmService s, CancellationToken ct, bool done = true) =>
        {
            await s.CompleteActivityAsync(id, done, ct);
            return Results.NoContent();
        }).RequirePermission(SalesPermissions.CrmUse);

        var c = app.MapGroup("/api/sales/commissions").WithTags("CRM").RequireAuthorization();
        c.MapGet("/rates", async (ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            await db.Set<CommissionRate>().AsNoTracking().Where(r => r.CompanyId == t.RequireCompanyId())
                .Select(r => new { r.UserId, Basis = r.Basis.ToString(), r.Percent }).ToListAsync(ct)).RequirePermission(SalesPermissions.CommissionsManage);
        c.MapPut("/rates", async (CommissionRateRequest r, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
        {
            if (r.Percent is < 0 or > 100) throw new BusinessRuleException("sal.commission_percent", "Use a percentage from 0 to 100.", "استخدم نسبة من 0 إلى 100.");
            var rate = await db.Set<CommissionRate>().SingleOrDefaultAsync(x => x.CompanyId == t.RequireCompanyId() && x.UserId == r.UserId, ct)
                       ?? db.Add(new CommissionRate { CompanyId = t.RequireCompanyId(), UserId = r.UserId }).Entity;
            rate.Basis = r.Basis;
            rate.Percent = r.Percent;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission(SalesPermissions.CommissionsManage);
        c.MapGet("/report", (int year, int month, CommissionService s, CancellationToken ct) => s.ReportAsync(year, month, ct))
            .RequirePermission(SalesPermissions.CommissionsManage);
    }

    private static LeadDto ToDto(Lead l) => new(l.Id, l.LeadNo, l.CompanyName, l.ContactName, l.Phone, l.Email, l.Source.ToString(), l.Status.ToString(),
        l.OwnerUserId, l.CustomerId, l.Notes, l.LostReason, l.CreatedAt);

    private static async Task<List<OpportunityDto>> OpportunitiesAsync(ErpDbContext db, ITenantContext t, Guid? id, string? stage, Guid? customerId, CancellationToken ct)
    {
        var q = db.Set<Opportunity>().AsNoTracking().Where(o => o.CompanyId == t.RequireCompanyId());
        if (id is not null) q = q.Where(o => o.Id == id);
        if (Enum.TryParse<OpportunityStage>(stage, true, out var s)) q = q.Where(o => o.Stage == s);
        if (customerId is not null) q = q.Where(o => o.CustomerId == customerId);
        var list = await q.OrderBy(o => o.ExpectedCloseDate ?? DateOnly.MaxValue).Take(1000).ToListAsync(ct);
        var leadIds = list.Where(o => o.LeadId != null).Select(o => o.LeadId!.Value).Distinct().ToList();
        var customerIds = list.Where(o => o.CustomerId != null).Select(o => o.CustomerId!.Value).Distinct().ToList();
        var quoteIds = list.Where(o => o.QuotationId != null).Select(o => o.QuotationId!.Value).Distinct().ToList();
        var leads = await db.Set<Lead>().AsNoTracking().Where(l => leadIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.CompanyName, ct);
        var customers = await db.Set<Customer>().AsNoTracking().Where(c => customerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var quotes = await db.Set<Quotation>().AsNoTracking().Where(q2 => quoteIds.Contains(q2.Id)).ToDictionaryAsync(q2 => q2.Id, q2 => q2.QuoteNo, ct);
        return list.Select(o => new OpportunityDto(o.Id, o.OpportunityNo, o.Title, o.LeadId, o.LeadId is { } l ? leads.GetValueOrDefault(l) : null, o.CustomerId,
            o.CustomerId is { } c1 ? customers.GetValueOrDefault(c1)?.NameEn : null, o.CustomerId is { } c2 ? customers.GetValueOrDefault(c2)?.NameAr : null,
            o.Stage.ToString(), o.ExpectedValue, o.Probability, CrmService.Weighted(o), o.ExpectedCloseDate, o.OwnerUserId, o.QuotationId,
            o.QuotationId is { } qid ? quotes.GetValueOrDefault(qid) : null, o.LostReason, o.ClosedAt, o.Notes)).ToList();
    }

    private static async Task<List<ActivityDto>> ActivitiesAsync(ErpDbContext db, ITenantContext t, IClock clock, bool open, Guid? leadId, Guid? opportunityId,
        Guid? customerId, CancellationToken ct)
    {
        var q = db.Set<CrmActivity>().AsNoTracking().Where(a => a.CompanyId == t.RequireCompanyId());
        if (open) q = q.Where(a => !a.Done);
        if (leadId is not null) q = q.Where(a => a.LeadId == leadId);
        if (opportunityId is not null) q = q.Where(a => a.OpportunityId == opportunityId);
        if (customerId is not null) q = q.Where(a => a.CustomerId == customerId);
        var list = await q.OrderBy(a => a.DueDate).Take(500).ToListAsync(ct);
        var leadIds = list.Where(a => a.LeadId != null).Select(a => a.LeadId!.Value).ToList();
        var oppIds = list.Where(a => a.OpportunityId != null).Select(a => a.OpportunityId!.Value).ToList();
        var custIds = list.Where(a => a.CustomerId != null).Select(a => a.CustomerId!.Value).ToList();
        var leads = await db.Set<Lead>().AsNoTracking().Where(l => leadIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.CompanyName, ct);
        var opps = await db.Set<Opportunity>().AsNoTracking().Where(o => oppIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.Title, ct);
        var custs = await db.Set<Customer>().AsNoTracking().Where(c => custIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.NameEn, ct);
        var today = clock.Today;
        return list.Select(a => new ActivityDto(a.Id, a.LeadId, a.OpportunityId, a.CustomerId, a.Kind.ToString(), a.Subject, a.DueDate, a.Done,
            !a.Done && a.DueDate < today, a.OwnerUserId, a.Notes,
            a.OpportunityId is { } o ? opps.GetValueOrDefault(o) : a.LeadId is { } l ? leads.GetValueOrDefault(l) : a.CustomerId is { } c ? custs.GetValueOrDefault(c) : null))
            .ToList();
    }
}
