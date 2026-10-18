using VibeRe.Web.Services;

namespace VibeRe.Web.Tests;

public class BrowserServerTests
{
    [Theory]
    [InlineData("https://vibe.example.com/other/page?token=ignored#fragment", "https://vibe.example.com/")]
    [InlineData("https://vibe.example.com:7443/", "https://vibe.example.com:7443/")]
    [InlineData("http://localhost:5067/", "http://localhost:5067/")]
    public void ConfigurationUsesOnlyDocumentOrigin(string documentAddress, string expected)
    {
        var server = new BrowserServer(documentAddress);
        Assert.Equal(expected, server.Origin.AbsoluteUri);
        Assert.False(server.Connection.StartLocally);
        Assert.False(server.Connection.HasAccessToken);
    }

    [Fact]
    public void RemotePlaintextOriginsAreRejected() =>
        Assert.Throws<ArgumentException>(() => new BrowserServer("http://192.168.1.2:5067/"));

    [Theory]
    [InlineData("http://vibe.example.com")]
    [InlineData("http://localhost:5067")]
    [InlineData("https://user:secret@vibe.example.com")]
    [InlineData("https://vibe.example.com/path")]
    [InlineData("https://vibe.example.com?token=secret")]
    [InlineData("https://vibe.example.com/#fragment")]
    [InlineData("javascript:alert(1)")]
    public void ChangingServerRequiresCleanHttpsOrigin(string address) =>
        Assert.Throws<ArgumentException>(() => BrowserServer.NavigationTarget(address));

    [Fact]
    public async Task ConfigurationCannotRetainMutations()
    {
        var store = new CurrentOriginConfiguration(new BrowserServer("https://vibe.example.com/"));
        (await store.LoadAsync()).Servers.Clear();
        var current = await store.LoadAsync();
        Assert.Single(current.Servers);
        Assert.True(current.ServerRequired);
        await Assert.ThrowsAsync<NotSupportedException>(() => store.UpdateAsync(config => Task.FromResult(config)));
    }

    [Fact]
    public async Task ConnectorRejectsTokensAndOtherServerNames()
    {
        var connector = new BrowserServerConnector(new BrowserServer("https://vibe.example.com/"));
        await Assert.ThrowsAsync<ArgumentException>(() => connector.ConnectAsync(tokenOverride: "secret"));
        await Assert.ThrowsAsync<ArgumentException>(() => connector.ConnectAsync(name: "other"));
    }

    [Fact]
    public async Task BrowserSessionSupportsAsynchronousDisposalWithoutConnecting()
    {
        var connector = new BrowserServerConnector(new BrowserServer("https://vibe.example.com/"));
        var connection = await connector.ConnectAsync();
        await using var client = connection.Client;
        Assert.Equal("https://vibe.example.com", connection.Server.Address);
    }

    [Fact]
    public void BrowserAssemblyHasNoNativeInfrastructureDependency()
    {
        Assert.DoesNotContain(typeof(BrowserServer).Assembly.GetReferencedAssemblies(), dependency =>
            dependency.Name is "VibeRe.Client.Infrastructure" or "Microsoft.Data.Sqlite" or "SharpKeyring" or "VibeRe.Server");
    }
}
