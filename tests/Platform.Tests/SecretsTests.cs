using Platform.Tests.Infrastructure;

namespace Platform.Tests;

public sealed class SecretsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("changeme")]
    [InlineData("CHANGEME")]
    [InlineData("placeholder")]
    [InlineData("todo")]
    [InlineData("secret")]
    [InlineData("<replace-with-a-random-64-hex-audit-chain-key>")]
    [InlineData("changeme-changeme-changeme-changeme")]
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    public void HostRefusesToStartWithMissingWeakOrPlaceholderAuditKey(string? key)
    {
        using var factory = new PlatformFactory().WithSetting("Platform:AuditChainKey", key);
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    [Fact]
    public async Task HostStartsWithStrongRandomKey()
    {
        using var factory = new PlatformFactory().WithSetting("Platform:AuditChainKey", PlatformFactory.RandomHex(32));
        using var client = factory.CreateAnonymousClient();
        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative));
        Assert.True(response.IsSuccessStatusCode);
    }
}
