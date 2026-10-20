using System.Text.Json;
using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Inventory.Domain;
using Erp.SharedKernel.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Inventory.Endpoints;

public sealed record SeasonEvent(string Key, string NameEn, string NameAr, DateOnly Date);

public sealed record SeasonalDemandRow(Guid ItemId, string Sku, string NameEn, string NameAr, decimal OnHand, decimal DailyRecent, decimal LastYearWindow,
    decimal Factor, bool HasHistory, decimal Forecast, decimal SuggestedExtra);

public sealed record SeasonalDemandReport(DateOnly From, DateOnly To, SeasonEvent? Event, IReadOnlyList<SeasonEvent> Upcoming, IReadOnlyList<SeasonalDemandRow> Rows);

/// <summary>
/// Shopping peaks in Qatar. Islamic dates follow the moon, so these are estimates (±1–2 days); the forecast only uses them to line up last
/// year's same season, so a day's difference does not matter.
/// </summary>
public static class QatarSeasons
{
    public static readonly IReadOnlyList<SeasonEvent> All =
    [
        new("ramadan", "Ramadan", "رمضان", new(2025, 3, 1)), new("eid_fitr", "Eid al-Fitr", "عيد الفطر", new(2025, 3, 30)),
        new("eid_adha", "Eid al-Adha", "عيد الأضحى", new(2025, 6, 6)),
        new("ramadan", "Ramadan", "رمضان", new(2026, 2, 18)), new("eid_fitr", "Eid al-Fitr", "عيد الفطر", new(2026, 3, 20)),
        new("eid_adha", "Eid al-Adha", "عيد الأضحى", new(2026, 5, 27)),
        new("ramadan", "Ramadan", "رمضان", new(2027, 2, 8)), new("eid_fitr", "Eid al-Fitr", "عيد الفطر", new(2027, 3, 10)),
        new("eid_adha", "Eid al-Adha", "عيد الأضحى", new(2027, 5, 16)),
        new("ramadan", "Ramadan", "رمضان", new(2028, 1, 28)), new("eid_fitr", "Eid al-Fitr", "عيد الفطر", new(2028, 2, 27)),
        new("eid_adha", "Eid al-Adha", "عيد الأضحى", new(2028, 5, 5)),
        .. Enumerable.Range(2025, 4).SelectMany(y => new SeasonEvent[]
        {
            new("national_day", "Qatar National Day", "اليوم الوطني", new(y, 12, 18)),
            new("school", "Back to school", "العودة إلى المدارس", new(y, 8, 25)),
            new("summer", "Summer travel season", "موسم السفر الصيفي", new(y, 6, 20))
        })
    ];
}

/// <summary>
/// How much of each item will sell in the coming days: the recent daily rate, scaled by how last year's same season compared with the weeks
/// before it (lined up on Ramadan/Eid when one is coming). Sales are read from stock movements (invoices, deliveries, till, returns).
/// </summary>
internal sealed class SeasonalDemandService(ErpDbContext db, ITenantContext tenant, IClock clock)
{
    private static readonly string[] SaleSources = ["sales_invoice", "delivery_note", "pos_sale", "credit_note"];
    private const int Baseline = 56;

