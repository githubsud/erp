using Erp.Infrastructure.Persistence;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Sales.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Sales.Documents;

public sealed record CustomerRequest(string Code, string? NameEn, string? NameAr, string? Country, string? TaxCardNo, string? CrNo, string? Email,
    string? Phone, string? AddressEn, string? AddressAr, int PaymentTermsDays = 30, decimal? CreditLimit = null, Guid? PriceListId = null,
    Guid? SalespersonUserId = null, bool IsActive = true, string? Currency = null);

public sealed record SalesSettingsRequest(CreditControl CreditControl, int QuotationValidityDays);

public sealed record PriceListItemRequest(Guid ItemId, decimal UnitPrice);

public sealed record PriceListRequest(string Code, string? NameEn, string? NameAr, bool IsActive, IReadOnlyList<PriceListItemRequest>? Items);

public sealed record ReverseRequest(DateOnly Date, string Reason);

internal sealed class CustomerService(ErpDbContext db, ITenantContext tenant, IInventoryQueries inventory)
{
    public async Task<Customer> SaveAsync(Guid? id, CustomerRequest r, CancellationToken ct)
    {
        var (en, ar) = Bilingual.Require(r.NameEn, r.NameAr);
        var country = string.IsNullOrWhiteSpace(r.Country) ? "QA" : r.Country.Trim().ToUpperInvariant();
        if (country.Length != 2)
            throw new BusinessRuleException("sal.country", "Use the two-letter country code (QA, AE, IN…).", "استخدم رمز الدولة المكون من حرفين.");
        if (r.PaymentTermsDays is < 0 or > 365)
            throw new BusinessRuleException("sal.terms", "Payment terms must be between 0 and 365 days.", "يجب أن تكون مدة السداد بين 0 و365 يوماً.");
        if (r.CreditLimit is < 0)
            throw new BusinessRuleException("sal.credit_limit", "The credit limit cannot be negative.", "لا يمكن أن يكون حد الائتمان سالباً.");
        var companyId = tenant.RequireCompanyId();
        if (r.PriceListId is { } priceListId && !await db.Set<PriceList>().AnyAsync(p => p.Id == priceListId && p.CompanyId == companyId, ct))
            throw new NotFoundException("Price list", priceListId);

        var customer = id is null
            ? db.Add(new Customer { CompanyId = companyId, Code = "", NameEn = en, NameAr = ar }).Entity
            : await db.Set<Customer>().SingleOrDefaultAsync(c => c.Id == id && c.CompanyId == companyId, ct)
              ?? throw new NotFoundException("Customer", id);
        customer.Code = Guard.Required(r.Code, "code").ToUpperInvariant();
        customer.NameEn = en;
        customer.NameAr = ar;
        customer.Country = country;
        customer.TaxCardNo = Clean(r.TaxCardNo);
        customer.CrNo = Clean(r.CrNo);
        customer.Email = Clean(r.Email)?.ToLowerInvariant();
        customer.Phone = Clean(r.Phone);
        customer.AddressEn = Clean(r.AddressEn);
        customer.AddressAr = Clean(r.AddressAr);
        customer.PaymentTermsDays = r.PaymentTermsDays;
        customer.Currency = SalesRules.CurrencyCode(r.Currency);
        customer.CreditLimit = r.CreditLimit is { } limit ? Money.Round(limit) : null;
        customer.PriceListId = r.PriceListId;
        customer.SalespersonUserId = r.SalespersonUserId;
        customer.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return customer;
    }

    public async Task<SalesSettings> SaveSettingsAsync(SalesSettingsRequest r, CancellationToken ct)
    {
        if (r.QuotationValidityDays is < 1 or > 365)
            throw new BusinessRuleException("sal.validity", "Quotation validity must be between 1 and 365 days.", "يجب أن تكون صلاحية عرض السعر بين 1 و365 يوماً.");
        var settings = await db.Set<SalesSettings>().SingleAsync(s => s.CompanyId == tenant.RequireCompanyId(), ct);
        settings.CreditControl = r.CreditControl;
        settings.QuotationValidityDays = r.QuotationValidityDays;
        await db.SaveChangesAsync(ct);
        return settings;
    }

