using Erp.SharedKernel.Domain;

namespace Erp.Modules.Purchasing.Domain;

public static class PurchasingRules
{
    /// <summary>
    /// Withholding tax rates on gross payments to non-residents without a PE (Income Tax Law No. 24 of 2018, art. 20):
    /// 5% on royalties and technical fees; 7% on interest, commissions, brokerage, directors' and attendance fees and other
    /// services performed in Qatar. Treaty relief is not applied automatically.
    /// </summary>
    public static decimal WhtRate(WhtCategory category) => category switch
    {
        WhtCategory.None => 0m,
        WhtCategory.Royalties or WhtCategory.TechnicalFees => 5m,
        _ => 7m
    };

    /// <summary>Splits an amount in proportion to the bases so that the parts add up exactly (largest remainder, to the dirham).</summary>
    public static decimal[] Allocate(decimal amount, IReadOnlyList<decimal> bases)
    {
        var total = bases.Sum();
        if (bases.Count == 0 || total <= 0)
            throw new BusinessRuleException("pur.allocation_basis", "There is nothing to allocate the charges to.", "لا يوجد أساس لتوزيع التكاليف.");
        var cents = (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);
        var exact = bases.Select(b => cents * b / total).ToArray();
        var parts = exact.Select(e => (long)Math.Floor(e)).ToArray();
        var remainder = cents - parts.Sum();
        foreach (var i in exact.Select((e, i) => (Fraction: e - Math.Floor(e), Index: i)).OrderByDescending(x => x.Fraction).ThenBy(x => x.Index)
                     .Take((int)remainder).Select(x => x.Index))
            parts[i]++;
        return parts.Select(p => p / 100m).ToArray();
    }

    /// <summary>True when the invoiced unit price does not exceed the receipt cost by more than the tolerance.</summary>
    public static bool WithinTolerance(decimal invoicedUnitPrice, decimal receiptUnitCost, decimal tolerancePercent) =>
        invoicedUnitPrice <= receiptUnitCost * (1 + tolerancePercent / 100m) + 0.000001m;

    /// <summary>WHT withheld on a part payment: pro rata, with the last payment taking the remainder.</summary>
    public static decimal WhtOnPayment(decimal invoiceTotal, decimal invoiceWht, decimal alreadyWithheld, decimal openBefore, decimal paying)
    {
        if (invoiceWht == 0) return 0;
        return paying >= openBefore ? invoiceWht - alreadyWithheld : Money.Round(invoiceWht * paying / invoiceTotal);
    }
}
