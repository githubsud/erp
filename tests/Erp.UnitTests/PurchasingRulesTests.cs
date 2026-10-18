using Erp.Modules.Purchasing.Domain;

namespace Erp.UnitTests;

public sealed class PurchasingRulesTests
{
    [Fact]
    public void Allocation_adds_up_to_the_charge_exactly()
    {
        var parts = PurchasingRules.Allocate(100m, [1m, 1m, 1m]);
        parts.Sum().ShouldBe(100m);
        parts.ShouldBe([33.34m, 33.33m, 33.33m]);

        var byValue = PurchasingRules.Allocate(1234.57m, [600m, 400m, 0.5m]);
        byValue.Sum().ShouldBe(1234.57m);
        byValue[0].ShouldBeGreaterThan(byValue[1]);
    }

    [Fact]
    public void Allocation_needs_a_positive_basis() =>
        Should.Throw<Erp.SharedKernel.Domain.BusinessRuleException>(() => PurchasingRules.Allocate(10m, [0m, 0m]));

    [Theory]
    [InlineData(101, 100, 2, true)]
    [InlineData(102, 100, 2, true)]
    [InlineData(102.01, 100, 2, false)]
    [InlineData(90, 100, 0, true)]   // cheaper than received is never held
    public void Price_tolerance_only_holds_overcharges(decimal invoiced, decimal received, decimal tolerance, bool ok) =>
        PurchasingRules.WithinTolerance(invoiced, received, tolerance).ShouldBe(ok);

    [Theory]
    [InlineData(WhtCategory.Royalties, 5)]
    [InlineData(WhtCategory.TechnicalFees, 5)]
    [InlineData(WhtCategory.Commissions, 7)]
    [InlineData(WhtCategory.OtherServices, 7)]
    [InlineData(WhtCategory.None, 0)]
    public void Wht_rates_follow_the_income_tax_law(WhtCategory category, decimal rate) => PurchasingRules.WhtRate(category).ShouldBe(rate);

    [Fact]
    public void Wht_is_withheld_pro_rata_and_the_last_payment_takes_the_remainder()
    {
        var first = PurchasingRules.WhtOnPayment(1000m, 50m, 0m, 1000m, 333.33m);
        first.ShouldBe(16.67m);
        var last = PurchasingRules.WhtOnPayment(1000m, 50m, first, 666.67m, 666.67m);
        (first + last).ShouldBe(50m);
    }
}
