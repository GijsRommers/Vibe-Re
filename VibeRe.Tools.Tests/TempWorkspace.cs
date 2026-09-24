using System.Text.Json;
using VibeRe.Application.Tools;

namespace VibeRe.Tools.Tests;

internal sealed class TempWorkspace : IDisposable
{
    public TempWorkspace()
    {
        Root = Directory.CreateTempSubdirectory("vibe-re-tools-").FullName;
        Context = new ToolContext(Guid.NewGuid(), Root);
    }

    public string Root { get; }

    public ToolContext Context { get; }

    public string Write(string relativePath, string content)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public string Read(string relativePath) => File.ReadAllText(Path.Combine(Root, relativePath));

    public Task<ToolResult> InvokeAsync(ITool tool, string argumentsJson) =>
        tool.InvokeAsync(JsonDocument.Parse(argumentsJson).RootElement, Context);

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
