namespace Erp.SharedKernel.Domain;

public static class Money
{
    public const string BaseCurrency = "QAR";

    /// <summary>Commercial rounding (half away from zero), 2 decimals for QAR by default.</summary>
    public static decimal Round(decimal value, int decimals = 2) =>
        Math.Round(value, decimals, MidpointRounding.AwayFromZero);
}
