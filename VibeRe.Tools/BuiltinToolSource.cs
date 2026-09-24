using VibeRe.Application.Tools;
using VibeRe.Tools.Files;
using VibeRe.Tools.Search;

namespace VibeRe.Tools;

/// <summary>The tools VibeRe ships with.</summary>
public sealed class BuiltinToolSource(Ripgrep ripgrep) : IToolSource
{
    private readonly IReadOnlyList<ITool> _tools =
    [
        new ReadFileTool(),
        new CreateFileTool(),
        new EditFileTool(),
        new ListDirectoryTool(),
        new GrepTool(ripgrep),
        new FindFilesTool(ripgrep),
    ];

    public Task<IReadOnlyList<ITool>> GetToolsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_tools);
}
