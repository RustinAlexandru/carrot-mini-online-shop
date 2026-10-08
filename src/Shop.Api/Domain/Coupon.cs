namespace Shop.Api.Domain;

public sealed class Coupon
{
    public int Id { get; private set; }
    public required string Code { get; init; }
    public required decimal Amount { get; init; }
}
