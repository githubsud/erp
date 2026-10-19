namespace Erp.SharedKernel.Application;

/// <summary>
/// Modules a customer can be licensed for (by the platform) and switch on or off (by the company). Core and Finance are always on.
/// </summary>
public static class ModuleCodes
{
    public const string Hr = "hr";
    public const string Ess = "ess";
    public const string Inventory = "inventory";
    public const string Purchasing = "purchasing";
    public const string Sales = "sales";
    public const string Pos = "pos";
    public const string Crm = "crm";
    public const string Projects = "projects";
    public const string Subcontracts = "subcontracts";
    public const string Assets = "assets";
    public const string Budgets = "budgets";
    public const string Tailoring = "tailoring";
    public const string Salon = "salon";

    public static readonly IReadOnlyList<string> Optional =
        [Hr, Ess, Inventory, Purchasing, Sales, Pos, Crm, Projects, Subcontracts, Assets, Budgets, Tailoring, Salon];

    /// <summary>A module only works together with these.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Requires = new Dictionary<string, string[]>
    {
        [Ess] = [Hr], [Purchasing] = [Inventory], [Sales] = [Inventory], [Pos] = [Inventory], [Crm] = [Sales], [Projects] = [Sales],
        [Subcontracts] = [Purchasing], [Tailoring] = [Sales], [Salon] = [Sales]
    };

    public static HashSet<string> Parse(string? raw) =>
        (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(m => Optional.Contains(m)).ToHashSet();
}

/// <summary>Which optional modules are off for the signed-in company (not licensed, or switched off), and whether the account is suspended.</summary>
public interface IModuleAccess
{
    Task<IReadOnlySet<string>> DisabledAsync(CancellationToken ct = default);

    /// <summary>Modules the customer's subscription includes.</summary>
    Task<IReadOnlySet<string>> LicensedAsync(CancellationToken ct = default);

    /// <summary>A suspended customer can still sign in and read (and export) data, but not change anything.</summary>
    Task<bool> SuspendedAsync(CancellationToken ct = default);
}
