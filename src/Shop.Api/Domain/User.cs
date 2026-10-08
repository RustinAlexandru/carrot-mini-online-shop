namespace Shop.Api.Domain;

public sealed class User
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Email { get; init; }
    public required string PasswordHash { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
}
