using Erp.Modules.Core.Platform;

namespace Erp.UnitTests;

public sealed class BillingRuleTests
{
    private static readonly DateOnly Today = new(2026, 10, 19);

    [Theory]
    [InlineData("2026-10-01", BillingCycle.Monthly, "2026-10-31")]
    [InlineData("2026-01-31", BillingCycle.Monthly, "2026-02-27")]
    [InlineData("2026-10-19", BillingCycle.Annual, "2027-10-18")]
    public void Periods_cover_one_month_or_one_year(string start, BillingCycle cycle, string end) =>
        BillingRules.PeriodEnd(DateOnly.Parse(start), cycle).ShouldBe(DateOnly.Parse(end));

    [Fact]
    public void Status_follows_the_oldest_unpaid_invoice()
    {
        BillingRules.StatusFor(SubscriptionStatus.Active, Today, null, true, 14, null).ShouldBe(SubscriptionStatus.Active);
        BillingRules.StatusFor(SubscriptionStatus.Active, Today, Today, true, 14, null).ShouldBe(SubscriptionStatus.Active);
        BillingRules.StatusFor(SubscriptionStatus.Active, Today, Today.AddDays(-3), true, 14, null).ShouldBe(SubscriptionStatus.PastDue);
        BillingRules.StatusFor(SubscriptionStatus.PastDue, Today, Today.AddDays(-15), true, 14, null).ShouldBe(SubscriptionStatus.Suspended);
        BillingRules.StatusFor(SubscriptionStatus.PastDue, Today, Today.AddDays(-15), false, 14, null).ShouldBe(SubscriptionStatus.PastDue);
        BillingRules.StatusFor(SubscriptionStatus.Suspended, Today, null, true, 14, null).ShouldBe(SubscriptionStatus.Active);
        BillingRules.StatusFor(SubscriptionStatus.Trial, Today, null, true, 14, Today.AddDays(5)).ShouldBe(SubscriptionStatus.Trial);
        BillingRules.StatusFor(SubscriptionStatus.Cancelled, Today, Today.AddDays(-90), true, 14, null).ShouldBe(SubscriptionStatus.Cancelled);
    }
}
