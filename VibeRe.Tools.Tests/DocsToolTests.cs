using VibeRe.Application.Tools;
using VibeRe.Tools.Docs;

namespace VibeRe.Tools.Tests;

public class DocsToolTests
{
    private static readonly DocsLibrary Library = new([
        new DocPage("tools.md", "# Tools\n\nIntro.\n\n## Approvals\n\nAsk, Allow or Deny each tool.\n\n```sh\n# not a heading\n```\n"),
        new DocPage("mcp.md", "# MCP servers\n\n## Configuration\n\nServers are configured in SQLite. Tools need approval too.\n"),
    ]);

    [Fact]
    public void Search_ranks_heading_matches_and_stems_terms()
    {
        Assert.Equal(["Approvals", "Configuration"], Library.Search("how do I approve?", 5).Select(section => section.Heading));
        Assert.Equal("Configuration", Assert.Single(Library.Search("configure", 5)).Heading);
        Assert.Contains("# not a heading", Library.Search("deny", 5)[0].Content);
        Assert.Empty(Library.Search("how do I", 5));
    }

    [Fact]
    public async Task Search_docs_returns_sections_or_the_page_list()
    {
        using var workspace = new TempWorkspace();
        var tool = new SearchDocsTool(Library);

        Assert.Equal(new ToolResult("## tools.md › Approvals\nAsk, Allow or Deny each tool.\n\n```sh\n# not a heading\n```"),
            await workspace.InvokeAsync(tool, """{"query":"deny"}"""));
        Assert.Equal(new ToolResult("No documentation matches 'kubernetes'. Pages: mcp.md, tools.md. Use read_doc to read one."),
            await workspace.InvokeAsync(tool, """{"query":"kubernetes"}"""));
    }

    [Fact]
    public async Task Read_doc_reads_pages_by_loose_path_or_lists_them()
    {
        using var workspace = new TempWorkspace();
        var tool = new ReadDocTool(Library);

        Assert.Equal("mcp.md – MCP servers\ntools.md – Tools", (await workspace.InvokeAsync(tool, "{}")).Content);
        Assert.StartsWith("# MCP servers", (await workspace.InvokeAsync(tool, """{"path":"docs/MCP"}""")).Content);
        Assert.True((await workspace.InvokeAsync(tool, """{"path":"missing.md"}""")).IsError);
    }

    [Fact]
    public void The_user_guide_is_embedded()
    {
        var docs = DocsLibrary.LoadEmbedded();

        Assert.Equal(["getting-started.md", "mcp.md", "tools.md"], docs.Pages.Select(page => page.Path));
        Assert.Equal("mcp.md", docs.Search("mcp server headers", 1)[0].Page.Path);
        Assert.Equal("getting-started.md", docs.Search("MISTRAL_API_KEY", 1)[0].Page.Path);
    }
}
