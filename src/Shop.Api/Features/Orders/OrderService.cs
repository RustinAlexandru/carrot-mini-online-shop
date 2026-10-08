using Microsoft.EntityFrameworkCore;
using Shop.Api.Common;
using Shop.Api.Data;
using Shop.Api.Domain;

namespace Shop.Api.Features.Orders;

/// <summary>
/// Owned-order use cases: ownership lookup, catalog/coupon resolution, calling the aggregate, one save, response mapping.
/// Business failures come back as <see cref="AppError"/> values, never as HTTP types.
/// </summary>
public sealed class OrderService(ShopDbContext db, TimeProvider time)
{
    private static readonly AppError.NotFound OrderNotFound = new("Order not found.");

    public async Task<Result<OrderResponse>> CreateAsync(Guid userId, CreateOrderRequest request, CancellationToken cancellationToken)
    {
        var resolved = await ResolveAsync(request.Items, request.CouponCode, cancellationToken);
        if (!resolved.IsSuccess) return resolved.Error;

        var created = Order.Create(userId, resolved.Value.Lines, resolved.Value.Coupon, time.GetUtcNow());
        if (!created.IsSuccess) return created.Error;

        db.Orders.Add(created.Value);
        await db.SaveChangesAsync(cancellationToken);
        return OrderResponse.From(created.Value);
    }

    public async Task<Result<OrderResponse>> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking().Include(o => o.Items)
            .SingleOrDefaultAsync(o => o.Id == id && o.UserId == userId, cancellationToken);
        return order is null ? OrderNotFound : OrderResponse.From(order);
    }

    public async Task<Result<OrderResponse>> ReplaceAsync(Guid userId, Guid id, UpdateOrderRequest request, CancellationToken cancellationToken)
    {
        var order = await db.Orders.Include(o => o.Items)
            .SingleOrDefaultAsync(o => o.Id == id && o.UserId == userId, cancellationToken);
        if (order is null) return OrderNotFound;

        var resolved = await ResolveAsync(request.Items, request.CouponCode, cancellationToken);
        if (!resolved.IsSuccess) return resolved.Error;

        if (order.Replace(resolved.Value.Lines, resolved.Value.Coupon, time.GetUtcNow()) is { } error) return error;

        await db.SaveLiveOrderAsync(order, cancellationToken);
        return OrderResponse.From(order);
    }

    public async Task<AppError?> DeleteAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        var order = await db.Orders.SingleOrDefaultAsync(o => o.Id == id && o.UserId == userId, cancellationToken);
        if (order is null) return OrderNotFound;

        db.Orders.Remove(order); // the original rowversion guards the DELETE; items cascade in the database
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    private sealed record Resolved(List<OrderLine> Lines, CouponSnapshot? Coupon);

    /// <summary>Turns client product ids/quantities and a coupon code into server-priced lines and a coupon snapshot.</summary>
    private async Task<Result<Resolved>> ResolveAsync(List<OrderItemRequest?>? items, string? couponCode, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var requested = new List<(int Index, Guid ProductId, int Quantity)>();
        for (var i = 0; i < (items?.Count ?? 0); i++)
        {
            var item = items![i];
            if (item?.ProductId is not { } productId || productId == Guid.Empty)
                errors[$"items[{i}].productId"] = ["productId is required."];
            if (item?.Quantity is not { } quantity)
                errors[$"items[{i}].quantity"] = ["quantity is required."];
            else if (item.ProductId is { } id && id != Guid.Empty)
                requested.Add((i, id, quantity));
        }

        var ids = requested.Select(r => r.ProductId).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        var lines = new List<OrderLine>();
        foreach (var (index, productId, quantity) in requested)
        {
            if (!products.TryGetValue(productId, out var product))
                errors[$"items[{index}].productId"] = ["Unknown product."];
            else
                lines.Add(new OrderLine(product.Id, product.Sku, product.Name, product.Price, quantity, product.StockQuantity));
        }

        CouponSnapshot? coupon = null;
        if (couponCode is not null)
        {
            var code = couponCode.Trim().ToUpperInvariant();
            var found = code.Length == 0 ? null : await db.Coupons.AsNoTracking().SingleOrDefaultAsync(c => c.Code == code, cancellationToken);
            if (found is null) errors["couponCode"] = [code.Length == 0 ? "couponCode must not be blank." : "Unknown coupon code."];
            else coupon = new CouponSnapshot(found.Code, found.Amount);
        }

        // Unresolvable input is reported here; the aggregate then validates the complete, resolved state itself.
        if (errors.Count > 0) return new AppError.Validation(errors);
        return Result<Resolved>.Ok(new Resolved(lines, coupon));
    }
}
