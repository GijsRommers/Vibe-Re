using System.Text.Json;

namespace VibeRe.Domain.Conversations;

/// <summary>
/// One immutable entry in a conversation's ordered trace log (streaming, steps, usage, errors). What the model
/// sees lives in the <see cref="ContextNode"/> tree; <see cref="NodeId"/> ties an event to the message it produced.
/// </summary>
public sealed class ConversationEvent
{
    private ConversationEvent() { }

    public ConversationEvent(
        Guid conversationId,
        long sequence,
        string kind,
        string dataJson,
        Guid? turnId = null,
        Guid? stepId = null,
        Guid? nodeId = null)
    {
        if (conversationId == Guid.Empty)
        {
            throw new ArgumentException("A conversation ID is required.", nameof(conversationId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataJson);
        using var _ = JsonDocument.Parse(dataJson);
        if (turnId == Guid.Empty || stepId == Guid.Empty || nodeId == Guid.Empty || (stepId is not null && turnId is null))
        {
            throw new ArgumentException("A step must belong to a turn, and IDs cannot be empty.");
        }

        ConversationId = conversationId;
        Sequence = sequence;
        Kind = kind;
        DataJson = dataJson;
        TurnId = turnId;
        StepId = stepId;
        NodeId = nodeId;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid ConversationId { get; private set; }

    public long Sequence { get; private set; }

    public string Kind { get; private set; } = null!;

    public string DataJson { get; private set; } = null!;

    public Guid? TurnId { get; private set; }

    public Guid? StepId { get; private set; }

    public Guid? NodeId { get; private set; }
}
