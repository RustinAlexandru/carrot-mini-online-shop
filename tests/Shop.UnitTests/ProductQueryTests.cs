using Shop.Api.Features.Products;
using Xunit;

namespace Shop.UnitTests;

public class ProductQueryTests
{
    [Fact]
    public void Defaults_are_first_page_of_twelve_by_name_ascending()
    {
        var (query, errors) = ProductQuery.Parse(null, null, null, null);
        Assert.Empty(errors);
        Assert.Equal(new ProductQuery(1, 12, ProductSortField.Name, false), query);
    }

    [Theory]
    [InlineData("name", "asc", ProductSortField.Name, false)]
    [InlineData("PRICE", "DESC", ProductSortField.Price, true)]
    [InlineData("createdAt", "desc", ProductSortField.CreatedAt, true)]
    [InlineData("createdat", "asc", ProductSortField.CreatedAt, false)]
    public void Sort_fields_and_directions_are_case_insensitive(string sortBy, string direction, ProductSortField field, bool descending)
    {
        var (query, errors) = ProductQuery.Parse(2, 50, sortBy, direction);
        Assert.Empty(errors);
        Assert.Equal(new ProductQuery(2, 50, field, descending), query);
    }

    [Theory]
    [InlineData(0, 12, null, null, "page")]
    [InlineData(-3, 12, null, null, "page")]
    [InlineData(1, 0, null, null, "pageSize")]
    [InlineData(1, 51, null, null, "pageSize")]
    [InlineData(1, 12, "sku", null, "sortBy")]
    [InlineData(1, 12, "", null, "sortBy")]
    [InlineData(1, 12, null, "up", "sortDirection")]
    public void Out_of_range_or_unlisted_values_are_reported_by_field(int page, int pageSize, string? sortBy, string? direction, string field)
    {
        var (query, errors) = ProductQuery.Parse(page, pageSize, sortBy, direction);
        Assert.Null(query);
        Assert.Equal([field], errors.Keys);
    }

    [Fact]
    public void Every_invalid_parameter_is_reported_together()
    {
        var (_, errors) = ProductQuery.Parse(0, 0, "x", "y");
        Assert.Equal(["page", "pageSize", "sortBy", "sortDirection"], errors.Keys.Order());
    }

    [Fact]
    public void Offset_is_computed_without_int_overflow()
    {
        var (query, _) = ProductQuery.Parse(int.MaxValue, 50, null, null);
        Assert.Equal((long)(int.MaxValue - 1) * 50, query!.Offset);
    }
}
