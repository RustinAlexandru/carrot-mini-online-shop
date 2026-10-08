using Shop.Api.Common;
using Shop.Api.Domain;
using Xunit;

namespace Shop.UnitTests;

public class OrderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid User = Guid.NewGuid();
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid();

    private static OrderLine Line(Guid product, int quantity, decimal price = 10.00m, int stock = 100)
        => new(product, $"SKU-{product.ToString()[..4]}", $"Product {product.ToString()[..4]}", price, quantity, stock);

    private static Order NewOrder(CouponSnapshot? coupon = null, params OrderLine[] lines)
        => Order.Create(User, lines.Length == 0 ? [Line(A, 2), Line(B, 1, 5.00m)] : lines, coupon, T0).Value;

    private static IReadOnlyDictionary<string, string[]> Errors(AppError? error)
        => Assert.IsType<AppError.Validation>(error).Errors;

    private static (Guid Product, int Quantity, decimal Price)[] Items(Order order)
        => order.Items.OrderBy(i => i.ProductId).Select(i => (i.ProductId, i.Quantity, i.UnitPrice)).ToArray();

    // ---- Create ----

    [Fact]
    public void Create_makes_a_draft_with_snapshots_and_matching_timestamps()
    {
        var order = NewOrder(new CouponSnapshot("SAVE5", 5.00m));

        Assert.Equal((User, OrderStatus.Draft, T0, T0), (order.UserId, order.Status, order.CreatedAt, order.UpdatedAt));
        Assert.NotEqual(Guid.Empty, order.Id);
        Assert.Equal(("SAVE5", 5.00m), (order.CouponCode, order.CouponAmount));
        Assert.Equal(new[] { (A, 2, 10.00m), (B, 1, 5.00m) }.OrderBy(i => i.Item1), Items(order));
        Assert.Equal(new OrderTotals(25.00m, 5.00m, 20.00m), order.Totals());
    }

    [Fact]
    public void Create_without_a_coupon_stores_neither_code_nor_amount()
    {
        var order = NewOrder();
        Assert.Null(order.CouponCode);
        Assert.Null(order.CouponAmount);
        Assert.Equal(new OrderTotals(25.00m, 0m, 25.00m), order.Totals());
    }

    [Fact]
    public void Create_rejects_an_empty_order()
        => Assert.Contains("items", Errors(Order.Create(User, [], null, T0).Error).Keys);

    [Fact]
    public void Create_accepts_fifty_lines_and_rejects_fifty_one()
    {
        OrderLine[] Many(int n) => Enumerable.Range(0, n).Select(_ => Line(Guid.NewGuid(), 1)).ToArray();
        Assert.True(Order.Create(User, Many(50), null, T0).IsSuccess);
        Assert.Contains("items", Errors(Order.Create(User, Many(51), null, T0).Error).Keys);
    }

    [Fact]
    public void Create_rejects_a_repeated_product()
        => Assert.Contains("items[1].productId", Errors(Order.Create(User, [Line(A, 1), Line(A, 2)], null, T0).Error).Keys);

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    [InlineData(10001, 20000)]
    public void Create_rejects_quantities_outside_one_to_ten_thousand(int quantity, int stock)
        => Assert.Equal(["Quantity must be between 1 and 10000."],
            Errors(Order.Create(User, [Line(A, quantity, stock: stock)], null, T0).Error)["items[0].quantity"]);

    [Theory]
    [InlineData(4, 3)]
    [InlineData(1, 0)]
    public void Create_enforces_the_per_order_stock_ceiling(int quantity, int stock)
        => Assert.Equal([$"Only {stock} in stock."], Errors(Order.Create(User, [Line(A, quantity, stock: stock)], null, T0).Error)["items[0].quantity"]);

    [Fact]
    public void Create_accepts_exactly_the_stock_and_the_maximum_quantity()
    {
        Assert.True(Order.Create(User, [Line(A, 3, stock: 3)], null, T0).IsSuccess);
        Assert.True(Order.Create(User, [Line(A, 10000, stock: 10000)], null, T0).IsSuccess);
    }

    [Fact]
    public void Create_reports_every_problem_together_and_creates_nothing()
    {
        var result = Order.Create(User, [Line(A, 0), Line(B, 99, stock: 1), Line(B, 1)], new CouponSnapshot("", 0m), T0);
        Assert.False(result.IsSuccess);
        Assert.Equal(["couponCode", "items[0].quantity", "items[1].quantity", "items[2].productId"], Errors(result.Error).Keys.Order());
    }

    [Theory]
    [InlineData("", 5.00)]
    [InlineData("  ", 5.00)]
    [InlineData("SAVE5", 0.00)]
    [InlineData("SAVE5", -1.00)]
    public void Create_rejects_an_incomplete_or_non_positive_coupon_snapshot(string code, double amount)
        => Assert.Contains("couponCode", Errors(Order.Create(User, [Line(A, 1)], new CouponSnapshot(code, (decimal)amount), T0).Error).Keys);

    [Fact]
    public void Create_rejects_an_invalid_product_snapshot()
    {
        Assert.Contains("items[0].productId", Errors(Order.Create(User, [Line(A, 1, price: -0.01m)], null, T0).Error).Keys);
        Assert.Contains("items[0].productId", Errors(Order.Create(User, [Line(A, 1, price: Order.MaxUnitPrice + 0.01m)], null, T0).Error).Keys);
        Assert.Contains("items[0].productId", Errors(Order.Create(User, [Line(A, 1) with { Sku = " " }], null, T0).Error).Keys);
    }

    // ---- Replace ----

    [Fact]
    public void Replace_adds_updates_reprices_and_removes_exactly_the_difference()
    {
        var order = NewOrder(); // A x2 @10.00, B x1 @5.00
        var later = T0.AddMinutes(5);

        var error = order.Replace([Line(B, 3, price: 6.50m), Line(C, 1, price: 2.00m)], null, later);

        Assert.Null(error);
        Assert.Equal(new[] { (B, 3, 6.50m), (C, 1, 2.00m) }.OrderBy(i => i.Item1), Items(order));
        Assert.DoesNotContain(order.Items, i => i.ProductId == A);
        Assert.Equal((T0, later), (order.CreatedAt, order.UpdatedAt));
        Assert.Equal(new OrderTotals(21.50m, 0m, 21.50m), order.Totals());
    }

    [Fact]
    public void Replace_sets_changes_and_clears_the_coupon_snapshot()
    {
        var order = NewOrder();
        Assert.Null(order.Replace([Line(A, 2)], new CouponSnapshot("SAVE10", 10.00m), T0.AddMinutes(1)));
        Assert.Equal(("SAVE10", 10.00m), (order.CouponCode, order.CouponAmount));
        Assert.Null(order.Replace([Line(A, 2)], new CouponSnapshot("SAVE5", 5.00m), T0.AddMinutes(2)));
        Assert.Equal(("SAVE5", 5.00m), (order.CouponCode, order.CouponAmount));
        Assert.Null(order.Replace([Line(A, 2)], null, T0.AddMinutes(3)));
        Assert.Equal((null, null), (order.CouponCode, order.CouponAmount));
    }

    [Fact]
    public void Replace_refreshes_a_changed_coupon_amount_under_the_same_code()
    {
        var order = NewOrder(new CouponSnapshot("SAVE5", 5.00m));
        Assert.Null(order.Replace([Line(A, 2)], new CouponSnapshot("SAVE5", 7.00m), T0.AddMinutes(1)));
        Assert.Equal(7.00m, order.CouponAmount);
    }

    [Fact]
    public void An_invalid_replacement_changes_nothing()
    {
        var order = NewOrder(new CouponSnapshot("SAVE5", 5.00m));
        var before = Items(order);

        var error = order.Replace([Line(A, 1), Line(C, 0), Line(C, 1)], new CouponSnapshot("X", 0m), T0.AddHours(1));

        Assert.IsType<AppError.Validation>(error);
        Assert.Equal(before, Items(order));
        Assert.Equal(("SAVE5", 5.00m, T0), (order.CouponCode, order.CouponAmount, order.UpdatedAt));
    }

    [Fact]
    public void An_empty_replacement_is_rejected_and_keeps_the_items()
    {
        var order = NewOrder();
        Assert.Contains("items", Errors(order.Replace([], null, T0.AddMinutes(1))).Keys);
        Assert.Equal(2, order.Items.Count);
    }

    [Fact]
    public void An_expired_order_cannot_be_replaced()
    {
        var order = NewOrder(new CouponSnapshot("SAVE5", 5.00m));
        Assert.Null(order.Expire(T0.AddHours(1)));
        var before = Items(order);

        var error = order.Replace([Line(C, 1)], null, T0.AddHours(2));

        Assert.IsType<AppError.Conflict>(error);
        Assert.Equal(before, Items(order));
        Assert.Equal(("SAVE5", T0.AddHours(1)), (order.CouponCode, order.UpdatedAt));
    }

    // ---- Lifecycle ----

    [Fact]
    public void Only_a_draft_untouched_before_the_cutoff_is_abandoned()
    {
        var order = NewOrder();
        Assert.True(order.IsAbandoned(T0.AddTicks(1)));
        Assert.False(order.IsAbandoned(T0)); // exactly at the cutoff stays live
        Assert.False(order.IsAbandoned(T0.AddTicks(-1)));
    }

    [Fact]
    public void Editing_moves_the_order_out_of_the_abandoned_window()
    {
        var order = NewOrder();
        Assert.Null(order.Replace([Line(A, 1)], null, T0.AddMinutes(20)));
        Assert.False(order.IsAbandoned(T0.AddMinutes(10)));
        Assert.True(order.IsAbandoned(T0.AddMinutes(21)));
    }

    [Fact]
    public void Expire_marks_a_draft_expired_once()
    {
        var order = NewOrder();
        Assert.Null(order.Expire(T0.AddMinutes(31)));
        Assert.Equal((OrderStatus.Expired, T0.AddMinutes(31)), (order.Status, order.UpdatedAt));
        Assert.False(order.IsAbandoned(T0.AddDays(1)));
        Assert.IsType<AppError.Conflict>(order.Expire(T0.AddMinutes(40)));
        Assert.Equal(T0.AddMinutes(31), order.UpdatedAt);
    }

    [Fact]
    public void Totals_cap_the_discount_and_never_go_negative()
    {
        var order = NewOrder(new CouponSnapshot("SAVE10", 10.00m), Line(A, 1, price: 3.00m));
        Assert.Equal(new OrderTotals(3.00m, 3.00m, 0m), order.Totals());
    }
}
