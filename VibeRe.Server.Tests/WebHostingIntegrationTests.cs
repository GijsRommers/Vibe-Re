using System.Net;
using Microsoft.AspNetCore.Hosting;

namespace VibeRe.Server.Tests;

public class WebHostingIntegrationTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task ReferencedWebAssetsAreServedWithoutDependingOnALaunchProfile(string environment)
    {
        using var factory = new SignalRTestServer();
        using var server = factory.WithWebHostBuilder(builder => builder.UseEnvironment(environment));
        using var client = server.CreateClient();
        using var root = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        Assert.Equal("text/html", root.Content.Headers.ContentType?.MediaType);
        Assert.Contains("_framework/blazor.webassembly.js", await root.Content.ReadAsStringAsync());
        foreach (var path in new[] { "/app.css", "/mistral.svg", "/_framework/blazor.webassembly.js" })
        {
            using var asset = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        }
        foreach (var path in new[] { "/api/unknown", "/hubs/unknown" })
        {
            using var missing = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
    }
}
