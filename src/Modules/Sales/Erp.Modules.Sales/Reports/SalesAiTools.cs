using System.Text.Json;
using Erp.Infrastructure.Persistence;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Sales.Domain;
using Erp.Modules.Sales.Endpoints;
using Erp.Modules.Sales.Pos;
using Erp.Modules.Sales.Tailoring;
using Erp.Modules.Sales.Workshop;
using Erp.SharedKernel.Application;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Sales.Reports;

/// <summary>Who owes the business money (receivables aging), for the AI assistant.</summary>
internal sealed class CustomerBalancesAiTool(ErpDbContext db, ITenantContext tenant, IFinanceQueries finance, IClock clock) : IAiTool
{
    public string Name => "customer_balances";
    public string Description => "Customers who owe money, largest balance first: total owed, amount overdue more than 30 days, credit limit. Also the total receivables.";
    public string Permission => SalesPermissions.ReportsRead;
    public string? Module => ModuleCodes.Sales;
    public string ParametersSchema => """{"type":"object","properties":{"top":{"type":"integer","description":"how many customers, default 10"}}}""";

    public async Task<object> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var aging = await SalesEndpoints.AgingAsync(db, tenant, finance, clock, ct);
        var top = AiArgs.Int(args, "top", 10, 1, 50);
        return new
        {
            as_of = aging.AsOf,
            total_receivables = aging.Total,
            customers_owing = aging.Rows.Count(r => r.Total > 0),
            customers = aging.Rows.Where(r => r.Total > 0).Take(top).Select(r => new
            {
                customer = r.NameEn, customer_ar = r.NameAr, owed = r.Total, overdue_over_30_days = r.Days31To60 + r.Days61To90 + r.Over90,
                credit_limit = r.CreditLimit
            })
        };
    }
}

/// <summary>Sales for a period from invoices and the tills, with the best-selling items and the biggest customers.</summary>
internal sealed class SalesSummaryAiTool(ErpDbContext db, ITenantContext tenant, IInventoryQueries inventory, IClock clock) : IAiTool
{
    public string Name => "sales_summary";
    public string Description =>
        "Sales for a period (invoices and point-of-sale tills): total, number of sales, sales per day, best-selling items and biggest customers. Defaults to today.";
    public string Permission => SalesPermissions.ReportsRead;
    public string? Module => ModuleCodes.Sales;
    public string ParametersSchema => """
        {"type":"object","properties":{"from":{"type":"string","description":"yyyy-MM-dd"},"to":{"type":"string","description":"yyyy-MM-dd"},
         "top":{"type":"integer","description":"how many items and customers, default 5"}}}
        """;

    public async Task<object> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var today = clock.Today;
        var periodStart = AiArgs.Date(args, "from", today);
        var periodEnd = AiArgs.Date(args, "to", today);
        var top = AiArgs.Int(args, "top", 5, 1, 20);
        var companyId = tenant.RequireCompanyId();
        // Invoices raised from a till are counted with the till.
        var invoices = await (from i in db.Set<SalesInvoice>().AsNoTracking()
                              join c in db.Set<Customer>() on i.CustomerId equals c.Id
                              where i.CompanyId == companyId && i.InvoiceDate >= periodStart && i.InvoiceDate <= periodEnd && i.Status != InvoiceStatus.Draft
                                    && i.Status != InvoiceStatus.Reversed && !db.Set<PosSale>().Any(p => p.SalesInvoiceId == i.Id)
                              select new { i.Id, i.InvoiceDate, i.TotalBase, c.NameEn, c.NameAr }).ToListAsync(ct);
        var invoiceIds = invoices.Select(i => i.Id).ToList();
        var invoiceLines = await db.Set<SalesInvoiceLine>().AsNoTracking().Where(l => invoiceIds.Contains(l.SalesInvoiceId) && l.ItemId != null)
            .Select(l => new { ItemId = l.ItemId!.Value, l.Quantity, l.Amount }).ToListAsync(ct);
        var registers = await db.Set<PosRegister>().AsNoTracking().Where(r => r.CompanyId == companyId).Select(r => r.Id).ToListAsync(ct);
        var shifts = await db.Set<PosShift>().AsNoTracking().Where(s => registers.Contains(s.RegisterId) && s.BusinessDate >= periodStart && s.BusinessDate <= periodEnd)
            .Select(s => new { s.Id, s.BusinessDate }).ToListAsync(ct);
        var shiftDates = shifts.ToDictionary(s => s.Id, s => s.BusinessDate);
        var shiftIds = shiftDates.Keys.ToList();
        var tillSales = await db.Set<PosSale>().AsNoTracking().Where(s => shiftIds.Contains(s.ShiftId))
            .Select(s => new { s.Id, s.ShiftId, s.Total, s.RefundOfId, s.CustomerName }).ToListAsync(ct);
        var tillIds = tillSales.Select(s => s.Id).ToList();
        var tillLines = await db.Set<PosSaleLine>().AsNoTracking().Where(l => tillIds.Contains(l.PosSaleId))
            .Select(l => new { l.ItemId, l.Quantity, l.Amount }).ToListAsync(ct);

