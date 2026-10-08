using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shop.Api.Features.Auth;
using Xunit;

namespace Shop.UnitTests;

/// <summary>Starts a real generic host with the production auth registration (no SQL) to prove options are validated at start.</summary>
public class JwtStartupValidationTests
{
    private static IHost CreateHost(Dictionary<string, string?> overrides, params string[] remove)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Jwt:Issuer"] = "MiniShop",
            ["Jwt:Audience"] = "MiniShop.Web",
            ["Jwt:SigningKey"] = new string('k', 32),
            ["Jwt:Lifetime"] = "01:00:00"
        };
        foreach (var (key, value) in overrides) settings[key] = value;
        foreach (var key in remove) settings.Remove(key);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddShopAuthentication();
        return builder.Build();
    }

    [Fact]
    public async Task Valid_options_start_the_host()
    {
        using var host = CreateHost([]);
        await host.StartAsync();
        Assert.Equal("MiniShop", host.Services.GetRequiredService<IOptions<JwtOptions>>().Value.Issuer);
        await host.StopAsync();
    }

    [Theory]
    [InlineData("Jwt:SigningKey", null, "Jwt:SigningKey")]
    [InlineData("Jwt:SigningKey", "0123456789012345678901234567890", "Jwt:SigningKey")] // 31 bytes
    [InlineData("Jwt:Issuer", "", "Jwt:Issuer")]
    [InlineData("Jwt:Audience", " ", "Jwt:Audience")]
    [InlineData("Jwt:Lifetime", "00:00:00", "Jwt:Lifetime")]
    public async Task Invalid_options_fail_host_start_before_any_request(string key, string? value, string expected)
    {
        using var host = CreateHost(new Dictionary<string, string?> { [key] = value });
        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public async Task An_absent_signing_key_fails_host_start_naming_the_environment_variable()
    {
        using var host = CreateHost([], "Jwt:SigningKey");
        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains("Jwt__SigningKey", exception.Message);
    }
}
