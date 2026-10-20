using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Sales.Documents;
using Erp.Modules.Sales.Domain;
using Erp.Modules.Sales.Tailoring;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.Sales.Workshop;

public enum JobStatus { Received, Diagnosing, AwaitingApproval, InProgress, WaitingParts, Ready, Delivered, Cancelled }

public enum JobLineKind { Labour, Part }

public sealed class Vehicle : TenantEntity
{
    public Guid CompanyId { get; init; }
    public Guid CustomerId { get; set; }
    public required string PlateNo { get; init; }
    public string? Make { get; set; }
    public string? Model { get; set; }
    public int? Year { get; set; }
    public string? Color { get; set; }
    public string? Vin { get; set; }
    public int? LastMileage { get; set; }
}

public sealed class JobCard : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string JobNo { get; init; }
    public Guid CustomerId { get; init; }
    public required string CustomerName { get; init; }
    public string? Phone { get; init; }
    public Guid VehicleId { get; init; }
    public required string PlateNo { get; init; }
    public DateOnly ReceivedOn { get; init; }
    public DateOnly? PromisedOn { get; set; }
    public int? Mileage { get; init; }
    public required string Complaint { get; set; }
    public string? Diagnosis { get; set; }
    public string? TechnicianName { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Received;
    public DateOnly? ApprovedOn { get; set; }
    public decimal Total { get; set; }
    public decimal Paid { get; set; }
    public DateOnly? ReadyOn { get; set; }
    public DateOnly? DeliveredOn { get; set; }
    public Guid? SalesInvoiceId { get; set; }
    public List<JobCardLine> Lines { get; init; } = [];
}

public sealed class JobCardLine : TenantEntity
{
    public Guid JobCardId { get; init; }
    public int LineNo { get; init; }
    public JobLineKind Kind { get; init; }
    public Guid? ItemId { get; init; }
    public required string Description { get; init; }
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal Amount { get; init; }
}

public sealed class JobCardPayment : TenantEntity
{
    public Guid JobCardId { get; init; }
    public Guid ReceiptId { get; init; }
    public DateOnly PaidOn { get; init; }
    public decimal Amount { get; init; }
}

internal sealed class VehicleConfiguration : IEntityTypeConfiguration<Vehicle>
{
    public void Configure(EntityTypeBuilder<Vehicle> b) => b.ToTable("vehicle", "sal");
}

internal sealed class JobCardConfiguration : IEntityTypeConfiguration<JobCard>
{
    public void Configure(EntityTypeBuilder<JobCard> b)
    {
        b.ToTable("job_card", "sal");
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.JobCardId);
    }
}

internal sealed class JobCardLineConfiguration : IEntityTypeConfiguration<JobCardLine>
{
    public void Configure(EntityTypeBuilder<JobCardLine> b)
    {
        b.ToTable("job_card_line", "sal");
        b.Property(x => x.Quantity).AsQuantity();
        b.Property(x => x.UnitPrice).AsQuantity();
    }
}

internal sealed class JobCardPaymentConfiguration : IEntityTypeConfiguration<JobCardPayment>
{
    public void Configure(EntityTypeBuilder<JobCardPayment> b) => b.ToTable("job_card_payment", "sal");
}

public sealed record JobLineRequest(JobLineKind Kind, Guid? ItemId, string? Description, decimal Quantity, decimal UnitPrice);

/// <summary>Either <see cref="CustomerId"/>, or a walk-in's <see cref="CustomerName"/> and <see cref="Phone"/> (found or created by phone).</summary>
public sealed record JobCardRequest(Guid? CustomerId, string? CustomerName, string? Phone, string PlateNo, string? Make, string? Model, int? Year, string? Color,
    DateOnly ReceivedOn, DateOnly? PromisedOn, int? Mileage, string Complaint, string? TechnicianName, IReadOnlyList<JobLineRequest>? Lines,
    TailoringPaymentRequest? Deposit);

public sealed record JobStatusRequest(JobStatus Status, string? TechnicianName = null, string? Diagnosis = null, DateOnly? PromisedOn = null);

/// <summary><see cref="WarehouseId"/>: where the parts are taken from.</summary>
public sealed record JobDeliverRequest(DateOnly Date, TailoringPaymentRequest? FinalPayment, Guid? WarehouseId = null);

public sealed record JobLineDto(int LineNo, string Kind, Guid? ItemId, string Description, decimal Quantity, decimal UnitPrice, decimal Amount);

