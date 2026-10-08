namespace Shop.Api.Domain;

/// <summary>
/// Persisted order shape. M3 adds the controlled Create/Replace/Expire operations that own every invariant.
/// </summary>
public sealed class Order
{
    private readonly List<OrderItem> _items = [];

    private Order()
    {
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public OrderStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public string? CouponCode { get; private set; }
    public decimal? CouponAmount { get; private set; }
    public byte[] Version { get; private set; } = [];
    public IReadOnlyCollection<OrderItem> Items => _items;
}
