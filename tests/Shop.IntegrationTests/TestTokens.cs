using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Shop.IntegrationTests;

/// <summary>Crafts bearer tokens with the test signing key (or another) to probe the real validation middleware.</summary>
public static class TestTokens
{
    public const string ForeignKey = "A-Different-Signing-Key-0123456789-abcdef";

    public static string Create(Action<SecurityTokenDescriptor>? adjust = null, string key = ShopApiFactory.SigningKey)
    {
        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "MiniShop",
            Audience = "MiniShop.Web",
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(5),
            Claims = new Dictionary<string, object> { ["sub"] = Guid.NewGuid().ToString() },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256)
        };
        adjust?.Invoke(descriptor);
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    public static string Expired() => Create(d =>
    {
        d.IssuedAt = DateTime.UtcNow.AddMinutes(-70);
        d.NotBefore = DateTime.UtcNow.AddMinutes(-70);
        d.Expires = DateTime.UtcNow.AddMinutes(-10);
    });
}
