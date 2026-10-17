namespace VibeRe.Client.Tests;

public sealed class ServerAddressTests
{
    [Theory]
    [InlineData("http://remote.example")]
    [InlineData("ftp://remote.example")]
    [InlineData("https://user:password@remote.example")]
    [InlineData("https://remote.example/path")]
    [InlineData("https://remote.example?token=secret")]
    [InlineData("https://remote.example#fragment")]
    public void DirectClientRejectsUnsafeOrNonOriginEndpoints(string address) =>
        Assert.Throws<ArgumentException>(() => new VibeReClient(address));

    [Theory]
    [InlineData(" https://REMOTE.example:443/ ", "https://remote.example")]
    [InlineData("http://localhost:5067/", "http://localhost:5067")]
    [InlineData("http://127.0.0.1:5067", "http://127.0.0.1:5067")]
    [InlineData("http://[::1]:5067", "http://[::1]:5067")]
    public void DirectClientAcceptsHttpsAndLoopbackHttp(string address, string normalized)
    {
        Assert.Equal(normalized, ServerAddress.Normalize(address));
        using var client = new VibeReClient(address);
    }
}
