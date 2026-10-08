namespace Shop.Api.Domain;

/// <summary>Persisted line snapshot; the product's SKU, name and unit price are copied at write time.</summary>
public sealed class OrderItem
{
    private OrderItem()
    {
    }

    public int Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid ProductId { get; private set; }
    public string Sku { get; private set; } = "";
    public string ProductName { get; private set; } = "";
    public decimal UnitPrice { get; private set; }
    public int Quantity { get; private set; }
}
