using VibeRe.Application.Tools;
using VibeRe.Tools.Search;

namespace VibeRe.Tools.Tests;

/// <summary>Runs only where ripgrep is available (<c>rg</c> on the PATH or <c>VIBE_RE_RG_PATH</c>).</summary>
public sealed class RipgrepFactAttribute : FactAttribute
{
    public RipgrepFactAttribute()
    {
        var configured = Environment.GetEnvironmentVariable(Ripgrep.PathEnvironmentVariable);
        var onPath = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator)
            .Any(directory => File.Exists(Path.Combine(directory, "rg")));
        if (!onPath && !File.Exists(configured))
        {
            Skip = $"ripgrep not found; install it or set {Ripgrep.PathEnvironmentVariable}.";
        }
    }
}

public class SearchToolTests
{
    private static TempWorkspace CreateRepository()
    {
        var workspace = new TempWorkspace();
        workspace.Write("src/App.cs", "class App\n{\n    // TODO: wire tools\n}\n");
        workspace.Write("src/Tools/Grep.cs", "class Grep { } // todo\n");
        workspace.Write("README.md", "No todos here.\n");
        workspace.Write("bin/Generated.cs", "// TODO generated\n");
        workspace.Write(".gitignore", "bin/\n");
        workspace.Write(".git/HEAD", "TODO\n");
        Directory.CreateDirectory(Path.Combine(workspace.Root, ".git"));
        return workspace;
    }

    [RipgrepFact]
    public async Task Grep_lists_matching_files_respecting_gitignore()
    {
        using var workspace = CreateRepository();
        var grep = new GrepTool(new Ripgrep());

        Assert.Equal(new ToolResult("src/App.cs"), await workspace.InvokeAsync(grep, """{"pattern":"TODO"}"""));
        Assert.Equal(new ToolResult("README.md\nsrc/App.cs\nsrc/Tools/Grep.cs"),
            await workspace.InvokeAsync(grep, """{"pattern":"todo","ignore_case":true}"""));
        Assert.Equal(new ToolResult("src/Tools/Grep.cs"),
            await workspace.InvokeAsync(grep, """{"pattern":"todo","path":"src/Tools"}"""));
        Assert.Equal(new ToolResult("No matches found."), await workspace.InvokeAsync(grep, """{"pattern":"nothing"}"""));
    }

    [RipgrepFact]
    public async Task Grep_shows_content_counts_and_truncates()
    {
        using var workspace = CreateRepository();
        var grep = new GrepTool(new Ripgrep());

        Assert.Equal(new ToolResult("src/App.cs:3:    // TODO: wire tools"),
            await workspace.InvokeAsync(grep, """{"pattern":"TODO","output_mode":"content"}"""));
        Assert.Equal(new ToolResult("src/App.cs-2-{\nsrc/App.cs:3:    // TODO: wire tools\nsrc/App.cs-4-}"),
            await workspace.InvokeAsync(grep, """{"pattern":"TODO","output_mode":"content","context":1}"""));
        Assert.Equal(new ToolResult("src/App.cs:1\nsrc/Tools/Grep.cs:1"),
            await workspace.InvokeAsync(grep, """{"pattern":"class","output_mode":"count"}"""));
        Assert.Equal(new ToolResult("src/App.cs\n... results truncated at 1 lines; narrow the search."),
            await workspace.InvokeAsync(grep, """{"pattern":"class","max_results":1}"""));
        Assert.StartsWith("ripgrep failed:", (await workspace.InvokeAsync(grep, """{"pattern":"("}""")).Content);
    }

    [RipgrepFact]
    public async Task Find_files_matches_globs()
    {
        using var workspace = CreateRepository();
        var find = new FindFilesTool(new Ripgrep());

        Assert.Equal(new ToolResult("src/App.cs\nsrc/Tools/Grep.cs"), await workspace.InvokeAsync(find, """{"pattern":"*.cs"}"""));
        Assert.Equal(new ToolResult("src/Tools/Grep.cs"), await workspace.InvokeAsync(find, """{"pattern":"**/Tools/*.cs"}"""));
        Assert.Equal(new ToolResult("src/Tools/Grep.cs"),
            await workspace.InvokeAsync(find, """{"pattern":"src/Tools/*.cs","path":"src"}"""));
        Assert.Equal(new ToolResult("src/Tools/Grep.cs"),
            await workspace.InvokeAsync(find, """{"pattern":"*.cs","path":"src/Tools"}"""));
        Assert.Equal(new ToolResult("No files found."), await workspace.InvokeAsync(find, """{"pattern":"*.py"}"""));
    }

    [Fact]
    public async Task A_missing_ripgrep_is_explained()
    {
        using var workspace = new TempWorkspace();

        var result = await workspace.InvokeAsync(new GrepTool(new Ripgrep("/nonexistent/rg")), """{"pattern":"x"}""");

        Assert.True(result.IsError);
        Assert.Contains("brew install ripgrep", result.Content);
    }
}
