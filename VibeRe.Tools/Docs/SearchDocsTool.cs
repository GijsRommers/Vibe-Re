using System.ComponentModel;
using System.Text;
using VibeRe.Application.Tools;

namespace VibeRe.Tools.Docs;

public sealed record SearchDocsArguments(
    [property: Description("Keywords to look for, e.g. 'mcp server headers' or 'approval deny'.")] string Query);

public sealed class SearchDocsTool(DocsLibrary docs) : BuiltinTool<SearchDocsArguments>
{
    private const int MaxSections = 5;
    private const int MaxSectionLength = 1500;

    public override string Name => "search_docs";

    public override string Description =>
        "Searches VibeRe's own documentation: installing, signing in, configuration, built-in tools, approvals and " +
        "MCP servers. Use it when the user asks how VibeRe itself works instead of answering from memory.";

    protected override Task<ToolResult> InvokeAsync(
        SearchDocsArguments arguments,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var sections = docs.Search(arguments.Query, MaxSections);
        if (sections.Count == 0)
        {
            return Task.FromResult(new ToolResult(
                $"No documentation matches '{arguments.Query}'. Pages: " +
                string.Join(", ", docs.Pages.Select(page => page.Path)) + ". Use read_doc to read one."));
        }

        var output = new StringBuilder();
        foreach (var section in sections)
        {
            var content = section.Content.Length > MaxSectionLength
                ? section.Content[..MaxSectionLength] + "\n[section truncated; use read_doc for the full page]"
                : section.Content;
            output.Append($"## {section.Page.Path} › {section.Heading}\n{content}\n\n");
        }

        return Task.FromResult(new ToolResult(output.ToString().TrimEnd()));
    }
}
