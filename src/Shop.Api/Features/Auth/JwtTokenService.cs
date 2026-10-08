using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shop.Api.Domain;

namespace Shop.Api.Features.Auth;

public sealed record IssuedToken(string Token, DateTimeOffset ExpiresAt);

public sealed class JwtTokenService(IOptions<JwtOptions> options, TimeProvider time)
{
    private readonly JsonWebTokenHandler _handler = new();

    public IssuedToken Issue(User user)
    {
        var jwt = options.Value;
        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(time.GetUtcNow().ToUnixTimeSeconds());
        var expiresAt = issuedAt + jwt.Lifetime;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            IssuedAt = issuedAt.UtcDateTime,
            NotBefore = issuedAt.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = user.Id.ToString(),
                [JwtRegisteredClaimNames.Email] = user.Email
            },
            SigningCredentials = new SigningCredentials(jwt.CreateSigningKey(), SecurityAlgorithms.HmacSha256)
        };
        return new IssuedToken(_handler.CreateToken(descriptor), expiresAt);
    }
}
