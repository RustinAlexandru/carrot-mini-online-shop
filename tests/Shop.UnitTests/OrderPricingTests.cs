using Shop.Api.Domain;
using Xunit;

namespace Shop.UnitTests;

public class OrderPricingTests
{
    [Fact]
    public void Line_total_is_exact_decimal_multiplication()
    {
        Assert.Equal(59.97m, OrderPricing.LineTotal(19.99m, 3));
        Assert.Equal(0.30m, OrderPricing.LineTotal(0.10m, 3)); // binary floating point would give 0.30000000000000004
    }

    [Fact]
    public void Subtotal_sums_lines_and_no_coupon_means_no_discount()
    {
        var totals = OrderPricing.Calculate([(14.50m, 2), (12.90m, 1)], couponAmount: null);
        Assert.Equal(new OrderTotals(41.90m, 0m, 41.90m), totals);
    }

    [Fact]
    public void A_coupon_below_the_subtotal_is_subtracted_in_full()
        => Assert.Equal(new OrderTotals(41.90m, 5.00m, 36.90m), OrderPricing.Calculate([(14.50m, 2), (12.90m, 1)], 5.00m));

    [Theory]
    [InlineData("3.00", "3.00", "0.00")] // exactly the subtotal
    [InlineData("3.00", "10.00", "0.00")] // discount exceeding the subtotal is capped
    [InlineData("0.01", "10.00", "0.00")]
    public void Discount_is_capped_at_the_subtotal_so_the_total_is_never_negative(string subtotal, string coupon, string total)
    {
        var totals = OrderPricing.Calculate([(decimal.Parse(subtotal, System.Globalization.CultureInfo.InvariantCulture), 1)],
            decimal.Parse(coupon, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(decimal.Parse(subtotal, System.Globalization.CultureInfo.InvariantCulture), totals.Discount);
        Assert.Equal(decimal.Parse(total, System.Globalization.CultureInfo.InvariantCulture), totals.Total);
    }

    [Fact]
    public void Maximum_allowed_order_stays_exact()
    {
        var totals = OrderPricing.Calculate(Enumerable.Repeat((Order.MaxUnitPrice, QuantityRules.Max), Order.MaxLines), 10.00m);
        Assert.Equal(Order.MaxUnitPrice * QuantityRules.Max * Order.MaxLines, totals.Subtotal);
        Assert.Equal(totals.Subtotal - 10.00m, totals.Total);
    }
}
