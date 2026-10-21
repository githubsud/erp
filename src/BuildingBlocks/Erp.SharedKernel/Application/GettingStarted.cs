namespace Erp.SharedKernel.Application;

/// <summary>One step of a new company's "Getting started" list, done when the module sees the data it needs.</summary>
public sealed record GettingStartedStep(string Key, int Order, string TitleEn, string TitleAr, string HintEn, string HintAr, string Link, bool Done);

/// <summary>A module's first-day steps, shown to users holding <see cref="Permission"/> while the module is on.</summary>
public interface IGettingStartedProvider
{
    string Permission { get; }

    /// <summary>The optional module the steps belong to (null: always shown); hidden when it is off for the company.</summary>
    string? Module => null;

    Task<IReadOnlyList<GettingStartedStep>> GetStepsAsync(CancellationToken ct);
}
