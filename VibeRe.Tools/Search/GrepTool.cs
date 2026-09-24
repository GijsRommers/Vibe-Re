using System.ComponentModel;
using VibeRe.Application.Tools;

namespace VibeRe.Tools.Search;

public enum GrepOutputMode
{
    FilesWithMatches,
    Content,
    Count,
}

public sealed record GrepArguments(
    [property: Description("Regular expression to search for (Rust regex syntax).")] string Pattern,
    [property: Description("File or directory to search, relative to the working directory. Defaults to the working directory.")]
    string? Path = null,
    [property: Description("Only search files matching this glob, e.g. '*.cs' or 'src/**/*.{ts,tsx}'.")]
    string? Glob = null,
    [property: Description("Match case-insensitively. Defaults to false.")] bool IgnoreCase = false,
    [property: Description("files_with_matches (default) lists matching files, content shows matching lines, count shows matches per file.")]
    GrepOutputMode OutputMode = GrepOutputMode.FilesWithMatches,
    [property: Description("Lines of context around each match in content mode.")] int? Context = null,
    [property: Description("Maximum number of output lines. Defaults to 200.")] int? MaxResults = null);

public sealed class GrepTool(Ripgrep ripgrep) : BuiltinTool<GrepArguments>
{
    private const int DefaultMaxResults = 200;
    private const int MaxMaxResults = 2000;

    public override string Name => "grep";

    public override string Description =>
        "Searches file contents with ripgrep. Respects .gitignore and skips .git. Prefer this over reading " +
        "files one by one to find where something is defined or used.";

    protected override async Task<ToolResult> InvokeAsync(
        GrepArguments arguments,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var path = Workspace.Resolve(context, arguments.Path);
        List<string> rg = arguments.OutputMode switch
        {
            GrepOutputMode.Content => ["--line-number", "--no-heading", "--with-filename"],
            GrepOutputMode.Count => ["--count", "--with-filename"],
            _ => ["--files-with-matches"],
        };
        if (arguments.OutputMode == GrepOutputMode.Content && arguments.Context is > 0 and var lines)
        {
            rg.Add($"--context={lines}");
        }

        if (arguments.IgnoreCase)
        {
            rg.Add("--ignore-case");
        }

        if (!string.IsNullOrWhiteSpace(arguments.Glob))
        {
            rg.Add($"--glob={arguments.Glob}");
        }

        rg.Add($"--regexp={arguments.Pattern}");

        var maxResults = Math.Clamp(arguments.MaxResults ?? DefaultMaxResults, 1, MaxMaxResults);
        var output = await ripgrep.RunAsync(
            rg,
            Path.GetFullPath(context.WorkingDirectory),
            Workspace.Display(context, path),
            maxResults,
            cancellationToken);
        return output.Lines.Count == 0
            ? new ToolResult("No matches found.")
            : new ToolResult(string.Join('\n', output.Lines) +
                             (output.Truncated ? $"\n... results truncated at {maxResults} lines; narrow the search." : ""));
    }
}