        var lines = invoiceLines.Concat(tillLines).GroupBy(l => l.ItemId)
            .Select(g => new { ItemId = g.Key, Quantity = g.Sum(x => x.Quantity), Amount = g.Sum(x => x.Amount) }).OrderByDescending(x => x.Amount).Take(top).ToList();
        var items = await inventory.GetItemsAsync(lines.Select(l => l.ItemId), ct);
        var byDay = invoices.Select(i => (Date: i.InvoiceDate, Amount: i.TotalBase))
            .Concat(tillSales.Select(s => (Date: shiftDates[s.ShiftId], Amount: s.Total)))
            .GroupBy(x => x.Date).OrderBy(g => g.Key).Select(g => new { date = g.Key, total = g.Sum(x => x.Amount) }).ToList();
        return new
        {
            from = periodStart, to = periodEnd,
            total = invoices.Sum(i => i.TotalBase) + tillSales.Sum(s => s.Total),
            invoice_sales = invoices.Sum(i => i.TotalBase),
            till_sales = tillSales.Where(s => s.RefundOfId == null).Sum(s => s.Total),
            till_refunds = -tillSales.Where(s => s.RefundOfId != null).Sum(s => s.Total),
            number_of_sales = invoices.Count + tillSales.Count(s => s.RefundOfId == null),
            per_day = byDay.Count <= 31 ? byDay : null,
            best_selling_items = lines.Select(l => new
            {
                item = items.GetValueOrDefault(l.ItemId)?.NameEn, item_ar = items.GetValueOrDefault(l.ItemId)?.NameAr, quantity = l.Quantity, amount = l.Amount
            }),
            biggest_customers = invoices.GroupBy(i => (i.NameEn, i.NameAr)).Select(g => new { customer = g.Key.NameEn, customer_ar = g.Key.NameAr, amount = g.Sum(x => x.TotalBase) })
                .OrderByDescending(x => x.amount).Take(top)
        };
    }
}

/// <summary>Tailoring orders: open, due today, late, ready and not collected, deposits held.</summary>
internal sealed class TailoringAiTool(TailoringService tailoring) : IAiTool
{
    public string Name => "tailoring_orders";
    public string Description => "Tailoring and abaya orders: how many are open, due today, late, ready but not collected, deposits held, and each tailor's open work.";
    public string Permission => SalesPermissions.TailoringUse;
    public string? Module => ModuleCodes.Tailoring;

    public async Task<object> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var b = await tailoring.BoardAsync(ct);
        return new
        {
            b.Open, b.DueToday, b.Late, b.ReadyNotCollected, b.DepositsHeld,
            tailors = b.Tailors.Select(t => new { employee = t.Tailor, t.Received, t.InProgress, t.Fitting, t.DueThisWeek, t.Late })
        };
    }
}

/// <summary>Workshop job cards: open, waiting for the customer's approval or for parts, late, ready and not collected.</summary>
internal sealed class WorkshopAiTool(WorkshopService workshop) : IAiTool
{
    public string Name => "workshop_jobs";
    public string Description => "Car workshop job cards: how many cars are in, waiting for the customer's approval, waiting for parts, late, ready but not collected, deposits held, and each technician's open jobs.";
    public string Permission => SalesPermissions.WorkshopUse;
    public string? Module => ModuleCodes.Workshop;

    public async Task<object> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var b = await workshop.BoardAsync(ct);
        return new
        {
            cars_in = b.Open, b.AwaitingApproval, b.WaitingParts, b.Late, b.ReadyNotCollected, b.DepositsHeld,
            technicians = b.Technicians.Select(t => new { employee = t.Technician, t.Open, t.InProgress, t.Late })
        };
    }
}
