using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Sales.Documents;
using Erp.Modules.Sales.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.Sales.Salon;

public enum AppointmentStatus { Booked, Arrived, Done, Cancelled, NoShow }

public sealed class SalonStaff : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string Name { get; set; }
    public decimal CommissionPercent { get; set; }
    public string? Color { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class SalonVisit : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string VisitNo { get; init; }
    public DateOnly VisitDate { get; init; }
    public Guid CustomerId { get; init; }
    public required string CustomerName { get; init; }
    public decimal Total { get; set; }
    public decimal Commission { get; set; }
    public Guid SalesInvoiceId { get; set; }
    public List<SalonVisitLine> Lines { get; init; } = [];
}

public sealed class SalonVisitLine : TenantEntity
{
    public Guid VisitId { get; init; }
    public int LineNo { get; init; }
    public Guid ItemId { get; init; }
    public Guid StaffId { get; init; }
    public decimal Price { get; init; }
    public decimal DiscountPercent { get; init; }
    public decimal Amount { get; init; }
    public decimal CommissionPercent { get; init; }
    public decimal Commission { get; init; }
}

public sealed class SalonAppointment : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string CustomerName { get; set; }
    public string? Phone { get; set; }
    public Guid StaffId { get; set; }
    public Guid? ItemId { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public int Minutes { get; set; }
    public AppointmentStatus Status { get; set; } = AppointmentStatus.Booked;
    public string? Notes { get; set; }
    public Guid? VisitId { get; set; }
}

internal sealed class SalonStaffConfiguration : IEntityTypeConfiguration<SalonStaff>
{
    public void Configure(EntityTypeBuilder<SalonStaff> b)
    {
        b.ToTable("salon_staff", "sal");
        b.Property(x => x.CommissionPercent).AsQuantity();
    }
}

internal sealed class SalonVisitConfiguration : IEntityTypeConfiguration<SalonVisit>
{
    public void Configure(EntityTypeBuilder<SalonVisit> b)
    {
        b.ToTable("salon_visit", "sal");
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.VisitId);
    }
}

internal sealed class SalonVisitLineConfiguration : IEntityTypeConfiguration<SalonVisitLine>
{
    public void Configure(EntityTypeBuilder<SalonVisitLine> b)
    {
        b.ToTable("salon_visit_line", "sal");
        b.Property(x => x.DiscountPercent).AsQuantity();
        b.Property(x => x.CommissionPercent).AsQuantity();
    }
}

internal sealed class SalonAppointmentConfiguration : IEntityTypeConfiguration<SalonAppointment>
{
    public void Configure(EntityTypeBuilder<SalonAppointment> b) => b.ToTable("salon_appointment", "sal");
}

public sealed record StaffRequest(string Name, decimal CommissionPercent, string? Color, bool IsActive = true);

public sealed record StaffDto(Guid Id, string Name, decimal CommissionPercent, string? Color, bool IsActive);

public sealed record SalonService(Guid Id, string Sku, string NameEn, string NameAr, decimal Price);

public sealed record AppointmentRequest(string CustomerName, string? Phone, Guid StaffId, Guid? ItemId, DateTimeOffset StartsAt, int Minutes, string? Notes);

public sealed record AppointmentDto(Guid Id, string CustomerName, string? Phone, string? WhatsAppPhone, Guid StaffId, string StaffName, Guid? ItemId,
    string? ServiceNameEn, string? ServiceNameAr, DateTimeOffset StartsAt, int Minutes, string Status, string? Notes, Guid? VisitId);

public sealed record CheckoutLine(Guid ItemId, Guid StaffId, decimal? Price, decimal DiscountPercent = 0);

/// <summary>Walk-in by name and phone (or none: the shop's walk-in customer); paid cash or card into the given account.</summary>
public sealed record CheckoutRequest(Guid? AppointmentId, string? CustomerName, string? Phone, DateOnly Date, IReadOnlyList<CheckoutLine> Lines,
    Guid AccountId, ReceiptMethod Method = ReceiptMethod.Cash);

