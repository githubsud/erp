using Erp.SharedKernel.Domain;

namespace Erp.Modules.Sales.Domain;

public enum CreditOutcome { Allowed, Warn, Hold }

public sealed record CreditDecision(CreditOutcome Outcome, decimal Exposure, decimal? Limit)
{
    public decimal? Available => Limit is { } limit ? limit - Exposure : null;
}

public static class SalesRules
{
    /// <summary>Quantity × unit price less the line discount (percent), rounded to the dirham.</summary>
    public static decimal LineAmount(decimal quantity, decimal unitPrice, decimal discountPercent)
    {
        if (quantity <= 0 || unitPrice < 0)
            throw new BusinessRuleException("sal.line_values", "Quantities must be positive and prices not negative.", "يجب أن تكون الكميات موجبة والأسعار غير سالبة.");
        if (discountPercent is < 0 or > 100)
            throw new BusinessRuleException("sal.discount", "The discount must be between 0% and 100%.", "يجب أن يكون الخصم بين 0% و100%.");
        return Money.Round(quantity * unitPrice * (100m - discountPercent) / 100m);
    }

    /// <summary>The document currency: QAR when not given, otherwise a three-letter ISO code.</summary>
    public static string CurrencyCode(string? currency)
    {
        var code = string.IsNullOrWhiteSpace(currency) ? Money.BaseCurrency : currency.Trim().ToUpperInvariant();
        if (code.Length != 3 || !code.All(char.IsAsciiLetterUpper))
            throw new BusinessRuleException("sal.currency", "Use a three-letter currency code (QAR, USD, EUR…).", "استخدم رمز عملة من ثلاثة أحرف.");
        return code;
    }

    /// <summary>QAR per unit of the document currency: 1 for QAR, otherwise required and positive.</summary>
    public static decimal ExchangeRate(string currency, decimal? rate)
    {
        if (currency == Money.BaseCurrency) return 1m;
        if (rate is not > 0)
            throw new BusinessRuleException("sal.exchange_rate", $"Enter the exchange rate from {currency} to QAR.", $"أدخل سعر صرف {currency} مقابل الريال القطري.");
        return Math.Round(rate.Value, 6, MidpointRounding.AwayFromZero);
    }

    public static decimal ToBase(decimal amount, decimal rate) => Money.Round(amount * rate);

    /// <summary>QAR a settlement clears from an invoice: all the remaining QAR when it settles it, otherwise the amount at the invoice rate.</summary>
    public static decimal BaseCleared(decimal amount, decimal open, decimal openBase, decimal rate) => amount >= open ? openBase : ToBase(amount, rate);

    /// <summary>The discount given on a line: gross (quantity × price) less the net amount.</summary>
    public static decimal LineDiscount(decimal quantity, decimal unitPrice, decimal amount) => Money.Round(quantity * unitPrice) - amount;

    /// <summary>
    /// Credit check: <paramref name="exposure"/> is what the customer already owes or has on order (receivable balance,
    /// cheques not yet cleared, confirmed orders not yet invoiced) and <paramref name="adding"/> the new document.
    /// No limit means no check; a zero limit means cash only.
    /// </summary>
    public static CreditDecision Check(CreditControl policy, decimal? limit, decimal exposure, decimal adding)
    {
        var total = exposure + adding;
        if (policy == CreditControl.None || limit is null || total <= limit) return new CreditDecision(CreditOutcome.Allowed, total, limit);
        return new CreditDecision(policy == CreditControl.Warn ? CreditOutcome.Warn : CreditOutcome.Hold, total, limit);
    }

    /// <summary>Days past due → aging bucket index: 0 current, 1 = 1–30, 2 = 31–60, 3 = 61–90, 4 = over 90.</summary>
    public static int AgingBucket(DateOnly dueDate, DateOnly asOf) => (asOf.DayNumber - dueDate.DayNumber) switch
    {
        <= 0 => 0,
        <= 30 => 1,
        <= 60 => 2,
        <= 90 => 3,
        _ => 4
    };
}
