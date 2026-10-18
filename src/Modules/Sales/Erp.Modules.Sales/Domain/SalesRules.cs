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
