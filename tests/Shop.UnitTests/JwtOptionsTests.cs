using Shop.Api.Features.Auth;
using Xunit;

namespace Shop.UnitTests;

public class JwtOptionsTests
{
    private static JwtOptions Valid() => new()
    {
        Issuer = "MiniShop", Audience = "MiniShop.Web", SigningKey = new string('k', 32), Lifetime = TimeSpan.FromMinutes(60)
    };

    private static readonly JwtOptionsValidator Validator = new();

    [Fact]
    public void Complete_options_with_a_32_byte_key_are_valid()
        => Assert.True(Validator.Validate(null, Valid()).Succeeded);

    [Fact]
    public void A_31_byte_key_is_rejected_with_the_setting_name()
    {
        var options = Valid();
        options.SigningKey = new string('k', 31);
        var result = Validator.Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains("Jwt:SigningKey", result.FailureMessage);
    }

    [Fact]
    public void Key_length_counts_utf8_bytes_not_characters()
    {
        var options = Valid();
        options.SigningKey = new string('é', 16); // 16 characters, 32 bytes
        Assert.True(Validator.Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData("", "aud", 60, "Jwt:Issuer")]
    [InlineData("iss", " ", 60, "Jwt:Audience")]
    [InlineData("iss", "aud", 0, "Jwt:Lifetime")]
    [InlineData("iss", "aud", -5, "Jwt:Lifetime")]
    public void Missing_issuer_audience_or_non_positive_lifetime_is_rejected(string issuer, string audience, int minutes, string expected)
    {
        var options = Valid();
        options.Issuer = issuer;
        options.Audience = audience;
        options.Lifetime = TimeSpan.FromMinutes(minutes);
        var result = Validator.Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(expected, result.FailureMessage);
    }
}
