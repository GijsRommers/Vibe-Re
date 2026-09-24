namespace VibeRe.Domain.Conversations;

/// <summary>Content-addressed canonical message bytes, shared by every node (and conversation) that sends them.</summary>
public sealed class ContextBlob
{
    private ContextBlob() { }

    public ContextBlob(WireMessage message)
    {
        Hash = message.Hash;
        Bytes = message.Bytes.ToArray();
    }

    public string Hash { get; private set; } = null!;

    public byte[] Bytes { get; private set; } = null!;
}