    public async Task<SeasonalDemandReport> BuildAsync(int horizonDays, CancellationToken ct)
    {
        var horizon = Math.Clamp(horizonDays, 7, 90);
        var today = clock.Today;
        var start = today.AddDays(1);
        var end = today.AddDays(horizon);
        var upcoming = QatarSeasons.All.Where(e => e.Date >= today && e.Date <= today.AddDays(horizon + 14)).OrderBy(e => e.Date).ToList();
        var season = upcoming.FirstOrDefault(e => e.Key is "ramadan" or "eid_fitr" or "eid_adha") ?? upcoming.FirstOrDefault();
        var previous = season is null ? null : QatarSeasons.All.Where(e => e.Key == season.Key && e.Date < season.Date).MaxBy(e => e.Date);
        var shift = previous is null ? 364 : season!.Date.DayNumber - previous.Date.DayNumber;
        var lyStart = start.AddDays(-shift);
        var lyEnd = end.AddDays(-shift);
        var historyFrom = lyStart.AddDays(-Baseline);
        var recentFrom = today.AddDays(-Baseline + 1);

        var companyId = tenant.RequireCompanyId();
        var daily = await db.Set<StockMovement>().AsNoTracking()
            .Where(m => m.CompanyId == companyId && SaleSources.Contains(m.SourceType) && m.MovementDate >= historyFrom && m.MovementDate <= today)
            .GroupBy(m => new { m.ItemId, m.MovementDate }).Select(g => new { g.Key.ItemId, g.Key.MovementDate, Sold = -g.Sum(m => m.Quantity) }).ToListAsync(ct);
        var itemIds = daily.Select(d => d.ItemId).Distinct().ToList();
        var items = await db.Set<Item>().AsNoTracking().Where(i => itemIds.Contains(i.Id) && i.IsActive && i.ItemType == ItemType.Stock).ToListAsync(ct);
        var onHand = await db.Set<ItemValuation>().AsNoTracking().Where(v => itemIds.Contains(v.ItemId)).ToDictionaryAsync(v => v.ItemId, v => v.Quantity, ct);

        var rows = new List<SeasonalDemandRow>();
        foreach (var item in items)
        {
            var sales = daily.Where(d => d.ItemId == item.Id).ToList();
            decimal Sold(DateOnly from, DateOnly to) => sales.Where(d => d.MovementDate >= from && d.MovementDate <= to).Sum(d => d.Sold);
            var rate = Sold(recentFrom, today) / Baseline;
            var lyWindow = Sold(lyStart, lyEnd);
            var lyBefore = Sold(historyFrom, lyStart.AddDays(-1)) / Baseline * horizon;
            var hasHistory = lyWindow > 0 && lyBefore > 0;
            var factor = hasHistory ? Math.Clamp(lyWindow / lyBefore, 0.5m, 4m) : 1m;
            var forecast = Math.Round(rate * horizon * factor, 1);
            if (forecast <= 0) continue;
            var have = onHand.GetValueOrDefault(item.Id);
            rows.Add(new SeasonalDemandRow(item.Id, item.Sku, item.NameEn, item.NameAr, have, Math.Round(rate, 2), lyWindow, Math.Round(factor, 2), hasHistory,
                forecast, Math.Max(0, Math.Ceiling(forecast - have))));
        }
        return new SeasonalDemandReport(start, end, season, upcoming,
            rows.OrderByDescending(r => r.SuggestedExtra > 0).ThenByDescending(r => r.Factor).ThenByDescending(r => r.Forecast).Take(100).ToList());
    }
}

/// <summary>What to stock up on before the coming season, for the AI assistant.</summary>
internal sealed class SeasonalDemandAiTool(SeasonalDemandService demand) : IAiTool
{
    public string Name => "seasonal_demand";
    public string Description =>
        "Forecast of what each stock item will sell in the coming days, using recent sales and last year's same season (Ramadan, Eid, National Day, back to school), with the extra quantity to buy beyond stock on hand.";
    public string Permission => InventoryPermissions.ReportsRead;
    public string? Module => ModuleCodes.Inventory;
    public string ParametersSchema => """{"type":"object","properties":{"horizon_days":{"type":"integer","description":"how many days ahead, default 30"}}}""";

    public async Task<object> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var r = await demand.BuildAsync(AiArgs.Int(args, "horizon_days", 30, 7, 90), ct);
        return new
        {
            r.From, r.To, season = r.Event?.NameEn, season_date = r.Event?.Date, upcoming = r.Upcoming.Select(e => new { e.NameEn, e.Date }),
            items = r.Rows.Take(25).Select(x => new
            {
                item = x.NameEn, item_ar = x.NameAr, on_hand = x.OnHand, sold_per_day_recently = x.DailyRecent, season_factor = x.Factor,
                has_last_year_history = x.HasHistory, forecast = x.Forecast, buy_extra = x.SuggestedExtra
            })
        };
    }
}

internal static class SeasonalDemandEndpoints
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/inventory/reports/seasonal-demand", (SeasonalDemandService s, CancellationToken ct, int horizonDays = 30) => s.BuildAsync(horizonDays, ct))
            .WithTags("Inventory").RequireAuthorization().RequirePermission(InventoryPermissions.ReportsRead);
}
