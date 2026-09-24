using Microsoft.EntityFrameworkCore;
using VibeRe.Application.Conversations;
using VibeRe.Domain.Conversations;

namespace VibeRe.Infrastructure.Persistence.Repositories;

public sealed class ContextStore(VibeReDbContext db) : IContextStore
{
    public async Task<ContextBranch> StartAsync(
        Guid conversationId,
        WireMessage preamble,
        string branchName = ContextBranch.DefaultName,
        CancellationToken cancellationToken = default)
    {
        var root = ContextNode.Root(conversationId, preamble);
        var branch = ContextBranch.Start(root, branchName);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        await EnsureBranchNameFreeAsync(conversationId, branch.Name, cancellationToken);
        await AddBlobAsync(preamble, cancellationToken);
        db.ContextNodes.Add(root);
        db.ContextBranches.Add(branch);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return branch;
    }

    public async Task<ContextNode> AppendAsync(
        Guid branchId,
        Guid expectedHeadNodeId,
        WireMessage message,
        Guid? turnId = null,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var branch = await GetBranchAsync(branchId, cancellationToken)
            ?? throw new KeyNotFoundException($"Branch {branchId} does not exist.");
        if (branch.HeadNodeId != expectedHeadNodeId)
        {
            throw new InvalidOperationException(
                $"Branch {branchId} is at node {branch.HeadNodeId}, not {expectedHeadNodeId}.");
        }

        var parent = await db.ContextNodes.AsNoTracking()
            .SingleAsync(node => node.Id == expectedHeadNodeId, cancellationToken);
        var node = ContextNode.Child(parent, message, turnId);
        await AddBlobAsync(message, cancellationToken);
        db.ContextNodes.Add(node);
        await db.SaveChangesAsync(cancellationToken);

        var moved = await db.ContextBranches
            .Where(existing => existing.Id == branchId && existing.HeadNodeId == expectedHeadNodeId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(existing => existing.HeadNodeId, node.Id), cancellationToken);
        if (moved != 1)
        {
            throw new InvalidOperationException($"Branch {branchId} moved while appending.");
        }

        await transaction.CommitAsync(cancellationToken);
        return node;
    }

    public async Task<ContextBranch> ForkAsync(Guid fromNodeId, string name, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var from = await db.ContextNodes.AsNoTracking().SingleOrDefaultAsync(node => node.Id == fromNodeId, cancellationToken)
            ?? throw new KeyNotFoundException($"Node {fromNodeId} does not exist.");
        var branch = ContextBranch.Fork(from, name);
        await EnsureBranchNameFreeAsync(from.ConversationId, branch.Name, cancellationToken);
        db.ContextBranches.Add(branch);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return branch;
    }

    public Task<ContextBranch?> GetBranchAsync(Guid branchId, CancellationToken cancellationToken = default) =>
        db.ContextBranches.AsNoTracking().SingleOrDefaultAsync(branch => branch.Id == branchId, cancellationToken);

    public async Task<IReadOnlyList<ContextBranch>> ListBranchesAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default) =>
        await db.ContextBranches
            .AsNoTracking()
            .Where(branch => branch.ConversationId == conversationId)
            .OrderBy(branch => branch.Name)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ContextEntry>> GetPathAsync(Guid nodeId, CancellationToken cancellationToken = default)
    {
        var nodes = await db.ContextNodes
            .FromSql($"""
                WITH RECURSIVE path(Id) AS (
                    SELECT Id FROM context_nodes WHERE Id = {nodeId}
                    UNION ALL
                    SELECT node.ParentId FROM context_nodes node JOIN path ON node.Id = path.Id
                    WHERE node.ParentId IS NOT NULL
                )
                SELECT context_nodes.* FROM context_nodes JOIN path ON context_nodes.Id = path.Id
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var hashes = nodes.Select(node => node.PayloadHash).Distinct().ToList();
        var blobs = await db.ContextBlobs
            .AsNoTracking()
            .Where(blob => hashes.Contains(blob.Hash))
            .ToDictionaryAsync(blob => blob.Hash, blob => blob.Bytes, cancellationToken);

        return nodes
            .OrderBy(node => node.Depth)
            .Select(node => new ContextEntry(node, WireMessage.FromCanonical(blobs[node.PayloadHash])))
            .ToList();
    }

    private async Task AddBlobAsync(WireMessage message, CancellationToken cancellationToken)
    {
        if (!await db.ContextBlobs.AnyAsync(blob => blob.Hash == message.Hash, cancellationToken))
        {
            db.ContextBlobs.Add(new ContextBlob(message));
        }
    }

    private async Task EnsureBranchNameFreeAsync(Guid conversationId, string name, CancellationToken cancellationToken)
    {
        if (await db.ContextBranches.AnyAsync(
                branch => branch.ConversationId == conversationId && branch.Name == name,
                cancellationToken))
        {
            throw new InvalidOperationException($"Conversation {conversationId} already has a branch named '{name}'.");
        }
    }
}
