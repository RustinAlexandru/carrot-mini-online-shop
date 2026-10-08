using Microsoft.EntityFrameworkCore;
using Shop.Api.Common;
using Shop.Api.Data;
using Shop.Api.Domain;

namespace Shop.Api.Features.Products;

public sealed record ProductDto(
    Guid Id, string Sku, string Name, string Description, string Category,
    decimal Price, int StockQuantity, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record ProductPage(IReadOnlyList<ProductDto> Items, int Page, int PageSize, int TotalCount, int TotalPages);

public static class ProductEndpoints
{
    public static void MapProducts(this IEndpointRouteBuilder app)
        => app.MapGet("/products", ListAsync).AllowAnonymous();

    private static async Task<IResult> ListAsync(
        ShopDbContext db, CancellationToken cancellationToken,
        int? page = null, int? pageSize = null, string? sortBy = null, string? sortDirection = null)
    {
        var (query, errors) = ProductQuery.Parse(page, pageSize, sortBy, sortDirection);
        if (query is null) return ApiProblems.Validation(errors);

        var totalCount = await db.Products.CountAsync(cancellationToken);
        var items = new List<ProductDto>();
        // An offset beyond int.MaxValue can hold no rows; skip the query instead of overflowing Skip().
        if (query.Offset < Math.Min(totalCount, int.MaxValue))
        {
            items = await Order(db.Products.AsNoTracking(), query)
                .Skip((int)query.Offset).Take(query.PageSize)
                .Select(p => new ProductDto(p.Id, p.Sku, p.Name, p.Description, p.Category, p.Price, p.StockQuantity, p.CreatedAt, p.UpdatedAt))
                .ToListAsync(cancellationToken);
        }

        var totalPages = (totalCount + query.PageSize - 1) / query.PageSize;
        return Results.Ok(new ProductPage(items, query.Page, query.PageSize, totalCount, totalPages));
    }

    // Id is the final tie-break so paging over equal sort keys is stable.
    private static IOrderedQueryable<Product> Order(IQueryable<Product> products, ProductQuery query)
    {
        var ordered = (query.SortBy, query.Descending) switch
        {
            (ProductSortField.Name, false) => products.OrderBy(p => p.Name),
            (ProductSortField.Name, true) => products.OrderByDescending(p => p.Name),
            (ProductSortField.Price, false) => products.OrderBy(p => p.Price),
            (ProductSortField.Price, true) => products.OrderByDescending(p => p.Price),
            (ProductSortField.CreatedAt, false) => products.OrderBy(p => p.CreatedAt),
            _ => products.OrderByDescending(p => p.CreatedAt)
        };
        return ordered.ThenBy(p => p.Id);
    }
}
