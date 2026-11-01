using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Purchasing.Consignment;

namespace Erp.UnitTests;

public sealed class ConsignmentRuleTests
{
    [Theory]
    [InlineData(CommissionType.Percent, 10, 500, 2, 50)]
    [InlineData(CommissionType.Percent, 12.5, 99.99, 1, 12.5)]   // 12.49875 rounds half away from zero
    [InlineData(CommissionType.Fixed, 15, 240, 3, 45)]
    [InlineData(CommissionType.Fixed, 50, 80, 2, 80)]           // never more than the line
    [InlineData(CommissionType.Percent, 0, 300, 1, 0)]
    public void The_commission_is_known_in_advance_and_never_more_than_the_sale(CommissionType type, decimal rate, decimal amount, decimal quantity,
        decimal expected) =>
        new ConsignmentTerms(Guid.NewGuid(), type, rate).CommissionOn(amount, quantity).ShouldBe(expected);

    [Theory]
    [InlineData("2026-11-30", 5, "2026-12-05")]
    [InlineData("2026-12-31", 28, "2027-01-28")]
    [InlineData("2026-01-15", 10, "2026-02-10")]
    public void A_shop_is_paid_on_its_payout_day_of_the_next_month(string periodEnd, int day, string expected) =>
        ConsignmentService.PayoutDate(DateOnly.Parse(periodEnd), day).ShouldBe(DateOnly.Parse(expected));
}