public sealed record VisitDto(Guid Id, string VisitNo, DateOnly VisitDate, string CustomerName, decimal Total, decimal Commission, Guid SalesInvoiceId);

public sealed record CommissionRow(Guid StaffId, string StaffName, int Services, decimal Sales, decimal Commission);

/// <summary>
/// Salon and barber shop: appointments per staff member, and checkout of the services done into a sales invoice paid on the spot.
/// Each service line records who did it, so commission is reported per staff member for any period.
/// </summary>
internal sealed class SalonManager(ErpDbContext db, ITenantContext tenant, INumberingService numbering, CustomerService customers,
    CustomerReceiptService receipts, SalesInvoiceService invoices, IInventoryQueries inventory)
{
    public const string SourceType = "salon_visit";
    private const string WalkInCode = "WALK-IN";

    public async Task<SalonStaff> SaveStaffAsync(Guid? id, StaffRequest r, CancellationToken ct)
    {
        if (r.CommissionPercent is < 0 or > 100)
            throw new BusinessRuleException("sal.salon_commission", "Commission must be between 0% and 100%.", "يجب أن تكون العمولة بين 0% و100%.");
        var staff = id is null
            ? db.Add(new SalonStaff { CompanyId = tenant.RequireCompanyId(), Name = Guard.Required(r.Name, "name").Trim() }).Entity
            : await db.Set<SalonStaff>().SingleOrDefaultAsync(s => s.Id == id && s.CompanyId == tenant.RequireCompanyId(), ct) ?? throw new NotFoundException("Staff", id);
        staff.Name = Guard.Required(r.Name, "name").Trim();
        staff.CommissionPercent = r.CommissionPercent;
        staff.Color = string.IsNullOrWhiteSpace(r.Color) ? null : r.Color.Trim();
        staff.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return staff;
    }

    public async Task<List<StaffDto>> StaffAsync(CancellationToken ct) =>
        await db.Set<SalonStaff>().AsNoTracking().Where(s => s.CompanyId == tenant.RequireCompanyId()).OrderBy(s => s.Name)
            .Select(s => new StaffDto(s.Id, s.Name, s.CommissionPercent, s.Color, s.IsActive)).ToListAsync(ct);

    /// <summary>Priced, active service items: the salon's menu.</summary>
    public async Task<List<SalonService>> ServicesAsync(CancellationToken ct) =>
        (await inventory.GetCatalogAsync(ct)).Where(i => i.Type == ItemType.Service).Select(i => new SalonService(i.Id, i.Sku, i.NameEn, i.NameAr, i.SalesPrice))
        .ToList();

    public async Task<SalonAppointment> BookAsync(Guid? id, AppointmentRequest r, CancellationToken ct)
    {
        var staff = await db.Set<SalonStaff>().AsNoTracking().SingleOrDefaultAsync(s => s.Id == r.StaffId && s.CompanyId == tenant.RequireCompanyId() && s.IsActive, ct)
                    ?? throw new NotFoundException("Staff", r.StaffId);
        if (r.Minutes is < 5 or > 600) throw new BusinessRuleException("sal.salon_minutes", "Check the duration.", "تحقق من المدة.");
        var end = r.StartsAt.AddMinutes(r.Minutes);
        var start = r.StartsAt.ToUniversalTime();
        var clash = await db.Set<SalonAppointment>().AsNoTracking()
            .Where(a => a.StaffId == staff.Id && a.Id != id && (a.Status == AppointmentStatus.Booked || a.Status == AppointmentStatus.Arrived)
                        && a.StartsAt < end.ToUniversalTime())
            .ToListAsync(ct);
        if (clash.Any(a => a.StartsAt.AddMinutes(a.Minutes) > start))
            throw new ConflictException("sal.salon_clash", $"{staff.Name} already has an appointment at that time.", $"لدى {staff.Name} موعد آخر في هذا الوقت.");
        var appointment = id is null
            ? db.Add(new SalonAppointment { CompanyId = tenant.RequireCompanyId(), CustomerName = "" }).Entity
            : await db.Set<SalonAppointment>().SingleOrDefaultAsync(a => a.Id == id && a.CompanyId == tenant.RequireCompanyId(), ct)
              ?? throw new NotFoundException("Appointment", id);
        appointment.CustomerName = Guard.Required(r.CustomerName, "customerName").Trim();
        appointment.Phone = Clean(r.Phone);
        appointment.StaffId = staff.Id;
        appointment.ItemId = r.ItemId;
        appointment.StartsAt = start;
        appointment.Minutes = r.Minutes;
        appointment.Notes = Clean(r.Notes);
        await db.SaveChangesAsync(ct);
        return appointment;
    }

    public async Task SetStatusAsync(Guid id, AppointmentStatus status, CancellationToken ct)
    {
        var a = await db.Set<SalonAppointment>().SingleOrDefaultAsync(x => x.Id == id && x.CompanyId == tenant.RequireCompanyId(), ct)
                ?? throw new NotFoundException("Appointment", id);
        if (a.Status == AppointmentStatus.Done || status == AppointmentStatus.Done)
            throw new BusinessRuleException("sal.salon_done", "Use checkout to finish a visit.", "استخدم الدفع لإنهاء الزيارة.");
        a.Status = status;
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<AppointmentDto>> DayAsync(DateOnly day, CancellationToken ct)
    {
        var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3)).ToUniversalTime();
        var end = start.AddDays(1);
        var list = await (from a in db.Set<SalonAppointment>().AsNoTracking()
                          join s in db.Set<SalonStaff>() on a.StaffId equals s.Id
                          where a.CompanyId == tenant.RequireCompanyId() && a.StartsAt >= start && a.StartsAt < end
                          orderby a.StartsAt
                          select new { a, s.Name }).ToListAsync(ct);
        var items = await inventory.GetItemsAsync(list.Where(x => x.a.ItemId != null).Select(x => x.a.ItemId!.Value), ct);
        return list.Select(x => new AppointmentDto(x.a.Id, x.a.CustomerName, x.a.Phone, Tailoring.TailoringService.WhatsApp(x.a.Phone), x.a.StaffId, x.Name,
            x.a.ItemId, x.a.ItemId is { } i && items.TryGetValue(i, out var it) ? it.NameEn : null,
            x.a.ItemId is { } j && items.TryGetValue(j, out var it2) ? it2.NameAr : null, x.a.StartsAt, x.a.Minutes, x.a.Status.ToString(), x.a.Notes,
            x.a.VisitId)).ToList();
    }

    /// <summary>The visit is invoiced and paid at once; each line keeps the staff member and the commission earned.</summary>
    public Task<SalonVisit> CheckoutAsync(CheckoutRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            if (r.Lines is not { Count: > 0 }) throw new BusinessRuleException("sal.salon_lines", "Add the services done.", "أضف الخدمات المقدمة.");
            SalonAppointment? appointment = null;
            if (r.AppointmentId is { } aid)
            {
                appointment = await db.Set<SalonAppointment>().SingleOrDefaultAsync(a => a.Id == aid && a.CompanyId == tenant.RequireCompanyId(), token)
                              ?? throw new NotFoundException("Appointment", aid);
                if (appointment.VisitId is not null) throw new ConflictException("sal.salon_paid", "This appointment is already paid.", "تم دفع هذا الموعد مسبقاً.");
            }
            var name = Clean(r.CustomerName) ?? appointment?.CustomerName;
            var customer = await CustomerAsync(name, Clean(r.Phone) ?? appointment?.Phone, token);
            var staffIds = r.Lines.Select(l => l.StaffId).Distinct().ToList();
            var staff = await db.Set<SalonStaff>().AsNoTracking().Where(s => staffIds.Contains(s.Id) && s.CompanyId == tenant.RequireCompanyId()).ToDictionaryAsync(s => s.Id, token);
            var items = await inventory.GetItemsAsync(r.Lines.Select(l => l.ItemId), token);
            var visit = new SalonVisit
            {
                CompanyId = tenant.RequireCompanyId(), VisitNo = await numbering.NextAsync(SourceType, "SLN", r.Date, ct: token), VisitDate = r.Date,
                CustomerId = customer.Id, CustomerName = name ?? customer.NameEn
            };
            var no = 0;
            foreach (var l in r.Lines)
            {
                if (!staff.TryGetValue(l.StaffId, out var who)) throw new NotFoundException("Staff", l.StaffId);
                if (!items.TryGetValue(l.ItemId, out var item) || !item.IsActive) throw new NotFoundException("Service", l.ItemId);
                var price = l.Price ?? item.SalesPrice ?? throw new BusinessRuleException("sal.price_missing", $"{item.Sku} has no price.", $"لا يوجد سعر للخدمة {item.Sku}.");
                if (l.DiscountPercent is < 0 or > 100) throw new BusinessRuleException("sal.salon_discount", "Check the discount.", "تحقق من الخصم.");
                var amount = SalesRules.LineAmount(1, price, l.DiscountPercent);
                visit.Lines.Add(new SalonVisitLine
                {
                    VisitId = visit.Id, LineNo = ++no, ItemId = item.Id, StaffId = who.Id, Price = price, DiscountPercent = l.DiscountPercent, Amount = amount,
                    CommissionPercent = who.CommissionPercent, Commission = Money.Round(amount * who.CommissionPercent / 100m)
                });
            }
            visit.Total = visit.Lines.Sum(l => l.Amount);
            visit.Commission = visit.Lines.Sum(l => l.Commission);
            var invoice = await invoices.CreateAsync(new InvoiceRequest(customer.Id, r.Date, r.Date, null, visit.VisitNo, $"Salon visit {visit.VisitNo} / زيارة صالون",
                visit.Lines.Select(l => new InvoiceLineRequest(null, null, l.ItemId, $"{items[l.ItemId].NameEn} · {items[l.ItemId].NameAr} — {staff[l.StaffId].Name}", 1,
                    l.Price, l.DiscountPercent)).ToList(), Money.BaseCurrency, 1m), token);
            await invoices.PostAsync(invoice.Id, token);
            if (visit.Total > 0)
                await receipts.CreateAsync(new ReceiptRequest(customer.Id, r.Date, r.Method, r.AccountId, visit.VisitNo, null, null, null,
                    [new ReceiptAllocationRequest(invoice.Id, invoice.Total)]), token);
            visit.SalesInvoiceId = invoice.Id;
            db.Add(visit);
            if (appointment is not null)
            {
                appointment.Status = AppointmentStatus.Done;
                appointment.VisitId = visit.Id;
            }
            await db.SaveChangesAsync(token);
            return visit;
        }, ct);

    public async Task<List<VisitDto>> VisitsAsync(DateOnly periodStart, DateOnly periodEnd, CancellationToken ct) =>
        await db.Set<SalonVisit>().AsNoTracking()
            .Where(v => v.CompanyId == tenant.RequireCompanyId() && v.VisitDate >= periodStart && v.VisitDate <= periodEnd)
            .OrderByDescending(v => v.VisitDate).ThenByDescending(v => v.VisitNo)
            .Select(v => new VisitDto(v.Id, v.VisitNo, v.VisitDate, v.CustomerName, v.Total, v.Commission, v.SalesInvoiceId)).ToListAsync(ct);

    public async Task<List<CommissionRow>> CommissionAsync(DateOnly periodStart, DateOnly periodEnd, CancellationToken ct) =>
        await (from l in db.Set<SalonVisitLine>().AsNoTracking()
               join v in db.Set<SalonVisit>() on l.VisitId equals v.Id
               join s in db.Set<SalonStaff>() on l.StaffId equals s.Id
               where v.CompanyId == tenant.RequireCompanyId() && v.VisitDate >= periodStart && v.VisitDate <= periodEnd
               group l by new { s.Id, s.Name } into g
               orderby g.Key.Name
               select new CommissionRow(g.Key.Id, g.Key.Name, g.Count(), g.Sum(x => x.Amount), g.Sum(x => x.Commission))).ToListAsync(ct);

    private async Task<Customer> CustomerAsync(string? name, string? phone, CancellationToken ct)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length >= 8)
        {
            var local = digits.Length > 8 && digits.StartsWith("974") ? digits[3..] : digits;
            var known = await db.Set<Customer>().FirstOrDefaultAsync(c => c.CompanyId == tenant.RequireCompanyId() && c.Phone != null && c.Phone.EndsWith(local), ct);
            if (known is not null) return known;
            return await customers.SaveAsync(null, new CustomerRequest($"S{local}", name ?? local, name ?? local, "QA", null, null, null, local, null, null,
                PaymentTermsDays: 0), ct);
        }
        return await db.Set<Customer>().FirstOrDefaultAsync(c => c.CompanyId == tenant.RequireCompanyId() && c.Code == WalkInCode, ct)
               ?? await customers.SaveAsync(null, new CustomerRequest(WalkInCode, "Walk-in customer", "عميل عابر", "QA", null, null, null, null, null, null,
                   PaymentTermsDays: 0), ct);
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}

