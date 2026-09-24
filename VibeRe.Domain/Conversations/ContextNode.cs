namespace VibeRe.Domain.Conversations;

/// <summary>
/// One message in a conversation's context tree. The path from the root to a branch head is exactly
/// the message list sent to the model; forking is pointing a new branch at an existing node.
/// </summary>
public sealed class ContextNode
{
    private ContextNode() { }

    private ContextNode(Guid conversationId, ContextNode? parent, WireMessage message, Guid? turnId)
    {
        ConversationId = conversationId;
        ParentId = parent?.Id;
        Depth = parent is null ? 0 : parent.Depth + 1;
        Role = message.Role;
        TurnId = turnId;
        PayloadHash = message.Hash;
        PrefixHash = ChainHash(parent?.PrefixHash, message.Hash);
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid ConversationId { get; private set; }

    public Guid? ParentId { get; private set; }

    /// <summary>Position in the message list; the root is 0.</summary>
    public int Depth { get; private set; }

    public string Role { get; private set; } = null!;

    /// <summary>The turn this message belongs to: started by a user message, inherited by what follows it.</summary>
    public Guid? TurnId { get; private set; }

    public string PayloadHash { get; private set; } = null!;

    /// <summary>
    /// Hash of every payload from the root to here. Equal prefix hashes mean byte-identical histories,
    /// so nodes on different branches with the same value share the provider's prompt cache.
    /// </summary>
    public string PrefixHash { get; private set; } = null!;

    public static ContextNode Root(Guid conversationId, WireMessage preamble)
    {
        if (conversationId == Guid.Empty)
        {
            throw new ArgumentException("A conversation ID is required.", nameof(conversationId));
        }

        if (preamble.Role != WireMessage.SystemRole)
        {
            throw new ArgumentException("A context tree starts with the system message.", nameof(preamble));
        }

        return new ContextNode(conversationId, null, preamble, null);
    }

    /// <param name="turnId">Only for user messages, to reuse an ID already given to trace events; generated otherwise.</param>
    public static ContextNode Child(ContextNode parent, WireMessage message, Guid? turnId = null)
    {
        if (message.Role == WireMessage.SystemRole)
        {
            throw new ArgumentException("Only the root can be a system message.", nameof(message));
        }

        if (turnId == Guid.Empty || (turnId is not null && message.Role != WireMessage.UserRole))
        {
            throw new ArgumentException("Only user messages start a turn, and turn IDs cannot be empty.", nameof(turnId));
        }

        if (message.Role != WireMessage.UserRole && parent.TurnId is null)
        {
            throw new ArgumentException("The first message after the system message must be a user message.", nameof(message));
        }

        var turn = message.Role == WireMessage.UserRole ? turnId ?? Guid.NewGuid() : parent.TurnId;
        return new ContextNode(parent.ConversationId, parent, message, turn);
    }

    private static string ChainHash(string? parentPrefixHash, string payloadHash) =>
        parentPrefixHash is null
            ? payloadHash
            : CanonicalJson.Hash([.. Convert.FromHexString(parentPrefixHash), .. Convert.FromHexString(payloadHash)]);
}
