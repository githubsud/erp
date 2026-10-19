namespace Erp.SharedKernel.Application;

/// <summary>Optional modules a company can switch off. Core and Finance are always on.</summary>
public static class ModuleCodes
{
    public const string Hr = "hr";
    public const string Inventory = "inventory";
    public const string Purchasing = "purchasing";
    public const string Sales = "sales";

    public static readonly IReadOnlyList<string> Optional = [Hr, Inventory, Purchasing, Sales];
}

/// <summary>Which optional modules the signed-in company has switched off.</summary>
public interface IModuleAccess
{
    Task<IReadOnlySet<string>> DisabledAsync(CancellationToken ct = default);
}
