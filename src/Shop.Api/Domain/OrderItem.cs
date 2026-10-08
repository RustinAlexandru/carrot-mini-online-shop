namespace Shop.Api.Domain;

/// <summary>Persisted line snapshot; the product's SKU, name and unit price are copied at write time.</summary>
public sealed class OrderItem
{
    private OrderItem()
    {
    }

    internal OrderItem(OrderLine line) => Apply(line);

    public int Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid ProductId { get; private set; }
    public string Sku { get; private set; } = "";
    public string ProductName { get; private set; } = "";
    public decimal UnitPrice { get; private set; }
    public int Quantity { get; private set; }

    public decimal LineTotal => OrderPricing.LineTotal(UnitPrice, Quantity);

    internal void Apply(OrderLine line)
    {
        ProductId = line.ProductId;
        Sku = line.Sku;
        ProductName = line.ProductName;
        UnitPrice = line.UnitPrice;
        Quantity = line.Quantity;
    }
}
