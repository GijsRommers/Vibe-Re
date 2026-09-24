using System.Text.Json;
using VibeRe.Application.Projects;
using VibeRe.Application.Tools;
using VibeRe.Domain.Projects;
using VibeRe.Domain.Tools;
using VibeRe.Infrastructure.Projects;

namespace VibeRe.Infrastructure.Tests.Projects;

public sealed class ProjectActionToolSourceTests : IDisposable
{
    private static readonly JsonElement NoArguments = JsonDocument.Parse("{}").RootElement;

    private readonly string _repo = Directory.CreateTempSubdirectory("vibe-re-tests").FullName;
    private readonly ProjectActionToolSource _source =
        new(new ProjectActions(new ProjectConfigFileLoader(), new ShellCommandRunner()));

    public ProjectActionToolSourceTests() => Directory.CreateDirectory(Path.Combine(_repo, ".git"));

    public void Dispose() => Directory.Delete(_repo, recursive: true);

    [Fact]
    public async Task Actions_become_tools_that_run_from_the_project_root()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // POSIX shell syntax.
        }

        await WriteConfigAsync("""
            { "actions": [
              { "name": "where", "command": "echo \"$PWD|$VIBE_RE_PROJECT_ROOT\"", "description": "Print the directory." },
              { "name": "fail", "command": "echo nope; exit 2" },
              { "name": "dev", "command": "sleep 1000", "tool": false }
            ] }
            """);
        var context = new ToolContext(Guid.NewGuid(), Directory.CreateDirectory(Path.Combine(_repo, "src")).FullName);
        var registry = new ToolRegistry([_source], new AllowAll());

        var tools = await registry.GetAvailableAsync(context);
        var where = await registry.InvokeAsync("project__where", NoArguments, context, new AllowAll());
        var fail = await registry.InvokeAsync("project__fail", NoArguments, context, new AllowAll());

        Assert.Equal(["project__fail", "project__where"], tools.Select(tool => tool.Name));
        Assert.StartsWith("Print the directory.", tools[1].Description);
        Assert.False(where.IsError);
        Assert.Equal(2, where.Content.Split(Path.GetFileName(_repo)).Length - 1);
        Assert.EndsWith("Exit code: 0", where.Content);
        Assert.True(fail.IsError);
        Assert.Equal("nope\n\nExit code: 2", fail.Content);
    }

    [Fact]
    public async Task No_file_or_an_invalid_file_gives_no_tools()
    {
        var context = new ToolContext(Guid.NewGuid(), _repo);
        Assert.Empty(await _source.GetToolsAsync(context));

        await WriteConfigAsync("""{ "actions": [{ "name": "build" }] }""");
        Assert.Empty(await _source.GetToolsAsync(context));
    }

    private Task WriteConfigAsync(string json) =>
        File.WriteAllTextAsync(Path.Combine(_repo, ProjectConfig.FileName), json);

    private sealed class AllowAll : IToolPolicyStore, IToolApprover
    {
        public Task<IReadOnlyDictionary<string, ToolApproval>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, ToolApproval>>(new Dictionary<string, ToolApproval>());

        public Task SetAsync(string toolName, ToolApproval approval, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> DeleteAsync(string toolName, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> ApproveAsync(ITool tool, JsonElement arguments, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }
}
