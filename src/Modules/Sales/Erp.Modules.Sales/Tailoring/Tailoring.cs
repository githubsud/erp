using System.Text.Json;
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

namespace Erp.Modules.Sales.Tailoring;

public enum Garment { Abaya, Dress, Thobe, Jalabiya, Other }

public enum TailoringStatus { Received, InProgress, Fitting, Ready, Delivered, Cancelled }

public sealed class TailorMeasurement : TenantEntity
{
    public Guid CustomerId { get; init; }
    public Garment Garment { get; init; }
    public required string Measurements { get; set; }
    public string? Notes { get; set; }
}

public sealed class TailoringOrder : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string OrderNo { get; init; }
    public Guid CustomerId { get; init; }
    public required string CustomerName { get; init; }
    public string? Phone { get; init; }
    public DateOnly OrderDate { get; init; }
    public DateOnly? FittingDate { get; set; }
    public DateOnly DueDate { get; set; }
    public Garment Garment { get; init; }
    public string? Measurements { get; set; }
    public string? StyleNotes { get; set; }
    public string? TailorName { get; set; }
    public TailoringStatus Status { get; set; } = TailoringStatus.Received;
    public decimal Total { get; set; }
    public decimal Paid { get; set; }
    public DateOnly? ReadyOn { get; set; }
    public DateOnly? DeliveredOn { get; set; }
    public Guid? SalesInvoiceId { get; set; }
    public List<TailoringOrderLine> Lines { get; init; } = [];
}

public sealed class TailoringOrderLine : TenantEntity
{
    public Guid OrderId { get; init; }
    public int LineNo { get; init; }
    public Guid? ItemId { get; init; }
    public required string Description { get; init; }
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal Amount { get; init; }
}

public sealed class TailoringPayment : TenantEntity
{
    public Guid OrderId { get; init; }
    public Guid ReceiptId { get; init; }
    public DateOnly PaidOn { get; init; }
    public decimal Amount { get; init; }
}

internal sealed class TailorMeasurementConfiguration : IEntityTypeConfiguration<TailorMeasurement>
{
    public void Configure(EntityTypeBuilder<TailorMeasurement> b) => b.ToTable("tailor_measurement", "sal");
}

internal sealed class TailoringOrderConfiguration : IEntityTypeConfiguration<TailoringOrder>
{
    public void Configure(EntityTypeBuilder<TailoringOrder> b)
    {
        b.ToTable("tailoring_order", "sal");
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.OrderId);
    }
}

internal sealed class TailoringOrderLineConfiguration : IEntityTypeConfiguration<TailoringOrderLine>
{
    public void Configure(EntityTypeBuilder<TailoringOrderLine> b)
    {
        b.ToTable("tailoring_order_line", "sal");
        b.Property(x => x.Quantity).AsQuantity();
        b.Property(x => x.UnitPrice).AsQuantity();
    }
}

internal sealed class TailoringPaymentConfiguration : IEntityTypeConfiguration<TailoringPayment>
{
    public void Configure(EntityTypeBuilder<TailoringPayment> b) => b.ToTable("tailoring_payment", "sal");
}

public sealed record TailoringLineRequest(Guid? ItemId, string? Description, decimal Quantity, decimal UnitPrice);

/// <summary>Money received: into a cash or bank account (card takings go to the bank account the card machine settles to).</summary>
public sealed record TailoringPaymentRequest(decimal Amount, Guid AccountId, ReceiptMethod Method = ReceiptMethod.Cash, string? Reference = null);

/// <summary>Either <see cref="CustomerId"/>, or a walk-in's <see cref="CustomerName"/> and <see cref="Phone"/> (found or created by phone).</summary>
public sealed record TailoringOrderRequest(Guid? CustomerId, string? CustomerName, string? Phone, DateOnly OrderDate, DateOnly DueDate, DateOnly? FittingDate,
    Garment Garment, IReadOnlyDictionary<string, string>? Measurements, string? StyleNotes, string? TailorName, IReadOnlyList<TailoringLineRequest> Lines,
    TailoringPaymentRequest? Deposit);

public sealed record TailoringStatusRequest(TailoringStatus Status, string? TailorName = null, DateOnly? FittingDate = null, DateOnly? DueDate = null);

/// <summary><see cref="WarehouseId"/>: where fabric and other stock items on the order are taken from.</summary>
public sealed record DeliverRequest(DateOnly Date, TailoringPaymentRequest? FinalPayment, Guid? WarehouseId = null);

