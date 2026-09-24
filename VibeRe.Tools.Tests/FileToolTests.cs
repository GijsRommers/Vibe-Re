using VibeRe.Application.Tools;
using VibeRe.Tools.Files;

namespace VibeRe.Tools.Tests;

public class FileToolTests
{
    [Fact]
    public async Task Read_file_numbers_lines_and_pages()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("notes.txt", "one\ntwo\nthree\nfour\n");
        var tool = new ReadFileTool();

        Assert.Equal("     1\tone\n     2\ttwo\n     3\tthree\n     4\tfour\n",
            (await workspace.InvokeAsync(tool, """{"path":"notes.txt"}""")).Content);
        Assert.Equal("     2\ttwo\n     3\tthree\n... more lines follow; continue with offset 4.",
            (await workspace.InvokeAsync(tool, """{"path":"notes.txt","offset":2,"limit":2}""")).Content);
        Assert.True((await workspace.InvokeAsync(tool, """{"path":"notes.txt","offset":9}""")).IsError);
        Assert.True((await workspace.InvokeAsync(tool, """{"path":"missing.txt"}""")).IsError);
    }

    [Fact]
    public async Task Read_file_refuses_binary_files_and_directories()
    {
        using var workspace = new TempWorkspace();
        File.WriteAllBytes(Path.Combine(workspace.Root, "image.bin"), [0x89, 0x50, 0x00, 0x01]);
        Directory.CreateDirectory(Path.Combine(workspace.Root, "src"));

        Assert.Equal(ToolResult.Error("'image.bin' is a binary file."),
            await workspace.InvokeAsync(new ReadFileTool(), """{"path":"image.bin"}"""));
        Assert.True((await workspace.InvokeAsync(new ReadFileTool(), """{"path":"src"}""")).IsError);
    }

    [Fact]
    public async Task Paths_outside_the_working_directory_are_refused()
    {
        using var workspace = new TempWorkspace();
        var outside = Path.Combine(Path.GetTempPath(), "outside.txt");

        foreach (var path in new[] { "../outside.txt", outside, "src/../../outside.txt" })
        {
            var result = await workspace.InvokeAsync(new CreateFileTool(), $$"""{"path":"{{path}}","content":"x"}""");
            Assert.True(result.IsError);
            Assert.Contains("outside the working directory", result.Content);
        }

        Assert.False(File.Exists(outside));
    }

    [Fact]
    public async Task Create_file_makes_directories_and_only_overwrites_when_asked()
    {
        using var workspace = new TempWorkspace();
        var tool = new CreateFileTool();

        Assert.Equal(new ToolResult($"Created {Path.Combine("src", "app.cs")}."),
            await workspace.InvokeAsync(tool, """{"path":"src/app.cs","content":"v1"}"""));
        Assert.True((await workspace.InvokeAsync(tool, """{"path":"src/app.cs","content":"v2"}""")).IsError);
        Assert.Equal("v1", workspace.Read("src/app.cs"));

        Assert.False((await workspace.InvokeAsync(tool, """{"path":"src/app.cs","content":"v2","overwrite":true}""")).IsError);
        Assert.Equal("v2", workspace.Read("src/app.cs"));
    }

    [Fact]
    public async Task Edit_file_requires_a_unique_match_unless_replacing_all()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("app.cs", "var a = 1;\nvar b = 1;\n");
        var tool = new EditFileTool();

        var ambiguous = await workspace.InvokeAsync(tool, """{"path":"app.cs","old_string":"= 1","new_string":"= 2"}""");
        Assert.Equal(ToolResult.Error(
            "old_string occurs 2 times in 'app.cs'. Add surrounding context to make it unique, or set replace_all to true."),
            ambiguous);
        Assert.True((await workspace.InvokeAsync(tool, """{"path":"app.cs","old_string":"var c","new_string":"x"}""")).IsError);
        Assert.True((await workspace.InvokeAsync(tool, """{"path":"app.cs","old_string":"","new_string":"x"}""")).IsError);

        Assert.Equal(new ToolResult("Replaced 1 occurrence in app.cs."),
            await workspace.InvokeAsync(tool, """{"path":"app.cs","old_string":"var b = 1","new_string":"var b = 3"}"""));
        Assert.Equal(new ToolResult("Replaced 2 occurrences in app.cs."),
            await workspace.InvokeAsync(tool, """{"path":"app.cs","old_string":"var","new_string":"int","replace_all":true}"""));
        Assert.Equal("int a = 1;\nint b = 3;\n", workspace.Read("app.cs"));
    }

    [Fact]
    public async Task List_directory_marks_directories_and_sorts()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("b.txt", "");
        workspace.Write("src/a.cs", "");
        workspace.Write(".env", "");

        Assert.Equal(".env\nb.txt\nsrc/", (await workspace.InvokeAsync(new ListDirectoryTool(), "{}")).Content);
        Assert.Equal("a.cs", (await workspace.InvokeAsync(new ListDirectoryTool(), """{"path":"src"}""")).Content);
        Assert.True((await workspace.InvokeAsync(new ListDirectoryTool(), """{"path":"b.txt"}""")).IsError);
    }
}
