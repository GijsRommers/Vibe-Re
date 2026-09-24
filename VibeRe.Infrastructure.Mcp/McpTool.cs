using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using VibeRe.Application.Tools;

namespace VibeRe.Infrastructure.Mcp;

/// <summary>Adapts one tool of an MCP server to <see cref="ITool"/>, namespaced by the server's name.</summary>
internal sealed class McpTool(string serverName, McpClientTool tool) : ITool
{
    public string Name { get; } = QualifiedName(serverName, tool.Name);

    public string? Description => tool.Description;

    public JsonElement InputSchema => tool.JsonSchema;

    public async Task<ToolResult> InvokeAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        var values = arguments.ValueKind == JsonValueKind.Object
            ? arguments.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value)
            : [];
        var result = await tool.CallAsync(values, cancellationToken: cancellationToken);

        // Only text is forwarded for now; images and resources need model-specific content blocks.
        var content = string.Join("\n", result.Content.Select(block => block switch
        {
            TextContentBlock text => text.Text,
            _ => $"[{block.Type} content omitted]",
        }));
        return new ToolResult(content, result.IsError == true);
    }

    /// <summary>
    /// <c>mcp__{server}__{tool}</c>, the convention Claude Code and others use, with characters that models
    /// reject replaced by <c>_</c> and cut to the 64 characters that model APIs allow.
    /// </summary>
    internal static string QualifiedName(string serverName, string toolName)
    {
        var name = new StringBuilder($"mcp__{serverName}__");
        foreach (var character in toolName)
        {
            name.Append(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' ? character : '_');
        }

        return name.Length > 64 ? name.ToString(0, 64) : name.ToString();
    }
}
