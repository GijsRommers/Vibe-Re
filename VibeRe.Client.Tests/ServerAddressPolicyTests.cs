using VibeRe.Client.Application;
using VibeRe.Client.Application.Configuration;

namespace VibeRe.Client.Tests;

public class ServerAddressPolicyTests
{
    [Theory]
    [InlineData("http://localhost:5067")]
    [InlineData("http://127.0.0.1:5067")]
    [InlineData("http://[::1]:5067")]
    [InlineData("http://[::ffff:127.0.0.1]:5067")]
    public void LiteralLoopbackOriginsCanStartLocally(string address) =>
        Assert.True(ServerAddress.IsLoopback(address));

    [Theory]
    [InlineData("http://loopback:5067")]
    [InlineData("http://localhost.example:5067")]
    [InlineData("http://0.0.0.0:5067")]
    [InlineData("http://[::]:5067")]
    [InlineData("http://192.168.1.10:5067")]
    [InlineData("http://user@localhost:5067")]
    public void OtherHostsCannotBeTreatedAsLocal(string address)
    {
        Assert.False(ServerAddress.IsLoopback(address));
        Assert.Throws<ArgumentException>(() => ServerAddress.Normalize(address));
    }

    [Fact]
    public void HostDecidesWhetherDefaultConnectionCanLaunchProcess()
    {
        Assert.True(ClientConfiguration.Default(startLocally: true).Find().StartLocally);
        Assert.False(ClientConfiguration.Default(startLocally: false).Find().StartLocally);
    }
}