    public Task<PriceList> SavePriceListAsync(Guid? id, PriceListRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var (en, ar) = Bilingual.Require(r.NameEn, r.NameAr);
            var companyId = tenant.RequireCompanyId();
            var list = id is null
                ? db.Add(new PriceList { CompanyId = companyId, Code = "", NameEn = en, NameAr = ar }).Entity
                : await db.Set<PriceList>().Include(p => p.Items).SingleOrDefaultAsync(p => p.Id == id && p.CompanyId == companyId, token)
                  ?? throw new NotFoundException("Price list", id);
            list.Code = Guard.Required(r.Code, "code").ToUpperInvariant();
            list.NameEn = en;
            list.NameAr = ar;
            list.IsActive = r.IsActive;

            var requested = r.Items ?? [];
            if (requested.Select(i => i.ItemId).Distinct().Count() != requested.Count)
                throw new BusinessRuleException("sal.price_duplicate", "Each item can appear only once in a price list.", "يجب ألا يتكرر الصنف في قائمة الأسعار.");
            var items = await inventory.GetItemsAsync(requested.Select(i => i.ItemId), token);
            db.RemoveRange(list.Items);
            list.Items.Clear();
            await db.SaveChangesAsync(token);
            foreach (var i in requested)
            {
                if (!items.ContainsKey(i.ItemId)) throw new NotFoundException("Item", i.ItemId);
                var line = new PriceListItem { PriceListId = list.Id, ItemId = i.ItemId, UnitPrice = Guard.NonNegative(i.UnitPrice, "unitPrice") };
                list.Items.Add(line);
                db.Add(line);
            }
            await db.SaveChangesAsync(token);
            return list;
        }, ct);

    /// <summary>The price a customer pays for each item: its price list first, then the item's own sales price.</summary>
    /// <summary>
    /// Price-list prices are in the document's currency; the item's own sales price is in QAR and is converted at
    /// <paramref name="exchangeRate"/> for foreign-currency documents.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, decimal?>> PricesAsync(Guid? customerId, IReadOnlyCollection<Guid> itemIds, CancellationToken ct,
        decimal exchangeRate = 1m)
    {
        var items = await inventory.GetItemsAsync(itemIds, ct);
        Dictionary<Guid, decimal> listed = [];
        if (customerId is { } cid)
        {
            var priceListId = await db.Set<Customer>().AsNoTracking().Where(c => c.Id == cid && c.CompanyId == tenant.RequireCompanyId())
                .Select(c => c.PriceListId).SingleOrDefaultAsync(ct);
            if (priceListId is { } pl)
                listed = await (from i in db.Set<PriceListItem>().AsNoTracking()
                                join p in db.Set<PriceList>() on i.PriceListId equals p.Id
                                where i.PriceListId == pl && p.IsActive && itemIds.Contains(i.ItemId)
                                select i).ToDictionaryAsync(i => i.ItemId, i => i.UnitPrice, ct);
        }
        return itemIds.Distinct().ToDictionary(id => id,
            id => listed.TryGetValue(id, out var price) ? (decimal?)price
                : items.TryGetValue(id, out var item) && item.SalesPrice is { } qar ? Money.Round(qar / exchangeRate) : null);
    }

    internal static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static async Task<Customer> ActiveCustomerAsync(ErpDbContext db, ITenantContext tenant, Guid id, CancellationToken ct)
    {
        var customer = await db.Set<Customer>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == id && c.CompanyId == tenant.RequireCompanyId(), ct)
                       ?? throw new NotFoundException("Customer", id);
        if (!customer.IsActive)
            throw new BusinessRuleException("sal.customer_inactive", $"Customer {customer.Code} is inactive.", $"العميل {customer.Code} غير نشط.");
        return customer;
    }
}

public sealed record CustomerExposure(Guid CustomerId, decimal Receivable, decimal ChequesInHand, decimal OpenOrders, decimal Exposure, decimal? CreditLimit,
    decimal? Available);

