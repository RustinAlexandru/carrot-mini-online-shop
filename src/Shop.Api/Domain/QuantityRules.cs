namespace Shop.Api.Domain;

/// <summary>Sole C# owner of the line quantity bound and the stock-ceiling predicate; the JS guard only mirrors it for usability.</summary>
public static class QuantityRules
{
    public const int Min = 1;
    public const int Max = 10000;

    public static bool IsWithinBounds(int quantity) => quantity is >= Min and <= Max;

    public static bool IsValid(int quantity, int availableStock) =>
        IsWithinBounds(quantity) && quantity <= availableStock;
}
