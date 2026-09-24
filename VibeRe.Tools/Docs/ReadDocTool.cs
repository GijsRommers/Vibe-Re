using System.ComponentModel;
using VibeRe.Application.Tools;

namespace VibeRe.Tools.Docs;

public sealed record ReadDocArguments(
    [property: Description("Page to read, e.g. 'tools.md'. Omit to list all pages.")] string? Path = null);

public sealed class ReadDocTool(DocsLibrary docs) : BuiltinTool<ReadDocArguments>
{
    public override string Name => "read_doc";

    public override string Description =>
        "Reads a page of VibeRe's own documentation in full, or lists the pages when no path is given.";

    protected override Task<ToolResult> InvokeAsync(
        ReadDocArguments arguments,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(arguments.Path))
        {
            return Task.FromResult(new ToolResult(PageList()));
        }

        return Task.FromResult(docs.Find(arguments.Path) is { } page
            ? new ToolResult(page.Content)
            : ToolResult.Error($"There is no documentation page '{arguments.Path}'. Pages:\n{PageList()}"));
    }

    private string PageList() => string.Join('\n', docs.Pages.Select(page => $"{page.Path} – {page.Title}"));
}
