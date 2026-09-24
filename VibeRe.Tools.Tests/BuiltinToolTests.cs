using System.Text.Json;
using VibeRe.Tools.Docs;
using VibeRe.Tools.Files;
using VibeRe.Tools.Search;

namespace VibeRe.Tools.Tests;

public class BuiltinToolTests
{
    [Fact]
    public void Schemas_are_generated_from_the_argument_records()
    {
        var schema = new GrepTool(new Ripgrep()).InputSchema;

        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal(["pattern"], schema.GetProperty("required").EnumerateArray().Select(name => name.GetString()));
        var properties = schema.GetProperty("properties");
        Assert.StartsWith("Regular expression", properties.GetProperty("pattern").GetProperty("description").GetString());
        Assert.Equal(["files_with_matches", "content", "count"],
            properties.GetProperty("output_mode").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        Assert.True(properties.TryGetProperty("ignore_case", out _));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"path":"a.txt","limit":"ten"}""")]
    [InlineData("""{"path":"a.txt","unknown":1}""")]
    public async Task Invalid_arguments_are_reported_to_the_model(string arguments)
    {
        using var workspace = new TempWorkspace();

        var result = await workspace.InvokeAsync(new ReadFileTool(), arguments);

        Assert.True(result.IsError);
        Assert.StartsWith("Invalid arguments for 'read_file'", result.Content);
    }

    [Fact]
    public async Task Built_in_tools_have_unique_names()
    {
        var tools = await new BuiltinToolSource(new Ripgrep(), DocsLibrary.LoadEmbedded()).GetToolsAsync();

        Assert.Equal(
            ["create_file", "edit_file", "find_files", "grep", "list_directory", "read_doc", "read_file", "search_docs"],
            tools.Select(tool => tool.Name).Order());
        Assert.All(tools, tool => Assert.Equal(JsonValueKind.Object, tool.InputSchema.ValueKind));
    }
}
