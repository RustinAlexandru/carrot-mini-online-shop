using Shop.Api.Domain;
using Xunit;
namespace Shop.UnitTests;
public class QuantityTests
{
    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(10000, 10000, true)]
    [InlineData(0, 10, false)]
    [InlineData(-1, 10, false)]
    [InlineData(10001, 20000, false)]
    [InlineData(4, 3, false)]
    [InlineData(1, 0, false)]
    public void Quantity_obeys_positive_bound_and_stock_ceiling(int quantity, int stock, bool expected)
        => Assert.Equal(expected, QuantityRules.IsValid(quantity, stock));
}