public sealed record JobCardDto(Guid Id, string JobNo, Guid CustomerId, string CustomerName, string? Phone, string? WhatsAppPhone, Guid VehicleId, string PlateNo,
    string? Vehicle, DateOnly ReceivedOn, DateOnly? PromisedOn, int? Mileage, string Complaint, string? Diagnosis, string? TechnicianName, string Status,
    DateOnly? ApprovedOn, decimal Total, decimal Paid, decimal Balance, DateOnly? ReadyOn, DateOnly? DeliveredOn, Guid? SalesInvoiceId, bool Late,
    IReadOnlyList<JobLineDto> Lines);

public sealed record VehicleDto(Guid Id, string PlateNo, string? Make, string? Model, int? Year, string? Color, string? Vin, int? LastMileage, Guid CustomerId,
    string CustomerName, string? Phone, int Visits, DateOnly? LastVisit);

public sealed record TechnicianLoad(string Technician, int Open, int InProgress, int Late);

public sealed record WorkshopBoard(int Open, int AwaitingApproval, int WaitingParts, int Late, int ReadyNotCollected, decimal DepositsHeld,
    IReadOnlyList<TechnicianLoad> Technicians);

/// <summary>
/// Car workshop job cards. The estimate (parts and labour) can change until the car is delivered; deposits are customer receipts on
/// account; at delivery a sales invoice is posted (parts leave stock), deposits are applied and the balance collected — the ledger only
/// moves through Sales.
/// </summary>
internal sealed class WorkshopService(ErpDbContext db, ITenantContext tenant, INumberingService numbering, IClock clock, CustomerService customers,
    CustomerReceiptService receipts, SalesInvoiceService invoices, CustomerCreditService credits, IInventoryQueries inventory)
{
    public const string SourceType = "job_card";

    public Task<JobCard> CreateAsync(JobCardRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var plate = Plate(r.PlateNo);
            if (string.IsNullOrWhiteSpace(r.Complaint))
                throw new BusinessRuleException("sal.job_complaint", "Write what the customer reports.", "اكتب ما يشكو منه العميل.");
            if (r.PromisedOn is { } promised && promised < r.ReceivedOn)
                throw new BusinessRuleException("sal.job_dates", "The promised date cannot be before the day the car came in.", "لا يمكن أن يسبق موعد التسليم يوم استلام السيارة.");
            var vehicle = await db.Set<Vehicle>().SingleOrDefaultAsync(v => v.CompanyId == tenant.RequireCompanyId() && v.PlateNo == plate, token);
            // A known car brings its owner; otherwise the customer is chosen or created from the walk-in's name and phone.
            var customer = r.CustomerId is null && r.CustomerName is null && vehicle is not null
                ? await CustomerService.ActiveCustomerAsync(db, tenant, vehicle.CustomerId, token)
                : await WalkInCustomers.FindOrCreateAsync(db, tenant, customers, r.CustomerId, r.CustomerName, r.Phone, "W", token);
            if (vehicle is null)
            {
                vehicle = db.Add(new Vehicle { CompanyId = customer.CompanyId, CustomerId = customer.Id, PlateNo = plate }).Entity;
            }
            vehicle.CustomerId = customer.Id;
            vehicle.Make = Clean(r.Make) ?? vehicle.Make;
            vehicle.Model = Clean(r.Model) ?? vehicle.Model;
            vehicle.Year = r.Year ?? vehicle.Year;
            vehicle.Color = Clean(r.Color) ?? vehicle.Color;
            if (r.Mileage is { } km)
            {
                if (km < 0) throw new BusinessRuleException("sal.job_mileage", "The mileage cannot be negative.", "لا يمكن أن يكون العداد سالباً.");
                vehicle.LastMileage = km;
            }
            var job = new JobCard
            {
                CompanyId = customer.CompanyId, JobNo = await numbering.NextAsync(SourceType, "JOB", r.ReceivedOn, ct: token), CustomerId = customer.Id,
                CustomerName = customer.NameEn, Phone = Clean(r.Phone) ?? customer.Phone, VehicleId = vehicle.Id, PlateNo = plate, ReceivedOn = r.ReceivedOn,
                PromisedOn = r.PromisedOn, Mileage = r.Mileage, Complaint = r.Complaint.Trim(), TechnicianName = Clean(r.TechnicianName)
            };
            await SetLinesAsync(job, r.Lines ?? [], token);
            db.Add(job);
            await db.SaveChangesAsync(token);
            if (r.Deposit is { Amount: > 0 } deposit) await PayAsync(job, deposit, r.ReceivedOn, token);
            return job;
        }, ct);

    /// <summary>Replaces the estimate (parts and labour); allowed until the car is delivered.</summary>
    public Task<JobCard> SaveLinesAsync(Guid id, IReadOnlyList<JobLineRequest> lines, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var job = await OpenJobAsync(id, token);
            db.RemoveRange(job.Lines);
            job.Lines.Clear();
            await SetLinesAsync(job, lines, token);
            db.AddRange(job.Lines);   // ids are set in code, so EF would otherwise take new lines for existing ones
            if (job.Total < job.Paid)
                throw new BusinessRuleException("sal.job_below_paid", $"The customer already paid {job.Paid:N2}; the total cannot be less.",
                    $"دفع العميل {job.Paid:N2} بالفعل؛ لا يمكن أن يقل الإجمالي عن ذلك.");
            await db.SaveChangesAsync(token);
            return job;
        }, ct);

    public Task<JobCard> AddPaymentAsync(Guid id, TailoringPaymentRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var job = await OpenJobAsync(id, token);
            await PayAsync(job, r, clock.Today, token);
            return job;
        }, ct);

    public async Task<JobCard> SetStatusAsync(Guid id, JobStatusRequest r, CancellationToken ct)
    {
        var job = await OpenJobAsync(id, ct);
        if (r.Status is JobStatus.Delivered)
            throw new BusinessRuleException("sal.job_deliver", "Use deliver to hand the car over (it issues the invoice).", "استخدم التسليم لإنهاء العمل (يصدر الفاتورة).");
        if (r.Status is JobStatus.Cancelled)
            throw new BusinessRuleException("sal.job_cancel", "Use cancel to cancel the job.", "استخدم الإلغاء لإلغاء العمل.");
        // Moving on from "awaiting approval" means the customer accepted the estimate.
        if (job.Status == JobStatus.AwaitingApproval && r.Status is JobStatus.InProgress or JobStatus.WaitingParts or JobStatus.Ready) job.ApprovedOn ??= clock.Today;
        job.Status = r.Status;
        if (r.Status == JobStatus.Ready) job.ReadyOn ??= clock.Today;
        if (r.TechnicianName is not null) job.TechnicianName = Clean(r.TechnicianName);
        if (r.Diagnosis is not null) job.Diagnosis = Clean(r.Diagnosis);
        if (r.PromisedOn is not null) job.PromisedOn = r.PromisedOn;
        await db.SaveChangesAsync(ct);
        return job;
    }

    /// <summary>Hand-over: the invoice for the job, deposits applied to it, the balance collected (or left on the customer's account).</summary>
    public Task<JobCard> DeliverAsync(Guid id, JobDeliverRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var job = await OpenJobAsync(id, token);
            if (job.Lines.Count == 0)
                throw new BusinessRuleException("sal.job_no_lines", "Add the parts and labour before delivering.", "أضف القطع والأجور قبل التسليم.");
            var invoice = await invoices.CreateAsync(new InvoiceRequest(job.CustomerId, r.Date, r.Date, r.WarehouseId, job.JobNo,
                $"Job card {job.JobNo} · {job.PlateNo} / بطاقة عمل",
                job.Lines.OrderBy(l => l.LineNo).Select(l => new InvoiceLineRequest(null, null, l.ItemId, l.Description, l.Quantity, l.UnitPrice)).ToList(),
                Money.BaseCurrency, 1m), token);
            await invoices.PostAsync(invoice.Id, token);
            var payments = await db.Set<JobCardPayment>().Where(p => p.JobCardId == job.Id).OrderBy(p => p.PaidOn).ToListAsync(token);
            var left = invoice.Total;
            foreach (var p in payments)
            {
                var receipt = await db.Set<CustomerReceipt>().AsNoTracking().SingleAsync(x => x.Id == p.ReceiptId, token);
                var apply = Math.Min(Math.Min(receipt.UnappliedAmount, p.Amount), left);
                if (apply <= 0) continue;
                await credits.ApplyAsync(new ApplyCreditRequest(CreditSource.Receipt, p.ReceiptId, r.Date, [new ReceiptAllocationRequest(invoice.Id, apply)]), token);
                left -= apply;
            }
            if (r.FinalPayment is { Amount: > 0 } final)
            {
                if (final.Amount > left)
                    throw new BusinessRuleException("sal.job_overpaid", $"Only {left:N2} is left to pay.", $"المتبقي للدفع {left:N2} فقط.");
                var receipt = await receipts.CreateAsync(new ReceiptRequest(job.CustomerId, r.Date, final.Method, final.AccountId, Clean(final.Reference) ?? job.JobNo,
                    null, null, null, [new ReceiptAllocationRequest(invoice.Id, Money.Round(final.Amount))]), token);
                db.Add(new JobCardPayment { JobCardId = job.Id, ReceiptId = receipt.Id, PaidOn = r.Date, Amount = Money.Round(final.Amount) });
                job.Paid += Money.Round(final.Amount);
            }
            job.SalesInvoiceId = invoice.Id;
            job.Status = JobStatus.Delivered;
            job.DeliveredOn = r.Date;
            job.ReadyOn ??= r.Date;
            await db.SaveChangesAsync(token);
            return job;
        }, ct);

    /// <summary>Deposits already paid stay on the customer's account as a credit (refundable from customer credits).</summary>
    public async Task<JobCard> CancelAsync(Guid id, CancellationToken ct)
    {
        var job = await OpenJobAsync(id, ct);
        job.Status = JobStatus.Cancelled;
        await db.SaveChangesAsync(ct);
        return job;
    }

    public async Task<List<JobCardDto>> ListAsync(string? status, string? search, Guid? vehicleId, CancellationToken ct)
    {
        var q = db.Set<JobCard>().AsNoTracking().Include(j => j.Lines).Where(j => j.CompanyId == tenant.RequireCompanyId());
        if (vehicleId is not null) q = q.Where(j => j.VehicleId == vehicleId);
        if (status == "open") q = q.Where(j => j.Status != JobStatus.Delivered && j.Status != JobStatus.Cancelled);
        else if (Enum.TryParse<JobStatus>(status, true, out var s)) q = q.Where(j => j.Status == s);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var plate = Plate(term, validate: false);
            q = q.Where(j => EF.Functions.ILike(j.JobNo, $"%{term}%") || EF.Functions.ILike(j.CustomerName, $"%{term}%") || j.PlateNo.Contains(plate)
                             || (j.Phone != null && j.Phone.Contains(term)));
        }
        var list = await q.OrderByDescending(j => j.ReceivedOn).ThenByDescending(j => j.JobNo).Take(500).ToListAsync(ct);
        var vehicles = await VehicleNamesAsync(list.Select(j => j.VehicleId), ct);
        return list.Select(j => ToDto(j, vehicles.GetValueOrDefault(j.VehicleId))).ToList();
    }

    public async Task<JobCardDto> GetAsync(Guid id, CancellationToken ct)
    {
        var job = await FindAsync(id, ct, tracking: false);
        return ToDto(job, (await VehicleNamesAsync([job.VehicleId], ct)).GetValueOrDefault(job.VehicleId));
    }

    /// <summary>Cars by plate, customer or phone, with how often they came in.</summary>
    public async Task<List<VehicleDto>> VehiclesAsync(string? search, CancellationToken ct)
    {
        var q = from v in db.Set<Vehicle>().AsNoTracking()
                join c in db.Set<Customer>() on v.CustomerId equals c.Id
                where v.CompanyId == tenant.RequireCompanyId()
                select new { v, c.NameEn, c.Phone };
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var plate = Plate(term, validate: false);
            q = q.Where(x => x.v.PlateNo.Contains(plate) || EF.Functions.ILike(x.NameEn, $"%{term}%") || (x.Phone != null && x.Phone.Contains(term)));
        }
        var rows = await q.OrderBy(x => x.v.PlateNo).Take(200).ToListAsync(ct);
        var ids = rows.Select(x => x.v.Id).ToList();
        var visits = await db.Set<JobCard>().AsNoTracking().Where(j => ids.Contains(j.VehicleId) && j.Status != JobStatus.Cancelled)
            .GroupBy(j => j.VehicleId).Select(g => new { g.Key, Count = g.Count(), Last = g.Max(j => j.ReceivedOn) }).ToDictionaryAsync(x => x.Key, ct);
        return rows.Select(x => new VehicleDto(x.v.Id, x.v.PlateNo, x.v.Make, x.v.Model, x.v.Year, x.v.Color, x.v.Vin, x.v.LastMileage, x.v.CustomerId, x.NameEn,
            x.Phone, visits.GetValueOrDefault(x.v.Id)?.Count ?? 0, visits.GetValueOrDefault(x.v.Id)?.Last)).ToList();
    }

    public async Task<WorkshopBoard> BoardAsync(CancellationToken ct)
    {
        var today = clock.Today;
        var open = await db.Set<JobCard>().AsNoTracking()
            .Where(j => j.CompanyId == tenant.RequireCompanyId() && j.Status != JobStatus.Delivered && j.Status != JobStatus.Cancelled).ToListAsync(ct);
        var working = open.Where(j => j.Status != JobStatus.Ready).ToList();
        var technicians = working.GroupBy(j => j.TechnicianName ?? "—")
            .Select(g => new TechnicianLoad(g.Key, g.Count(), g.Count(j => j.Status == JobStatus.InProgress), g.Count(j => j.PromisedOn < today)))
            .OrderByDescending(t => t.Late).ThenBy(t => t.Technician).ToList();
        return new WorkshopBoard(open.Count, open.Count(j => j.Status == JobStatus.AwaitingApproval), open.Count(j => j.Status == JobStatus.WaitingParts),
            working.Count(j => j.PromisedOn < today), open.Count(j => j.Status == JobStatus.Ready), open.Sum(j => j.Paid), technicians);
    }

    private async Task SetLinesAsync(JobCard job, IReadOnlyList<JobLineRequest> lines, CancellationToken ct)
    {
        if (lines.Any(l => l.Quantity <= 0 || l.UnitPrice < 0))
            throw new BusinessRuleException("sal.job_lines", "Each line needs a quantity and a price.", "كل بند يحتاج كمية وسعراً.");
        var items = await inventory.GetItemsAsync(lines.Where(l => l.ItemId is not null).Select(l => l.ItemId!.Value), ct);
        var no = 0;
        foreach (var l in lines)
        {
            if (l.ItemId is { } itemId && (!items.TryGetValue(itemId, out var item) || !item.IsActive)) throw new NotFoundException("Item", itemId);
            var description = Clean(l.Description) ?? (l.ItemId is { } id ? $"{items[id].NameEn} · {items[id].NameAr}" : null)
                              ?? throw new BusinessRuleException("sal.job_lines", "Describe each part or job.", "صف كل قطعة أو عمل.");
            job.Lines.Add(new JobCardLine
            {
                JobCardId = job.Id, LineNo = ++no, Kind = l.Kind, ItemId = l.ItemId, Description = description, Quantity = l.Quantity, UnitPrice = l.UnitPrice,
                Amount = Money.Round(l.Quantity * l.UnitPrice)
            });
        }
        job.Total = job.Lines.Sum(l => l.Amount);
    }

    private async Task PayAsync(JobCard job, TailoringPaymentRequest r, DateOnly date, CancellationToken ct)
    {
        var amount = Money.Round(r.Amount);
        if (amount <= 0) throw new BusinessRuleException("sal.job_payment", "Enter the amount received.", "أدخل المبلغ المستلم.");
        // An advance before the estimate is known is allowed; once there is an estimate, payments cannot exceed it.
        if (job.Total > 0 && job.Paid + amount > job.Total)
            throw new BusinessRuleException("sal.job_overpaid", $"Only {job.Total - job.Paid:N2} is left to pay.", $"المتبقي للدفع {job.Total - job.Paid:N2} فقط.");
        var receipt = await receipts.CreateAsync(new ReceiptRequest(job.CustomerId, date, r.Method, r.AccountId, Clean(r.Reference) ?? job.JobNo, null, null,
            null, [], OnAccount: amount), ct);
        db.Add(new JobCardPayment { JobCardId = job.Id, ReceiptId = receipt.Id, PaidOn = date, Amount = amount });
        job.Paid += amount;
        await db.SaveChangesAsync(ct);
    }

    private async Task<JobCard> OpenJobAsync(Guid id, CancellationToken ct)
    {
        var job = await FindAsync(id, ct);
        if (job.Status is JobStatus.Delivered or JobStatus.Cancelled)
            throw new ConflictException("sal.job_closed", "This job card is closed.", "بطاقة العمل هذه مغلقة.");
        return job;
    }

    private async Task<JobCard> FindAsync(Guid id, CancellationToken ct, bool tracking = true)
    {
        var q = db.Set<JobCard>().Include(j => j.Lines).Where(j => j.Id == id && j.CompanyId == tenant.RequireCompanyId());
        return await (tracking ? q : q.AsNoTracking()).SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Job card", id);
    }

    private async Task<Dictionary<Guid, string>> VehicleNamesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return (await db.Set<Vehicle>().AsNoTracking().Where(v => list.Contains(v.Id)).ToListAsync(ct))
            .ToDictionary(v => v.Id, v => string.Join(" ", new[] { v.Make, v.Model, v.Year?.ToString(), v.Color }.Where(x => !string.IsNullOrWhiteSpace(x))));
    }

    private JobCardDto ToDto(JobCard j, string? vehicle) => new(j.Id, j.JobNo, j.CustomerId, j.CustomerName, j.Phone, TailoringService.WhatsApp(j.Phone),
        j.VehicleId, j.PlateNo, string.IsNullOrEmpty(vehicle) ? null : vehicle, j.ReceivedOn, j.PromisedOn, j.Mileage, j.Complaint, j.Diagnosis, j.TechnicianName,
        j.Status.ToString(), j.ApprovedOn, j.Total, j.Paid, j.Total - j.Paid, j.ReadyOn, j.DeliveredOn, j.SalesInvoiceId,
        j.Status is not (JobStatus.Ready or JobStatus.Delivered or JobStatus.Cancelled) && j.PromisedOn < clock.Today,
        j.Lines.OrderBy(l => l.LineNo).Select(l => new JobLineDto(l.LineNo, l.Kind.ToString(), l.ItemId, l.Description, l.Quantity, l.UnitPrice, l.Amount)).ToList());

    /// <summary>Plates are stored upper case without spaces or dashes ("123 456" and "123-456" are the same car).</summary>
    internal static string Plate(string? plate, bool validate = true)
    {
        var clean = new string((plate ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (validate && clean.Length is < 1 or > 20)
            throw new BusinessRuleException("sal.job_plate", "Enter the car's plate number.", "أدخل رقم لوحة السيارة.");
        return clean;
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}

internal static class WorkshopEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/workshop").WithTags("Workshop").RequireAuthorization();
        g.MapGet("/board", (WorkshopService s, CancellationToken ct) => s.BoardAsync(ct)).RequirePermission(SalesPermissions.WorkshopUse);
        g.MapGet("/jobs", (WorkshopService s, CancellationToken ct, string? status = "open", string? search = null, Guid? vehicleId = null) =>
            s.ListAsync(status, search, vehicleId, ct)).RequirePermission(SalesPermissions.WorkshopUse);
        g.MapGet("/jobs/{id:guid}", (Guid id, WorkshopService s, CancellationToken ct) => s.GetAsync(id, ct)).RequirePermission(SalesPermissions.WorkshopUse);
        g.MapPost("/jobs", async (JobCardRequest r, WorkshopService s, CancellationToken ct) => await s.GetAsync((await s.CreateAsync(r, ct)).Id, ct))
            .RequirePermission(SalesPermissions.WorkshopUse);
        g.MapPut("/jobs/{id:guid}/lines", async (Guid id, List<JobLineRequest> lines, WorkshopService s, CancellationToken ct) =>
            await s.GetAsync((await s.SaveLinesAsync(id, lines, ct)).Id, ct)).RequirePermission(SalesPermissions.WorkshopUse);
        g.MapPost("/jobs/{id:guid}/payments", async (Guid id, TailoringPaymentRequest r, WorkshopService s, CancellationToken ct) =>
            await s.GetAsync((await s.AddPaymentAsync(id, r, ct)).Id, ct)).RequirePermission(SalesPermissions.WorkshopUse);
        g.MapPost("/jobs/{id:guid}/status", async (Guid id, JobStatusRequest r, WorkshopService s, CancellationToken ct) =>
            await s.GetAsync((await s.SetStatusAsync(id, r, ct)).Id, ct)).RequirePermission(SalesPermissions.WorkshopUse);
        g.MapPost("/jobs/{id:guid}/deliver", async (Guid id, JobDeliverRequest r, WorkshopService s, CancellationToken ct) =>
            await s.GetAsync((await s.DeliverAsync(id, r, ct)).Id, ct)).RequirePermission(SalesPermissions.WorkshopUse);
        g.MapPost("/jobs/{id:guid}/cancel", async (Guid id, WorkshopService s, CancellationToken ct) => await s.GetAsync((await s.CancelAsync(id, ct)).Id, ct))
            .RequirePermission(SalesPermissions.WorkshopUse);
        g.MapGet("/vehicles", (WorkshopService s, CancellationToken ct, string? search = null) => s.VehiclesAsync(search, ct))
            .RequirePermission(SalesPermissions.WorkshopUse);
        // Parts (stock items) and labour (service items) with their prices, for the estimate.
        g.MapGet("/catalog", (IInventoryQueries inventory, CancellationToken ct) => inventory.GetCatalogAsync(ct)).RequirePermission(SalesPermissions.WorkshopUse);
    }
}
