namespace Shop.Api.Domain;

/// <summary>A resolved line: price, name and stock come from server catalog data, never from the client.</summary>
public sealed record OrderLine(Guid ProductId, string Sku, string ProductName, decimal UnitPrice, int Quantity, int AvailableStock);

/// <summary>A resolved coupon snapshot copied onto the order.</summary>
public sealed record CouponSnapshot(string Code, decimal Amount);
