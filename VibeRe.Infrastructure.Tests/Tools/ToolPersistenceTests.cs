using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VibeRe.Domain.Tools;
using VibeRe.Infrastructure.Persistence;
using VibeRe.Infrastructure.Persistence.Repositories;

namespace VibeRe.Infrastructure.Tests.Tools;

public class ToolPersistenceTests
{
    [Fact]
    public async Task Mcp_servers_and_tool_policies_survive_reopening_sqlite()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VibeReDbContext>().UseSqlite(connection).Options;

        await using (var db = new VibeReDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            var servers = new McpServerRepository(db);
            await servers.AddAsync(McpServer.Stdio(
                "github", "npx", ["-y", "@modelcontextprotocol/server-github"], new Dictionary<string, string> { ["GITHUB_TOKEN"] = "t" }));
            await servers.AddAsync(McpServer.Http(
                "docs", new Uri("https://example.com/mcp"), new Dictionary<string, string> { ["Authorization"] = "Bearer x" }));
            await Assert.ThrowsAsync<DbUpdateException>(() => servers.AddAsync(McpServer.Stdio("docs", "other")));
            Assert.True(await servers.SetEnabledAsync("docs", false));

            var policies = new ToolPolicyStore(db);
            await policies.SetAsync("mcp__github__create_issue", ToolApproval.Allow);
            await policies.SetAsync("mcp__github__create_issue", ToolApproval.Deny);
            await policies.SetAsync("read_file", ToolApproval.Allow);
        }

        await using (var db = new VibeReDbContext(options))
        {
            var servers = new McpServerRepository(db);
            var github = await servers.GetByNameAsync("github");
            Assert.NotNull(github);
            Assert.Equal(McpTransport.Stdio, github.Transport);
            Assert.Equal(["-y", "@modelcontextprotocol/server-github"], github.Arguments);
            Assert.Equal("t", github.Environment["GITHUB_TOKEN"]);
            var docs = await servers.GetByNameAsync("docs");
            Assert.False(docs?.Enabled);
            Assert.Equal("Bearer x", docs?.Headers["Authorization"]);
            Assert.Equal(["docs", "github"], (await servers.ListAsync()).Select(server => server.Name));
            Assert.True(await servers.DeleteAsync("docs"));
            Assert.False(await servers.DeleteAsync("docs"));

            var policies = new ToolPolicyStore(db);
            Assert.Equal(ToolApproval.Deny, (await policies.GetAllAsync())["mcp__github__create_issue"]);
            Assert.True(await policies.DeleteAsync("read_file"));
            Assert.Single(await policies.GetAllAsync());
        }
    }

    [Theory]
    [InlineData("bad name")]
    [InlineData("Upper")]
    [InlineData("a__b")]
    [InlineData("")]
    public void Server_names_must_be_safe_to_embed_in_tool_names(string name)
    {
        Assert.ThrowsAny<ArgumentException>(() => McpServer.Stdio(name, "npx"));
    }
}
