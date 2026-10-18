using Erp.Modules.Sales.Domain;
using Erp.SharedKernel.Domain;

namespace Erp.UnitTests;

public sealed class SalesRulesTests
{
    [Theory]
    [InlineData(10, 80, 10, 720)]
    [InlineData(3, 33.333, 0, 100)]
    [InlineData(1, 99.99, 100, 0)]
    [InlineData(7, 12.345, 2.5, 84.25)]   // 86.415 × 97.5% = 84.25462…
    public void Line_amount_applies_the_discount_and_rounds_to_the_dirham(decimal quantity, decimal price, decimal discount, decimal expected) =>
        SalesRules.LineAmount(quantity, price, discount).ShouldBe(expected);

    [Fact]
    public void Line_values_are_validated()
    {
        Should.Throw<BusinessRuleException>(() => SalesRules.LineAmount(0, 10, 0)).Code.ShouldBe("sal.line_values");
        Should.Throw<BusinessRuleException>(() => SalesRules.LineAmount(1, -1, 0)).Code.ShouldBe("sal.line_values");
        Should.Throw<BusinessRuleException>(() => SalesRules.LineAmount(1, 10, 101)).Code.ShouldBe("sal.discount");
        SalesRules.LineDiscount(10, 80, 720).ShouldBe(80m);
    }

    [Fact]
    public void Credit_check_follows_the_policy_and_the_limit()
    {
        SalesRules.Check(CreditControl.Block, null, 1_000_000, 5).Outcome.ShouldBe(CreditOutcome.Allowed);     // no limit
        SalesRules.Check(CreditControl.Block, 2_000, 1_500, 500).Outcome.ShouldBe(CreditOutcome.Allowed);      // exactly at the limit
        var held = SalesRules.Check(CreditControl.Block, 2_000, 1_500, 501);
        held.Outcome.ShouldBe(CreditOutcome.Hold);
        held.Exposure.ShouldBe(2_001m);
        held.Available.ShouldBe(-1m);
        SalesRules.Check(CreditControl.Warn, 2_000, 1_500, 501).Outcome.ShouldBe(CreditOutcome.Warn);
        SalesRules.Check(CreditControl.None, 0, 0, 1).Outcome.ShouldBe(CreditOutcome.Allowed);
        SalesRules.Check(CreditControl.Block, 0, 0, 1).Outcome.ShouldBe(CreditOutcome.Hold);                   // cash only
    }

    [Theory]
    [InlineData("2026-10-31", "2026-10-18", 0)]
    [InlineData("2026-10-18", "2026-10-18", 0)]
    [InlineData("2026-10-17", "2026-10-18", 1)]
    [InlineData("2026-09-18", "2026-10-18", 1)]
    [InlineData("2026-09-17", "2026-10-18", 2)]
    [InlineData("2026-07-20", "2026-10-18", 3)]
    [InlineData("2026-07-19", "2026-10-18", 4)]
    public void Aging_buckets_count_days_past_due(string due, string asOf, int bucket) =>
        SalesRules.AgingBucket(DateOnly.Parse(due), DateOnly.Parse(asOf)).ShouldBe(bucket);
}
