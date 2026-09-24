using VibeRe.Application.Projects;
using VibeRe.Domain.Projects;
using VibeRe.Infrastructure.Projects;

namespace VibeRe.Infrastructure.Tests.Projects;

public sealed class ProjectConfigFileLoaderTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("vibe-re-tests").FullName;
    private readonly ProjectConfigFileLoader _loader = new();

    public ProjectConfigFileLoaderTests() => Directory.CreateDirectory(Path.Combine(_repo, ".git"));

    public void Dispose() => Directory.Delete(_repo, recursive: true);

    [Fact]
    public async Task Reads_actions_from_the_nearest_file_with_comments_and_defaults()
    {
        await File.WriteAllTextAsync(Path.Combine(_repo, ProjectConfig.FileName), """
            {
              // Set up new worktrees, then let the model build.
              "actions": [
                { "name": "setup", "command": "dotnet restore", "runOnWorktreeCreate": true, "tool": false },
                { "name": "build", "command": "make build", "description": "Build everything" },
              ],
            }
            """);
        var nested = Directory.CreateDirectory(Path.Combine(_repo, "src", "app")).FullName;

        var config = await _loader.FindAsync(nested);

        Assert.NotNull(config);
        Assert.Equal(_repo, config.RootDirectory.TrimEnd(Path.DirectorySeparatorChar));
        Assert.Collection(config.Actions,
            setup =>
            {
                Assert.Equal(("setup", "dotnet restore", null), (setup.Name, setup.Command, setup.Description));
                Assert.True(setup.RunOnWorktreeCreate);
                Assert.False(setup.Tool);
            },
            build =>
            {
                Assert.Equal(("build", "make build", "Build everything"), (build.Name, build.Command, build.Description));
                Assert.False(build.RunOnWorktreeCreate);
                Assert.True(build.Tool);
            });
    }

    [Fact]
    public async Task Does_not_look_past_the_repository_root()
    {
        var outer = Directory.CreateTempSubdirectory("vibe-re-tests").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(outer, ProjectConfig.FileName), """{ "actions": [] }""");
            var inner = Directory.CreateDirectory(Path.Combine(outer, "repo", "src")).FullName;
            await File.WriteAllTextAsync(Path.Combine(outer, "repo", ".git"), "gitdir: /elsewhere");

            Assert.Null(await _loader.FindAsync(inner));
            Assert.Null(await _loader.FindAsync(_repo));
        }
        finally
        {
            Directory.Delete(outer, recursive: true);
        }
    }

    [Theory]
    [InlineData("""{ "actions": [{ "name": "build", "comand": "make" }] }""")]
    [InlineData("""{ "actions": [{ "name": "build" }] }""")]
    [InlineData("""{ "actions": [{ "name": null, "command": "make" }] }""")]
    [InlineData("""{ "actions": [{ "name": "Build It", "command": "make" }] }""")]
    [InlineData("""{ "actions": [{ "name": "a__b", "command": "make" }] }""")]
    [InlineData("""{ "actions": [{ "name": "x", "command": "a" }, { "name": "x", "command": "b" }] }""")]
    [InlineData("""{ "actions": """)]
    public async Task Invalid_files_are_reported_with_their_path(string json)
    {
        var path = Path.Combine(_repo, ProjectConfig.FileName);
        await File.WriteAllTextAsync(path, json);

        var exception = await Assert.ThrowsAsync<ProjectConfigException>(() => _loader.FindAsync(_repo));

        Assert.StartsWith(path, exception.Message);
    }
}