internal static class SalonEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/salon").WithTags("Salon").RequireAuthorization().RequirePermission(SalesPermissions.SalonUse);
        g.MapGet("/staff", (SalonManager s, CancellationToken ct) => s.StaffAsync(ct));
        g.MapPost("/staff", async (StaffRequest r, SalonManager s, CancellationToken ct) => (await s.SaveStaffAsync(null, r, ct)).Id);
        g.MapPut("/staff/{id:guid}", async (Guid id, StaffRequest r, SalonManager s, CancellationToken ct) => (await s.SaveStaffAsync(id, r, ct)).Id);
        g.MapGet("/services", (SalonManager s, CancellationToken ct) => s.ServicesAsync(ct));
        g.MapGet("/appointments", (DateOnly day, SalonManager s, CancellationToken ct) => s.DayAsync(day, ct));
        g.MapPost("/appointments", async (AppointmentRequest r, SalonManager s, CancellationToken ct) => (await s.BookAsync(null, r, ct)).Id);
        g.MapPut("/appointments/{id:guid}", async (Guid id, AppointmentRequest r, SalonManager s, CancellationToken ct) => (await s.BookAsync(id, r, ct)).Id);
        g.MapPost("/appointments/{id:guid}/status", async (Guid id, StatusBody r, SalonManager s, CancellationToken ct) =>
        {
            await s.SetStatusAsync(id, r.Status, ct);
            return Results.NoContent();
        });
        g.MapPost("/checkout", async (CheckoutRequest r, SalonManager s, CancellationToken ct) =>
        {
            var v = await s.CheckoutAsync(r, ct);
            return new VisitDto(v.Id, v.VisitNo, v.VisitDate, v.CustomerName, v.Total, v.Commission, v.SalesInvoiceId);
        });
        g.MapGet("/visits", (DateOnly from, DateOnly to, SalonManager s, CancellationToken ct) => s.VisitsAsync(from, to, ct));
        g.MapGet("/commission", (DateOnly from, DateOnly to, SalonManager s, CancellationToken ct) => s.CommissionAsync(from, to, ct));
    }

    public sealed record StatusBody(AppointmentStatus Status);
}
