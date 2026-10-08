namespace Shop.Api.Domain;

public readonly record struct OrderTotals(decimal Subtotal, decimal Discount, decimal Total);

/// <summary>Exact decimal arithmetic over two-decimal amounts and integer quantities.</summary>
public static class OrderPricing
{
    public static decimal LineTotal(decimal unitPrice, int quantity) => unitPrice * quantity;

    /// <summary>The fixed coupon amount is capped at the subtotal, so a total is never negative.</summary>
    public static decimal Discount(decimal? couponAmount, decimal subtotal)
        => couponAmount is { } amount ? Math.Min(amount, subtotal) : 0m;

    public static OrderTotals Calculate(IEnumerable<(decimal UnitPrice, int Quantity)> lines, decimal? couponAmount)
    {
        var subtotal = lines.Sum(l => LineTotal(l.UnitPrice, l.Quantity));
        var discount = Discount(couponAmount, subtotal);
        return new OrderTotals(subtotal, discount, subtotal - discount);
    }
}
