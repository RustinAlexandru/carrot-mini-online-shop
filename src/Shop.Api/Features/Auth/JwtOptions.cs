using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Shop.Api.Features.Auth;

/// <summary>Single source for token issuance and bearer validation. The signing key comes from the environment, never appsettings.</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public const int MinimumKeyBytes = 32;

    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "";
    public string SigningKey { get; set; } = "";
    public TimeSpan Lifetime { get; set; }

    public SymmetricSecurityKey CreateSigningKey() => new(Encoding.UTF8.GetBytes(SigningKey));
}

public sealed class JwtOptionsValidator : IValidateOptions<JwtOptions>
{
    public ValidateOptionsResult Validate(string? name, JwtOptions options)
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Issuer)) failures.Add("Jwt:Issuer is required.");
        if (string.IsNullOrWhiteSpace(options.Audience)) failures.Add("Jwt:Audience is required.");
        if (Encoding.UTF8.GetByteCount(options.SigningKey ?? "") < JwtOptions.MinimumKeyBytes)
            failures.Add($"Jwt:SigningKey (set Jwt__SigningKey) must be at least {JwtOptions.MinimumKeyBytes} UTF-8 bytes.");
        if (options.Lifetime <= TimeSpan.Zero) failures.Add("Jwt:Lifetime must be positive.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
