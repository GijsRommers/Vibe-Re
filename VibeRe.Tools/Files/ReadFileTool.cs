using System.ComponentModel;
using System.Text;
using VibeRe.Application.Tools;

namespace VibeRe.Tools.Files;

public sealed record ReadFileArguments(
    [property: Description("File path, relative to the working directory.")] string Path,
    [property: Description("1-based line to start at. Defaults to 1.")] int? Offset = null,
    [property: Description("Maximum number of lines to return. Defaults to 2000.")] int? Limit = null);

public sealed class ReadFileTool : BuiltinTool<ReadFileArguments>
{
    private const int DefaultLimit = 2000;
    private const int MaxLineLength = 2000;

    public override string Name => "read_file";

    public override string Description =>
        "Reads a text file and returns its lines prefixed with 1-based line numbers. " +
        "Use offset and limit to page through large files.";

    protected override async Task<ToolResult> InvokeAsync(
        ReadFileArguments arguments,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var path = Workspace.Resolve(context, arguments.Path);
        var offset = Math.Max(arguments.Offset ?? 1, 1);
        var limit = Math.Clamp(arguments.Limit ?? DefaultLimit, 1, DefaultLimit);
        if (Directory.Exists(path))
        {
            throw new ToolException($"'{arguments.Path}' is a directory. Use list_directory instead.");
        }

        if (!File.Exists(path))
        {
            throw new ToolException($"'{arguments.Path}' does not exist.");
        }

        if (await LooksBinaryAsync(path, cancellationToken))
        {
            throw new ToolException($"'{arguments.Path}' is a binary file.");
        }

        var output = new StringBuilder();
        var lineNumber = 0;
        await foreach (var line in File.ReadLinesAsync(path, cancellationToken))
        {
            lineNumber++;
            if (lineNumber < offset)
            {
                continue;
            }

            if (lineNumber >= offset + limit)
            {
                output.Append($"... more lines follow; continue with offset {lineNumber}.");
                return new ToolResult(output.ToString());
            }

            var text = line.Length > MaxLineLength ? line[..MaxLineLength] + " [line truncated]" : line;
            output.Append($"{lineNumber,6}\t{text}\n");
        }

        if (lineNumber == 0)
        {
            return new ToolResult("(empty file)");
        }

        return offset > lineNumber
            ? ToolResult.Error($"Offset {offset} is past the end of the file ({lineNumber} lines).")
            : new ToolResult(output.ToString());
    }

    private static async Task<bool> LooksBinaryAsync(string path, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        await using var stream = File.OpenRead(path);
        var read = await stream.ReadAsync(buffer, cancellationToken);
        return buffer.AsSpan(0, read).Contains((byte)0);
    }
}