/// <summary>
/// Credit exposure = receivable balance (ledger) + post-dated cheques not yet cleared + confirmed orders not yet invoiced.
/// </summary>
internal sealed class CreditControlService(ErpDbContext db, ITenantContext tenant, IFinanceQueries finance, IClock clock, IPermissionService permissions)
{
    public async Task<CustomerExposure> ExposureAsync(Guid customerId, Guid? excludingOrderId, CancellationToken ct)
    {
        var customer = await db.Set<Customer>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == customerId && c.CompanyId == tenant.RequireCompanyId(), ct)
                       ?? throw new NotFoundException("Customer", customerId);
        var receivable = await finance.GetPartyBalanceAsync(AccountRoles.AccountsReceivable, PartyType.Customer, customerId, clock.Today.AddYears(10), ct);
        var cheques = await db.Set<CustomerReceipt>().AsNoTracking()
            .Where(r => r.CustomerId == customerId && r.Status == ReceiptStatus.PdcHeld).SumAsync(r => r.AmountBase, ct);
        var lines = await (from l in db.Set<SalesOrderLine>().AsNoTracking()
                           join o in db.Set<SalesOrder>() on l.SalesOrderId equals o.Id
                           where o.CustomerId == customerId && o.Id != excludingOrderId && l.QuantityInvoiced < l.Quantity
                                 && (o.Status == SalesOrderStatus.Confirmed || o.Status == SalesOrderStatus.PartiallyDelivered ||
                                     o.Status == SalesOrderStatus.Delivered)
                           select new { l.Quantity, l.QuantityInvoiced, l.Amount, o.ExchangeRate }).ToListAsync(ct);
        var orders = lines.Sum(l => Money.Round(l.Amount * (l.Quantity - l.QuantityInvoiced) / l.Quantity * l.ExchangeRate));
        var exposure = receivable + cheques + orders;
        return new CustomerExposure(customerId, receivable, cheques, orders, exposure, customer.CreditLimit,
            customer.CreditLimit is { } limit ? limit - exposure : null);
    }

    public async Task<CreditDecision> CheckAsync(Customer customer, decimal adding, Guid? excludingOrderId, CancellationToken ct)
    {
        var settings = await db.Set<SalesSettings>().AsNoTracking().SingleAsync(s => s.CompanyId == customer.CompanyId, ct);
        if (settings.CreditControl == CreditControl.None || customer.CreditLimit is null)
            return new CreditDecision(CreditOutcome.Allowed, 0, customer.CreditLimit);
        var exposure = await ExposureAsync(customer.Id, excludingOrderId, ct);
        return SalesRules.Check(settings.CreditControl, customer.CreditLimit, exposure.Exposure, adding);
    }

    /// <summary>Refuses a document over the limit under the "block" policy unless the user may release credit.</summary>
    public async Task EnsureAllowedAsync(Customer customer, decimal adding, CancellationToken ct)
    {
        var decision = await CheckAsync(customer, adding, null, ct);
        if (decision.Outcome == CreditOutcome.Hold && !await permissions.HasAsync(SalesPermissions.CreditApprove, ct))
            throw OverLimit(customer, decision);
    }

    public static BusinessRuleException OverLimit(Customer customer, CreditDecision d) =>
        new("sal.credit_limit_exceeded",
            $"{customer.NameEn} would owe {d.Exposure:N2}, above the credit limit of {d.Limit:N2}. A manager must release it.",
            $"سيصبح رصيد {customer.NameAr} {d.Exposure:N2} متجاوزاً حد الائتمان {d.Limit:N2}. يلزم اعتماد المدير.");

    public static string WarningText(CreditDecision d) =>
        $"Over credit limit: exposure {d.Exposure:N2} / limit {d.Limit:N2} · تجاوز حد الائتمان: {d.Exposure:N2} / {d.Limit:N2}";
}

/// <summary>Collects signed amounts per account role and key, then emits balanced debit/credit posting lines.</summary>
internal sealed class PostingBuilder(Guid? projectId = null)
{
    private readonly Dictionary<(string Role, Guid? Key, Guid? Party), decimal> _amounts = [];

    public void Add(string role, Guid? key, decimal signedDebit, Guid? customerId = null)
    {
        if (signedDebit == 0) return;
        _amounts[(role, key, customerId)] = _amounts.GetValueOrDefault((role, key, customerId)) + signedDebit;
    }

    public List<PostingLine> Lines() => _amounts.Where(x => x.Value != 0).Select(x => new PostingLine
    {
        AccountRole = x.Key.Role,
        RoleKey = x.Key.Key,
        Side = x.Value > 0 ? PostingSide.Debit : PostingSide.Credit,
        Amount = Math.Abs(x.Value),
        PartyType = x.Key.Party is null ? null : PartyType.Customer,
        PartyId = x.Key.Party,
        ProjectId = x.Key.Party is null ? projectId : null
    }).ToList();
}
