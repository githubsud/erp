namespace Erp.SharedKernel.Application;

/// <summary>One figure on the home dashboard. <see cref="Format"/>: "money" or "count"; <see cref="Tone"/>: null, "ok", "warn" or "bad".</summary>
public sealed record DashboardTile(string Section, string Key, string LabelEn, string LabelAr, decimal Value, string Format, string Link,
    string? HintEn = null, string? HintAr = null, string? Tone = null);

/// <summary>A module's dashboard figures, shown to users holding <see cref="Permission"/>.</summary>
public interface IDashboardProvider
{
    string Permission { get; }

    Task<IReadOnlyList<DashboardTile>> GetTilesAsync(CancellationToken ct);
}