public sealed record TailoringLineDto(int LineNo, Guid? ItemId, string Description, decimal Quantity, decimal UnitPrice, decimal Amount);

public sealed record TailoringOrderDto(Guid Id, string OrderNo, Guid CustomerId, string CustomerName, string? Phone, string? WhatsAppPhone, DateOnly OrderDate,
    DateOnly? FittingDate, DateOnly DueDate, string Garment, IReadOnlyDictionary<string, string> Measurements, string? StyleNotes, string? TailorName,
    string Status, decimal Total, decimal Paid, decimal Balance, DateOnly? ReadyOn, DateOnly? DeliveredOn, Guid? SalesInvoiceId, bool Late,
    IReadOnlyList<TailoringLineDto> Lines);

public sealed record TailorLoad(string Tailor, int Received, int InProgress, int Fitting, int DueThisWeek, int Late);

public sealed record TailoringBoard(int Open, int DueToday, int Late, int ReadyNotCollected, decimal DepositsHeld, IReadOnlyList<TailorLoad> Tailors);

/// <summary>
/// Made-to-measure orders (abayas, dresses, thobes). Deposits are customer receipts on account; at delivery a sales invoice is posted
/// (fabric items leave stock), the deposits are applied to it and any balance is collected — the ledger only moves through Sales.
/// </summary>
internal sealed class TailoringService(ErpDbContext db, ITenantContext tenant, INumberingService numbering, IClock clock, CustomerService customers,
    CustomerReceiptService receipts, SalesInvoiceService invoices, CustomerCreditService credits, IInventoryQueries inventory)
{
    public const string SourceType = "tailoring_order";

    public Task<TailoringOrder> CreateAsync(TailoringOrderRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            if (r.Lines is not { Count: > 0 } || r.Lines.Any(l => l.Quantity <= 0 || l.UnitPrice < 0))
                throw new BusinessRuleException("sal.tailoring_lines", "Add the garments with their prices.", "أضف القطع مع أسعارها.");
            if (r.DueDate < r.OrderDate)
                throw new BusinessRuleException("sal.tailoring_dates", "The ready date cannot be before the order date.", "لا يمكن أن يسبق تاريخ التسليم تاريخ الطلب.");
            var customer = await CustomerAsync(r.CustomerId, r.CustomerName, r.Phone, token);
            var items = await inventory.GetItemsAsync(r.Lines.Where(l => l.ItemId is not null).Select(l => l.ItemId!.Value), token);
            var order = new TailoringOrder
            {
                CompanyId = customer.CompanyId, OrderNo = await numbering.NextAsync(SourceType, "TLR", r.OrderDate, ct: token), CustomerId = customer.Id,
                CustomerName = customer.NameEn, Phone = Clean(r.Phone) ?? customer.Phone, OrderDate = r.OrderDate, DueDate = r.DueDate,
                FittingDate = r.FittingDate, Garment = r.Garment, StyleNotes = Clean(r.StyleNotes), TailorName = Clean(r.TailorName)
            };
            var no = 0;
            foreach (var l in r.Lines)
            {
                if (l.ItemId is { } itemId && (!items.TryGetValue(itemId, out var item) || !item.IsActive)) throw new NotFoundException("Item", itemId);
                var description = Clean(l.Description) ?? (l.ItemId is { } id ? $"{items[id].NameEn} · {items[id].NameAr}" : null)
                                  ?? throw new BusinessRuleException("sal.tailoring_lines", "Describe each garment.", "صف كل قطعة.");
                order.Lines.Add(new TailoringOrderLine
                {
                    OrderId = order.Id, LineNo = ++no, ItemId = l.ItemId, Description = description, Quantity = l.Quantity, UnitPrice = l.UnitPrice,
                    Amount = Money.Round(l.Quantity * l.UnitPrice)
                });
            }
            order.Total = order.Lines.Sum(l => l.Amount);
            if (r.Measurements is { Count: > 0 })
            {
                order.Measurements = Serialize(r.Measurements);
                await SaveProfileAsync(customer.Id, r.Garment, order.Measurements, token);
            }
            db.Add(order);
            await db.SaveChangesAsync(token);
            if (r.Deposit is { Amount: > 0 } deposit) await PayAsync(order, deposit, r.OrderDate, token);
            return order;
        }, ct);

    public Task<TailoringOrder> AddPaymentAsync(Guid id, TailoringPaymentRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var order = await FindAsync(id, token);
            if (order.Status is TailoringStatus.Delivered or TailoringStatus.Cancelled)
                throw new ConflictException("sal.tailoring_closed", "This order is closed.", "هذا الطلب مغلق.");
            await PayAsync(order, r, clock.Today, token);
            return order;
        }, ct);

    public async Task<TailoringOrder> SetStatusAsync(Guid id, TailoringStatusRequest r, CancellationToken ct)
    {
        var order = await FindAsync(id, ct);
        if (order.Status is TailoringStatus.Delivered or TailoringStatus.Cancelled)
            throw new ConflictException("sal.tailoring_closed", "This order is closed.", "هذا الطلب مغلق.");
        if (r.Status == TailoringStatus.Delivered)
            throw new BusinessRuleException("sal.tailoring_deliver", "Use deliver to hand the order over (it issues the invoice).", "استخدم التسليم لإنهاء الطلب (يصدر الفاتورة).");
        order.Status = r.Status;
        if (r.Status == TailoringStatus.Ready) order.ReadyOn ??= clock.Today;
        if (r.TailorName is not null) order.TailorName = Clean(r.TailorName);
        if (r.FittingDate is not null) order.FittingDate = r.FittingDate;
        if (r.DueDate is not null) order.DueDate = r.DueDate.Value;
        await db.SaveChangesAsync(ct);
        return order;
    }

    /// <summary>Hand-over: the invoice for the order, the deposits applied to it, the balance collected (or left on the customer's account).</summary>
    public Task<TailoringOrder> DeliverAsync(Guid id, DeliverRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var order = await FindAsync(id, token);
            if (order.Status is TailoringStatus.Delivered or TailoringStatus.Cancelled)
                throw new ConflictException("sal.tailoring_closed", "This order is closed.", "هذا الطلب مغلق.");
            var invoice = await invoices.CreateAsync(new InvoiceRequest(order.CustomerId, r.Date, r.Date, r.WarehouseId, order.OrderNo,
                $"Tailoring order {order.OrderNo} / طلب خياطة",
                order.Lines.OrderBy(l => l.LineNo).Select(l => new InvoiceLineRequest(null, null, l.ItemId, l.Description, l.Quantity, l.UnitPrice)).ToList(),
                Money.BaseCurrency, 1m), token);
            await invoices.PostAsync(invoice.Id, token);
            var payments = await db.Set<TailoringPayment>().Where(p => p.OrderId == order.Id).OrderBy(p => p.PaidOn).ToListAsync(token);
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
                    throw new BusinessRuleException("sal.tailoring_overpaid", $"Only {left:N2} is left to pay.", $"المتبقي للدفع {left:N2} فقط.");
                var receipt = await receipts.CreateAsync(new ReceiptRequest(order.CustomerId, r.Date, final.Method, final.AccountId, Clean(final.Reference) ?? order.OrderNo,
                    null, null, null, [new ReceiptAllocationRequest(invoice.Id, Money.Round(final.Amount))]), token);
                db.Add(new TailoringPayment { OrderId = order.Id, ReceiptId = receipt.Id, PaidOn = r.Date, Amount = Money.Round(final.Amount) });
                order.Paid += Money.Round(final.Amount);
            }
            order.SalesInvoiceId = invoice.Id;
            order.Status = TailoringStatus.Delivered;
            order.DeliveredOn = r.Date;
            order.ReadyOn ??= r.Date;
            await db.SaveChangesAsync(token);
            return order;
        }, ct);

    /// <summary>Deposits already paid stay on the customer's account as a credit (refundable from customer credits).</summary>
    public async Task<TailoringOrder> CancelAsync(Guid id, CancellationToken ct)
    {
        var order = await FindAsync(id, ct);
        if (order.Status is TailoringStatus.Delivered or TailoringStatus.Cancelled)
            throw new ConflictException("sal.tailoring_closed", "This order is closed.", "هذا الطلب مغلق.");
        order.Status = TailoringStatus.Cancelled;
        await db.SaveChangesAsync(ct);
        return order;
    }

    public async Task<List<TailoringOrderDto>> ListAsync(string? status, string? search, Guid? customerId, CancellationToken ct)
    {
        var q = db.Set<TailoringOrder>().AsNoTracking().Include(o => o.Lines).Where(o => o.CompanyId == tenant.RequireCompanyId());
        if (customerId is not null) q = q.Where(o => o.CustomerId == customerId);
        if (status == "open") q = q.Where(o => o.Status != TailoringStatus.Delivered && o.Status != TailoringStatus.Cancelled);
        else if (Enum.TryParse<TailoringStatus>(status, true, out var s)) q = q.Where(o => o.Status == s);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            q = q.Where(o => EF.Functions.ILike(o.OrderNo, $"%{term}%") || EF.Functions.ILike(o.CustomerName, $"%{term}%") || (o.Phone != null && o.Phone.Contains(term)));
        }
        var list = await q.OrderBy(o => o.DueDate).ThenBy(o => o.OrderNo).Take(500).ToListAsync(ct);
        return list.Select(ToDto).ToList();
    }

    public async Task<TailoringOrderDto> GetAsync(Guid id, CancellationToken ct) => ToDto(await FindAsync(id, ct, tracking: false));

    public async Task<Dictionary<string, Dictionary<string, string>>> ProfilesAsync(Guid customerId, CancellationToken ct) =>
        (await db.Set<TailorMeasurement>().AsNoTracking().Where(m => m.CustomerId == customerId).ToListAsync(ct))
        .ToDictionary(m => m.Garment.ToString(), m => Parse(m.Measurements));

    public async Task<TailoringBoard> BoardAsync(CancellationToken ct)
    {
        var today = clock.Today;
        var open = await db.Set<TailoringOrder>().AsNoTracking()
            .Where(o => o.CompanyId == tenant.RequireCompanyId() && o.Status != TailoringStatus.Delivered && o.Status != TailoringStatus.Cancelled).ToListAsync(ct);
        var working = open.Where(o => o.Status != TailoringStatus.Ready).ToList();
        var tailors = working.GroupBy(o => o.TailorName ?? "—").Select(g => new TailorLoad(g.Key, g.Count(o => o.Status == TailoringStatus.Received),
                g.Count(o => o.Status == TailoringStatus.InProgress), g.Count(o => o.Status == TailoringStatus.Fitting), g.Count(o => o.DueDate <= today.AddDays(7)),
                g.Count(o => o.DueDate < today)))
            .OrderByDescending(t => t.Late).ThenBy(t => t.Tailor).ToList();
        return new TailoringBoard(open.Count, working.Count(o => o.DueDate == today), working.Count(o => o.DueDate < today),
            open.Count(o => o.Status == TailoringStatus.Ready), open.Sum(o => o.Paid), tailors);
    }

    private async Task PayAsync(TailoringOrder order, TailoringPaymentRequest r, DateOnly date, CancellationToken ct)
    {
        var amount = Money.Round(r.Amount);
        if (amount <= 0) throw new BusinessRuleException("sal.tailoring_payment", "Enter the amount received.", "أدخل المبلغ المستلم.");
        if (order.Paid + amount > order.Total)
            throw new BusinessRuleException("sal.tailoring_overpaid", $"Only {order.Total - order.Paid:N2} is left to pay.", $"المتبقي للدفع {order.Total - order.Paid:N2} فقط.");
        var receipt = await receipts.CreateAsync(new ReceiptRequest(order.CustomerId, date, r.Method, r.AccountId, Clean(r.Reference) ?? order.OrderNo, null, null,
            null, [], OnAccount: amount), ct);
        db.Add(new TailoringPayment { OrderId = order.Id, ReceiptId = receipt.Id, PaidOn = date, Amount = amount });
        order.Paid += amount;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Existing customer, or a walk-in found by phone, or a new cash customer created with the phone as its code.</summary>
    private async Task<Customer> CustomerAsync(Guid? customerId, string? name, string? phone, CancellationToken ct)
    {
        if (customerId is { } id) return await CustomerService.ActiveCustomerAsync(db, tenant, id, ct);
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length < 8 || string.IsNullOrWhiteSpace(name))
            throw new BusinessRuleException("sal.tailoring_customer", "Choose a customer or enter the walk-in's name and phone.", "اختر عميلاً أو أدخل اسم العميل ورقم هاتفه.");
        var local = digits.Length > 8 && digits.StartsWith("974") ? digits[3..] : digits;
        var existing = await db.Set<Customer>().FirstOrDefaultAsync(c => c.CompanyId == tenant.RequireCompanyId() && c.Phone != null && c.Phone.EndsWith(local), ct);
        if (existing is not null) return existing;
        return await customers.SaveAsync(null, new CustomerRequest($"T{local}", name.Trim(), name.Trim(), "QA", null, null, null, local, null, null,
            PaymentTermsDays: 0), ct);
    }

    private async Task SaveProfileAsync(Guid customerId, Garment garment, string measurements, CancellationToken ct)
    {
        var profile = await db.Set<TailorMeasurement>().SingleOrDefaultAsync(m => m.CustomerId == customerId && m.Garment == garment, ct);
        if (profile is null) db.Add(new TailorMeasurement { CustomerId = customerId, Garment = garment, Measurements = measurements });
        else profile.Measurements = measurements;
    }

    private async Task<TailoringOrder> FindAsync(Guid id, CancellationToken ct, bool tracking = true)
    {
        var q = db.Set<TailoringOrder>().Include(o => o.Lines).Where(o => o.Id == id && o.CompanyId == tenant.RequireCompanyId());
        return await (tracking ? q : q.AsNoTracking()).SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Tailoring order", id);
    }

    private TailoringOrderDto ToDto(TailoringOrder o) => new(o.Id, o.OrderNo, o.CustomerId, o.CustomerName, o.Phone, WhatsApp(o.Phone), o.OrderDate, o.FittingDate,
        o.DueDate, o.Garment.ToString(), Parse(o.Measurements), o.StyleNotes, o.TailorName, o.Status.ToString(), o.Total, o.Paid, o.Total - o.Paid, o.ReadyOn,
        o.DeliveredOn, o.SalesInvoiceId, o.Status is not (TailoringStatus.Ready or TailoringStatus.Delivered or TailoringStatus.Cancelled) && o.DueDate < clock.Today,
        o.Lines.OrderBy(l => l.LineNo).Select(l => new TailoringLineDto(l.LineNo, l.ItemId, l.Description, l.Quantity, l.UnitPrice, l.Amount)).ToList());

    /// <summary>International form for wa.me links (Qatar numbers are 8 digits).</summary>
    internal static string? WhatsApp(string? phone)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        return digits.Length switch { 8 => "974" + digits, > 8 => digits, _ => null };
    }

    private static string Serialize(IReadOnlyDictionary<string, string> m) =>
        JsonSerializer.Serialize(m.Where(x => !string.IsNullOrWhiteSpace(x.Value)).ToDictionary(x => x.Key.Trim()[..Math.Min(x.Key.Trim().Length, 40)],
            x => x.Value.Trim()[..Math.Min(x.Value.Trim().Length, 40)]));

    private static Dictionary<string, string> Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}

