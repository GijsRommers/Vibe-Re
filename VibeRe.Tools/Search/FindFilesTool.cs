using System.ComponentModel;
using VibeRe.Application.Tools;

namespace VibeRe.Tools.Search;

public sealed record FindFilesArguments(
    [property: Description("Glob to match file paths against, e.g. '*.cs' (any depth), 'src/**/*.ts' or '**/Tests/*.cs'. Globs containing '/' match from the working directory.")]
    string Pattern,
    [property: Description("Directory to search, relative to the working directory. Defaults to the working directory.")]
    string? Path = null,
    [property: Description("Maximum number of files to return. Defaults to 500.")] int? MaxResults = null);

public sealed class FindFilesTool(Ripgrep ripgrep) : BuiltinTool<FindFilesArguments>
{
    private const int DefaultMaxResults = 500;
    private const int MaxMaxResults = 5000;

    public override string Name => "find_files";

    public override string Description =>
        "Finds files by name with a glob, recursively. Respects .gitignore and skips .git.";

    protected override async Task<ToolResult> InvokeAsync(
        FindFilesArguments arguments,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var path = Workspace.Resolve(context, arguments.Path);
        var maxResults = Math.Clamp(arguments.MaxResults ?? DefaultMaxResults, 1, MaxMaxResults);
        var output = await ripgrep.RunAsync(
            ["--files", $"--glob={arguments.Pattern}"],
            Path.GetFullPath(context.WorkingDirectory),
            Workspace.Display(context, path),
            maxResults,
            cancellationToken);
        return output.Lines.Count == 0
            ? new ToolResult("No files found.")
            : new ToolResult(string.Join('\n', output.Lines) +
                             (output.Truncated ? $"\n... results truncated at {maxResults} files; narrow the pattern." : ""));
    }
}
