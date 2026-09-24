using System.ComponentModel;
using VibeRe.Application.Tools;

namespace VibeRe.Tools.Files;

public sealed record CreateFileArguments(
    [property: Description("File path, relative to the working directory. Missing parent directories are created.")]
    string Path,
    [property: Description("The full contents of the file.")] string Content,
    [property: Description("Replace the file if it already exists. Defaults to false.")] bool Overwrite = false);

public sealed class CreateFileTool : BuiltinTool<CreateFileArguments>
{
    public override string Name => "create_file";

    public override string Description =>
        "Creates a file with the given contents. Fails if the file exists unless overwrite is true; " +
        "prefer edit_file for changing existing files.";

    protected override async Task<ToolResult> InvokeAsync(
        CreateFileArguments arguments,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var path = Workspace.Resolve(context, arguments.Path);
        if (Directory.Exists(path))
        {
            throw new ToolException($"'{arguments.Path}' is a directory.");
        }

        var existed = File.Exists(path);
        if (existed && !arguments.Overwrite)
        {
            throw new ToolException($"'{arguments.Path}' already exists. Use edit_file, or set overwrite to true.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, arguments.Content, cancellationToken);
        return new ToolResult($"{(existed ? "Overwrote" : "Created")} {Workspace.Display(context, path)}.");
    }
}
