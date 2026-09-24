using Microsoft.Extensions.DependencyInjection;
using VibeRe.Application.Tools;
using VibeRe.Infrastructure.Mcp;
using VibeRe.Infrastructure.Persistence;

namespace VibeRe.Infrastructure.Tests.Mcp;

public class McpToolTests
{
    [Theory]
    [InlineData("github", "create_issue", "mcp__github__create_issue")]
    [InlineData("fs", "files.read/v2", "mcp__fs__files_read_v2")]
    public void Mcp_tools_are_namespaced_by_server(string server, string tool, string expected)
    {
        Assert.Equal(expected, McpTool.QualifiedName(server, tool));
        Assert.Equal(64, McpTool.QualifiedName("s", new string('x', 100)).Length);
    }

    [Fact]
    public async Task Mcp_registration_adds_a_tool_source()
    {
        await using var provider = new ServiceCollection()
            .AddVibeRePersistence("Data Source=:memory:")
            .AddVibeReMcp()
            .BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        Assert.IsType<McpToolSource>(Assert.Single(scope.ServiceProvider.GetServices<IToolSource>()));
    }
}
