using System.ComponentModel;
using System.Text;
using VibeRe.Application.Tools;

namespace VibeRe.Tools.Files;

public sealed record ListDirectoryArguments(
    [property: Description("Directory path, relative to the working directory. Defaults to the working directory.")]
    string? Path = null);

public sealed class ListDirectoryTool : BuiltinTool<ListDirectoryArguments>
{
    private const int MaxEntries = 1000;

    public override string Name => "list_directory";

    public override string Description =>
        "Lists the entries of one directory (not recursive). Directories end with '/'. " +
        "Use find_files to search recursively.";

    protected override Task<ToolResult> InvokeAsync(
        ListDirectoryArguments arguments,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var path = Workspace.Resolve(context, arguments.Path);
        if (!Directory.Exists(path))
        {
            throw new ToolException($"'{arguments.Path}' is not a directory.");
        }

        var entries = new DirectoryInfo(path).EnumerateFileSystemInfos()
            .Select(entry => entry is DirectoryInfo ? entry.Name + "/" : entry.Name)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (entries.Count == 0)
        {
            return Task.FromResult(new ToolResult("(empty directory)"));
        }

        var output = new StringBuilder(string.Join('\n', entries.Take(MaxEntries)));
        if (entries.Count > MaxEntries)
        {
            output.Append($"\n... and {entries.Count - MaxEntries} more entries.");
        }

        return Task.FromResult(new ToolResult(output.ToString()));
    }
}
