using System.Text.Json;
using VibeRe.Domain.Conversations;

namespace VibeRe.Application.Conversations;

/// <summary>Which stored <c>reasoning_content</c> is sent back to the model.</summary>
public enum ReasoningReplay
{
    /// <summary>
    /// DeepSeek thinking-mode convention: keep reasoning within the current turn's tool loop, drop it from
    /// earlier turns. Earlier turns lose their reasoning once, when the next user message arrives.
    /// </summary>
    CurrentTurn,

    /// <summary>Resend all reasoning; the prefix never changes, at the cost of more tokens.</summary>
    All,

    /// <summary>Never resend reasoning.</summary>
    None,
}

/// <summary>The request payload for one model call, made only of stored canonical bytes.</summary>
public sealed class ModelContext(IReadOnlyList<WireMessage> messages, byte[]? tools)
{
    public IReadOnlyList<WireMessage> Messages { get; } = messages;

    /// <summary>Canonical tool definitions, or null when the conversation has none.</summary>
    public ReadOnlyMemory<byte>? Tools { get; } = tools is null ? default(ReadOnlyMemory<byte>?) : tools;

    /// <summary>Writes the <c>messages</c> and <c>tools</c> properties into an open request object.</summary>
    public void WriteTo(Utf8JsonWriter writer)
    {
        writer.WritePropertyName("messages");
        writer.WriteStartArray();
        foreach (var message in Messages)
        {
            writer.WriteRawValue(message.Bytes.Span, skipInputValidation: true);
        }

        writer.WriteEndArray();
        if (Tools is { } tools)
        {
            writer.WritePropertyName("tools");
            writer.WriteRawValue(tools.Span, skipInputValidation: true);
        }
    }
}

public static class ContextBuilder
{
    public static ModelContext Build(IReadOnlyList<ContextEntry> path, ReasoningReplay reasoning = ReasoningReplay.CurrentTurn)
    {
        if (path.Count == 0 || path[0].Message.Role != WireMessage.SystemRole)
        {
            throw new ArgumentException("A context path starts at the system message.", nameof(path));
        }

        var preamble = path[0].Message;
        var tools = preamble.GetTools() is { } definitions ? CanonicalJson.Serialize(definitions) : null;
        var currentTurnStart = LastIndexOfUser(path);

        var messages = new List<WireMessage>(path.Count) { preamble.WithoutTools() };
        for (var index = 1; index < path.Count; index++)
        {
            var message = path[index].Message;
            var keepReasoning = reasoning switch
            {
                ReasoningReplay.All => true,
                ReasoningReplay.None => false,
                _ => index > currentTurnStart,
            };
            messages.Add(keepReasoning ? message : message.WithoutReasoning());
        }

        return new ModelContext(messages, tools);
    }

    private static int LastIndexOfUser(IReadOnlyList<ContextEntry> path)
    {
        for (var index = path.Count - 1; index >= 0; index--)
        {
            if (path[index].Message.Role == WireMessage.UserRole)
            {
                return index;
            }
        }

        return 0;
    }
}
