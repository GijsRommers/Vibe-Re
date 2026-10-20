namespace VibeRe.Contracts;

public sealed record WorkspaceReply(string Root, ThreadReply[] Threads);
public sealed record ThreadReply(Guid Id, Guid? ParentThreadId, string Title, string Status, string? WorktreePath,
    ThreadMessageReply[] Messages);
public sealed record ThreadMessageReply(Guid Id, string Role, string Text, Guid? SourceThreadId);
