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

    /// <summary>The document currency: QAR when not given, otherwise a three-letter ISO code.</summary>
    public static string CurrencyCode(string? currency)
    {
        var code = string.IsNullOrWhiteSpace(currency) ? Money.BaseCurrency : currency.Trim().ToUpperInvariant();
        if (code.Length != 3 || !code.All(char.IsAsciiLetterUpper))
            throw new BusinessRuleException("pur.currency", "Use a three-letter currency code (QAR, USD, EUR…).", "استخدم رمز عملة من ثلاثة أحرف.");
        return code;
    }

    /// <summary>QAR per unit of the document currency: 1 for QAR, otherwise required and positive.</summary>
    public static decimal ExchangeRate(string currency, decimal? rate)
    {
        if (currency == Money.BaseCurrency) return 1m;
        if (rate is not > 0)
            throw new BusinessRuleException("pur.exchange_rate", $"Enter the exchange rate from {currency} to QAR.", $"أدخل سعر صرف {currency} مقابل الريال القطري.");
        return Math.Round(rate.Value, 6, MidpointRounding.AwayFromZero);
    }

    public static decimal ToBase(decimal amount, decimal rate) => Money.Round(amount * rate);

    /// <summary>
    /// The QAR a settlement clears from a document: the whole remaining QAR balance when it settles the document (so no cents
    /// are left behind), otherwise the amount at the document's rate.
    /// </summary>
    public static decimal BaseCleared(decimal amount, decimal open, decimal openBase, decimal rate) => amount >= open ? openBase : ToBase(amount, rate);

    /// <summary>WHT withheld on a part payment: pro rata, with the last payment taking the remainder.</summary>
    public static decimal WhtOnPayment(decimal invoiceTotal, decimal invoiceWht, decimal alreadyWithheld, decimal openBefore, decimal paying)
    {
        if (invoiceWht == 0) return 0;
        return paying >= openBefore ? invoiceWht - alreadyWithheld : Money.Round(invoiceWht * paying / invoiceTotal);
    }
}
