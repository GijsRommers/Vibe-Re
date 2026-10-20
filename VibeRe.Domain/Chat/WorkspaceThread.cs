namespace VibeRe.Domain.Chat;

public sealed class WorkspaceThread
{
    public Guid Id { get; set; }
    public Guid? ParentThreadId { get; set; }
    public string Title { get; set; } = "";
    public string Status { get; set; } = "idle";
    public string RepositoryPath { get; set; } = "";
    public bool AutoWake { get; set; } = true;
    public string? WorktreePath { get; set; }
    public Guid? ActiveRunId { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<WorkspaceMessage> Messages { get; set; } = [];
}

public sealed class WorkspaceMessage
{
    public Guid Id { get; set; }
    public Guid ThreadId { get; set; }
    public int Sequence { get; set; }
    public string Role { get; set; } = "";
    public string Text { get; set; } = "";
    public bool Handled { get; set; }
    public Guid? SourceThreadId { get; set; }
}
