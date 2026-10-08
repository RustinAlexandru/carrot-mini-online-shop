namespace Shop.Api.Domain;
public static class QuantityRules
{
    public static bool IsValid(int quantity, int availableStock) =>
        quantity is >= 1 and <= 10000 && quantity <= availableStock;
}
