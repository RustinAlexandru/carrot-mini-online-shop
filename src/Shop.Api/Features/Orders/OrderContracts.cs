using Shop.Api.Domain;

namespace Shop.Api.Features.Orders;

// Clients send product ids and quantities only: unknown members such as prices, totals or user ids are rejected at binding.
public sealed record OrderItemRequest(Guid? ProductId, int? Quantity);

/// <summary>Create may omit couponCode, which means no coupon.</summary>
public sealed record CreateOrderRequest(List<OrderItemRequest?>? Items, string? CouponCode);

/// <summary>Whole-state replacement. couponCode must be present: a missing member fails binding (400) while an explicit null removes the coupon.</summary>
public sealed record UpdateOrderRequest
{
    public required List<OrderItemRequest?>? Items { get; init; }
    public required string? CouponCode { get; init; }
}

public sealed record OrderItemResponse(Guid ProductId, string Sku, string Name, decimal UnitPrice, int Quantity, decimal LineTotal);

public sealed record CouponResponse(string Code, decimal Amount);

public sealed record OrderResponse(
    Guid Id, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<OrderItemResponse> Items, CouponResponse? Coupon,
    decimal Subtotal, decimal Discount, decimal Total, string Currency)
{
    public const string UsDollars = "USD";

    public static OrderResponse From(Order order)
    {
        var totals = order.Totals();
        return new OrderResponse(
            order.Id, order.Status.ToString(), order.CreatedAt, order.UpdatedAt,
            order.Items.OrderBy(i => i.Id)
                .Select(i => new OrderItemResponse(i.ProductId, i.Sku, i.ProductName, i.UnitPrice, i.Quantity, i.LineTotal))
                .ToList(),
            order.CouponCode is null ? null : new CouponResponse(order.CouponCode, order.CouponAmount!.Value),
            totals.Subtotal, totals.Discount, totals.Total, UsDollars);
    }
}