internal static class TailoringEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/tailoring").WithTags("Tailoring").RequireAuthorization();
        g.MapGet("/board", (TailoringService s, CancellationToken ct) => s.BoardAsync(ct)).RequirePermission(SalesPermissions.TailoringUse);
        g.MapGet("/orders", (TailoringService s, CancellationToken ct, string? status = "open", string? search = null, Guid? customerId = null) =>
            s.ListAsync(status, search, customerId, ct)).RequirePermission(SalesPermissions.TailoringUse);
        g.MapGet("/orders/{id:guid}", (Guid id, TailoringService s, CancellationToken ct) => s.GetAsync(id, ct)).RequirePermission(SalesPermissions.TailoringUse);
        g.MapPost("/orders", async (TailoringOrderRequest r, TailoringService s, CancellationToken ct) => await s.GetAsync((await s.CreateAsync(r, ct)).Id, ct))
            .RequirePermission(SalesPermissions.TailoringUse);
        g.MapPost("/orders/{id:guid}/payments", async (Guid id, TailoringPaymentRequest r, TailoringService s, CancellationToken ct) =>
            await s.GetAsync((await s.AddPaymentAsync(id, r, ct)).Id, ct)).RequirePermission(SalesPermissions.TailoringUse);
        g.MapPost("/orders/{id:guid}/status", async (Guid id, TailoringStatusRequest r, TailoringService s, CancellationToken ct) =>
            await s.GetAsync((await s.SetStatusAsync(id, r, ct)).Id, ct)).RequirePermission(SalesPermissions.TailoringUse);
        g.MapPost("/orders/{id:guid}/deliver", async (Guid id, DeliverRequest r, TailoringService s, CancellationToken ct) =>
            await s.GetAsync((await s.DeliverAsync(id, r, ct)).Id, ct)).RequirePermission(SalesPermissions.TailoringUse);
        g.MapPost("/orders/{id:guid}/cancel", async (Guid id, TailoringService s, CancellationToken ct) => await s.GetAsync((await s.CancelAsync(id, ct)).Id, ct))
            .RequirePermission(SalesPermissions.TailoringUse);
        g.MapGet("/customers/{customerId:guid}/measurements", (Guid customerId, TailoringService s, CancellationToken ct) => s.ProfilesAsync(customerId, ct))
            .RequirePermission(SalesPermissions.TailoringUse);
    }
}
