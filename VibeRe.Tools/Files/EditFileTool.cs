using System.ComponentModel;
using VibeRe.Application.Tools;

namespace VibeRe.Tools.Files;

public sealed record EditFileArguments(
    [property: Description("File path, relative to the working directory.")] string Path,
    [property: Description("Exact text to replace, including whitespace and indentation.")] string OldString,
    [property: Description("Text to replace it with.")] string NewString,
    [property: Description("Replace every occurrence instead of requiring exactly one. Defaults to false.")]
    bool ReplaceAll = false);

public sealed class EditFileTool : BuiltinTool<EditFileArguments>
{
    public override string Name => "edit_file";

    public override string Description =>
        "Replaces exact text in an existing file. old_string must occur exactly once unless replace_all is true, " +
        "so include enough surrounding lines to make it unique.";

    protected override async Task<ToolResult> InvokeAsync(
        EditFileArguments arguments,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var path = Workspace.Resolve(context, arguments.Path);
        if (!File.Exists(path))
        {
            throw new ToolException($"'{arguments.Path}' does not exist. Use create_file to create it.");
        }

        if (arguments.OldString.Length == 0)
        {
            throw new ToolException("old_string cannot be empty.");
        }

        if (arguments.OldString == arguments.NewString)
        {
            throw new ToolException("old_string and new_string are identical.");
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken);
        var occurrences = CountOccurrences(content, arguments.OldString);
        if (occurrences == 0)
        {
            throw new ToolException($"old_string was not found in '{arguments.Path}'.");
        }

        if (occurrences > 1 && !arguments.ReplaceAll)
        {
            throw new ToolException(
                $"old_string occurs {occurrences} times in '{arguments.Path}'. " +
                "Add surrounding context to make it unique, or set replace_all to true.");
        }

        await File.WriteAllTextAsync(
            path,
            content.Replace(arguments.OldString, arguments.NewString, StringComparison.Ordinal),
            cancellationToken);
        return new ToolResult(
            $"Replaced {occurrences} occurrence{(occurrences == 1 ? "" : "s")} in {Workspace.Display(context, path)}.");
    }

    private static int CountOccurrences(string content, string value)
    {
        var count = 0;
        for (var index = content.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = content.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
