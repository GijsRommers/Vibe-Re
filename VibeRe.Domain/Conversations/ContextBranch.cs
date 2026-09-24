namespace VibeRe.Domain.Conversations;

/// <summary>A named, movable pointer to a node in a conversation's context tree.</summary>
public sealed class ContextBranch
{
    private ContextBranch() { }

    private ContextBranch(Guid conversationId, string name, Guid headNodeId, Guid? forkedFromNodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ConversationId = conversationId;
        Name = name.Trim();
        HeadNodeId = headNodeId;
        ForkedFromNodeId = forkedFromNodeId;
    }

    public const string DefaultName = "main";

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid ConversationId { get; private set; }

    public string Name { get; private set; } = null!;

    public Guid HeadNodeId { get; private set; }

    public Guid? ForkedFromNodeId { get; private set; }

    public static ContextBranch Start(ContextNode root, string name = DefaultName) =>
        new(root.ConversationId, name, root.Id, null);

    public static ContextBranch Fork(ContextNode from, string name) =>
        new(from.ConversationId, name, from.Id, from.Id);
}
