using System.Text.Json;

namespace VibeRe.Application.Tools;

/// <summary>Something a model can call: a built-in capability or a tool exposed by an MCP server.</summary>
public interface ITool
{
    /// <summary>Unique registry name, sent to the model. Must match <c>^[a-zA-Z0-9_-]{1,64}$</c>.</summary>
    string Name { get; }

    string? Description { get; }

    /// <summary>JSON Schema (an <c>object</c> schema) describing the arguments.</summary>
    JsonElement InputSchema { get; }

    Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken = default);
}

/// <summary>Where a tool call happens. Built-in tools resolve relative paths against the working directory.</summary>
public sealed record ToolContext(Guid ConversationId, string WorkingDirectory);

/// <summary>What goes back to the model. Errors are returned, not thrown, so the model can recover.</summary>
public sealed record ToolResult(string Content, bool IsError = false)
{
    public static ToolResult Error(string message) => new(message, IsError: true);
}

/// <summary>A group of tools that is discovered together: the built-ins, or everything from MCP servers.</summary>
public interface IToolSource
{
    Task<IReadOnlyList<ITool>> GetToolsAsync(CancellationToken cancellationToken = default);
}

/// <summary>The front end that confirms <see cref="Domain.Tools.ToolApproval.Ask"/> tool calls with the user.</summary>
public interface IToolApprover
{
    Task<bool> ApproveAsync(ITool tool, JsonElement arguments, CancellationToken cancellationToken);
}
