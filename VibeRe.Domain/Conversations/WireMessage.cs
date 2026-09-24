using System.Text;
using System.Text.Json.Nodes;

namespace VibeRe.Domain.Conversations;

/// <summary>A function call requested by the model. <see cref="Arguments"/> is kept verbatim as the model wrote it.</summary>
public sealed record WireToolCall(string Id, string Name, string Arguments);

/// <summary>
/// One chat message in the OpenAI/DeepSeek wire shape, frozen to canonical bytes at construction.
/// Requests are rebuilt from these bytes, never re-rendered, so the prompt prefix stays stable for caching.
/// </summary>
public sealed class WireMessage
{
    public const string SystemRole = "system";
    public const string UserRole = "user";
    public const string AssistantRole = "assistant";
    public const string ToolRole = "tool";

    private const string ToolsKey = "tools";
    private const string ReasoningKey = "reasoning_content";

    private readonly byte[] _bytes;

    private WireMessage(JsonObject json)
    {
        Role = json["role"]?.GetValue<string>() is { Length: > 0 } role
            ? role
            : throw new ArgumentException("A message requires a role.");
        if (Role is not (SystemRole or UserRole or AssistantRole or ToolRole))
        {
            throw new ArgumentException($"Unknown message role '{Role}'.");
        }

        if (json.ContainsKey(ToolsKey) && Role != SystemRole)
        {
            throw new ArgumentException("Only the system message can carry tool definitions.");
        }

        HasReasoning = json.ContainsKey(ReasoningKey);
        HasTools = json.ContainsKey(ToolsKey);
        _bytes = CanonicalJson.Serialize(json);
        Hash = CanonicalJson.Hash(_bytes);
    }

    public string Role { get; }

    /// <summary>Canonical UTF-8 JSON, exactly as stored and sent.</summary>
    public ReadOnlyMemory<byte> Bytes => _bytes;

    /// <summary>Lowercase hex SHA-256 of <see cref="Bytes"/>.</summary>
    public string Hash { get; }

    public bool HasReasoning { get; }

    public bool HasTools { get; }

    /// <summary>The conversation preamble: system prompt plus tool definitions, which are part of the cached prefix.</summary>
    public static WireMessage System(string content, JsonArray? tools = null)
    {
        var json = new JsonObject { ["role"] = SystemRole, ["content"] = content };
        if (tools is { Count: > 0 })
        {
            json[ToolsKey] = tools.DeepClone();
        }

        return new WireMessage(json);
    }

    public static WireMessage User(string content) =>
        new(new JsonObject { ["role"] = UserRole, ["content"] = content });

    public static WireMessage Assistant(
        string? content,
        string? reasoningContent = null,
        IReadOnlyList<WireToolCall>? toolCalls = null)
    {
        var json = new JsonObject { ["role"] = AssistantRole, ["content"] = content };
        if (reasoningContent is not null)
        {
            json[ReasoningKey] = reasoningContent;
        }

        if (toolCalls is { Count: > 0 })
        {
            json["tool_calls"] = new JsonArray(toolCalls.Select(call => (JsonNode)new JsonObject
            {
                ["id"] = call.Id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments },
            }).ToArray());
        }

        return new WireMessage(json);
    }

    public static WireMessage Tool(string toolCallId, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolCallId);
        return new(new JsonObject { ["role"] = ToolRole, ["tool_call_id"] = toolCallId, ["content"] = content });
    }

    /// <summary>Rehydrates stored bytes, rejecting anything that isn't already canonical.</summary>
    public static WireMessage FromCanonical(ReadOnlySpan<byte> bytes)
    {
        var json = JsonNode.Parse(bytes) as JsonObject
            ?? throw new ArgumentException("A message must be a JSON object.");
        var message = new WireMessage(json);
        if (!bytes.SequenceEqual(message._bytes))
        {
            throw new ArgumentException("Stored message bytes are not canonical.");
        }

        return message;
    }

    public JsonObject ToJson() => (JsonObject)JsonNode.Parse(_bytes)!;

    public JsonArray? GetTools() => ToJson()[ToolsKey]?.DeepClone().AsArray();

    public WireMessage WithoutReasoning() => Without(ReasoningKey);

    public WireMessage WithoutTools() => Without(ToolsKey);

    private WireMessage Without(string key)
    {
        var json = ToJson();
        return json.Remove(key) ? new WireMessage(json) : this;
    }

    public override string ToString() => Encoding.UTF8.GetString(_bytes);
}
