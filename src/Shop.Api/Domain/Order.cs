using Shop.Api.Common;

namespace Shop.Api.Domain;

/// <summary>
/// The order aggregate. It owns every invariant: complete-input validation before any mutation, line bounds,
/// distinct nonempty items, Draft-only replacement, the item diff, the all-or-none coupon snapshot and timestamps.
/// It imports neither EF nor ASP.NET and reports business failures as <see cref="AppError"/> values.
/// </summary>
public sealed class Order
{
    public const int MaxLines = 50;
    public const decimal MaxUnitPrice = 999_999_999.99m;

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

    public static Result<Order> Create(Guid userId, IReadOnlyList<OrderLine> lines, CouponSnapshot? coupon, DateTimeOffset now)
    {
        if (Validate(lines, coupon) is { } error) return error;
        var order = new Order { Id = Guid.NewGuid(), UserId = userId, Status = OrderStatus.Draft, CreatedAt = now, UpdatedAt = now };
        order._items.AddRange(lines.Select(l => new OrderItem(l)));
        order.ApplyCoupon(coupon);
        return Result<Order>.Ok(order);
    }

    /// <summary>Whole-state replacement: adds new products, updates and reprices existing ones, removes omitted ones, sets or clears the coupon.</summary>
    public AppError? Replace(IReadOnlyList<OrderLine> lines, CouponSnapshot? coupon, DateTimeOffset now)
    {
        if (Status != OrderStatus.Draft) return new AppError.Conflict("Only Draft orders can be edited.");
        if (Validate(lines, coupon) is { } error) return error;

        var wanted = lines.ToDictionary(l => l.ProductId);
        _items.RemoveAll(item => !wanted.ContainsKey(item.ProductId));
        foreach (var item in _items) item.Apply(wanted[item.ProductId]);
        var existing = _items.Select(i => i.ProductId).ToHashSet();
        _items.AddRange(lines.Where(l => !existing.Contains(l.ProductId)).Select(l => new OrderItem(l)));
        ApplyCoupon(coupon);
        UpdatedAt = now;
        return null;
    }

    /// <summary>Only a Draft untouched since before the cutoff is abandoned; exactly at the cutoff it is still live.</summary>
    public bool IsAbandoned(DateTimeOffset cutoff) => Status == OrderStatus.Draft && UpdatedAt < cutoff;

    public AppError? Expire(DateTimeOffset now)
    {
        if (Status != OrderStatus.Draft) return new AppError.Conflict("Only Draft orders can expire.");
        Status = OrderStatus.Expired;
        UpdatedAt = now;
        return null;
    }

    public OrderTotals Totals() => OrderPricing.Calculate(_items.Select(i => (i.UnitPrice, i.Quantity)), CouponAmount);

    private void ApplyCoupon(CouponSnapshot? coupon)
    {
        CouponCode = coupon?.Code;
        CouponAmount = coupon?.Amount;
    }

    private static AppError.Validation? Validate(IReadOnlyList<OrderLine> lines, CouponSnapshot? coupon)
    {
        var errors = new Dictionary<string, string[]>();
        if (lines.Count is < 1 or > MaxLines)
            errors["items"] = [$"An order needs between 1 and {MaxLines} items."];

        var seen = new HashSet<Guid>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (!seen.Add(line.ProductId))
                errors[$"items[{i}].productId"] = ["Each product may appear only once."];
            if (!QuantityRules.IsValid(line.Quantity, line.AvailableStock))
                errors[$"items[{i}].quantity"] = [QuantityRules.IsWithinBounds(line.Quantity)
                    ? $"Only {line.AvailableStock} in stock."
                    : $"Quantity must be between {QuantityRules.Min} and {QuantityRules.Max}."];
            if (line.UnitPrice is < 0 or > MaxUnitPrice || string.IsNullOrWhiteSpace(line.Sku) || string.IsNullOrWhiteSpace(line.ProductName))
                errors[$"items[{i}].productId"] = ["The product snapshot is invalid."];
        }

        if (coupon is not null && (string.IsNullOrWhiteSpace(coupon.Code) || coupon.Amount <= 0))
            errors["couponCode"] = ["The coupon snapshot needs a code and a positive amount."];

        return errors.Count == 0 ? null : new AppError.Validation(errors);
    }
}
