namespace Shop.Api.Features.Products;

public enum ProductSortField
{
    Name,
    Price,
    CreatedAt
}

/// <summary>Validated catalog paging and sorting parameters; the allowlists live here and nowhere else.</summary>
public sealed record ProductQuery(int Page, int PageSize, ProductSortField SortBy, bool Descending)
{
    public const int DefaultPageSize = 12;
    public const int MaxPageSize = 50;

    public long Offset => (long)(Page - 1) * PageSize;

    public static (ProductQuery? Query, Dictionary<string, string[]> Errors) Parse(
        int? page, int? pageSize, string? sortBy, string? sortDirection)
    {
        var errors = new Dictionary<string, string[]>();
        var pageValue = page ?? 1;
        var pageSizeValue = pageSize ?? DefaultPageSize;
        if (pageValue < 1) errors["page"] = ["page must be at least 1."];
        if (pageSizeValue is < 1 or > MaxPageSize) errors["pageSize"] = [$"pageSize must be between 1 and {MaxPageSize}."];

        var field = ProductSortField.Name;
        if (sortBy is not null && !TryParseField(sortBy, out field))
            errors["sortBy"] = ["sortBy must be one of: name, price, createdAt."];

        var descending = false;
        if (sortDirection is not null)
        {
            if (sortDirection.Equals("desc", StringComparison.OrdinalIgnoreCase)) descending = true;
            else if (!sortDirection.Equals("asc", StringComparison.OrdinalIgnoreCase))
                errors["sortDirection"] = ["sortDirection must be asc or desc."];
        }

        return errors.Count > 0 ? (null, errors) : (new ProductQuery(pageValue, pageSizeValue, field, descending), errors);
    }

    private static bool TryParseField(string value, out ProductSortField field)
    {
        switch (value.ToLowerInvariant())
        {
            case "name": field = ProductSortField.Name; return true;
            case "price": field = ProductSortField.Price; return true;
            case "createdat": field = ProductSortField.CreatedAt; return true;
            default: field = default; return false;
        }
    }
}
