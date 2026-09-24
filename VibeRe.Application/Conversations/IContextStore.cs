using VibeRe.Domain.Conversations;

namespace VibeRe.Application.Conversations;

/// <summary>A node on a path together with the exact message bytes it stores.</summary>
public sealed record ContextEntry(ContextNode Node, WireMessage Message);

/// <summary>The branchable tree of messages sent to the model.</summary>
public interface IContextStore
{
    /// <summary>Creates the root (system prompt and tools) and the conversation's first branch.</summary>
    Task<ContextBranch> StartAsync(
        Guid conversationId,
        WireMessage preamble,
        string branchName = ContextBranch.DefaultName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends under the branch head and advances it. Fails if the head is no longer
    /// <paramref name="expectedHeadNodeId"/>, so concurrent writers can't silently interleave.
    /// </summary>
    Task<ContextNode> AppendAsync(
        Guid branchId,
        Guid expectedHeadNodeId,
        WireMessage message,
        Guid? turnId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a branch whose head is an existing node; nothing is copied.</summary>
    Task<ContextBranch> ForkAsync(Guid fromNodeId, string name, CancellationToken cancellationToken = default);

    Task<ContextBranch?> GetBranchAsync(Guid branchId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ContextBranch>> ListBranchesAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>Root-to-node messages, in send order.</summary>
    Task<IReadOnlyList<ContextEntry>> GetPathAsync(Guid nodeId, CancellationToken cancellationToken = default);
}
