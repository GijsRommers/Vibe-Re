namespace VibeRe.Client.Application.Workspace;

public static class CockpitThread
{
    public static readonly Guid Id = Guid.Parse("00000000-0000-0000-0000-000000000001");
}
public sealed record WorkspaceSnapshot(string Root, IReadOnlyList<ThreadState> Threads);
public sealed record ThreadState(Guid Id, Guid? ParentThreadId, string Title, string Status, string? WorktreePath,
    IReadOnlyList<ThreadMessage> Messages)
{
    public bool IsRunning => Status == "running";
    public string StatusLabel => Status switch { "running" => "Working", "completed" => "Done", "interrupted" => "Stopped", "failed" => "Needs attention", _ => "Ready" };
}
public sealed record ThreadMessage(Guid Id, string Role, string Text, Guid? SourceThreadId)
{
    public string Author => Role switch { "user" => "You", "report" => "Thread report", "activity" => "Activity", "error" => "Needs attention", _ => "VibeRe" };
    public bool IsUser => Role == "user";
    public bool IsReport => SourceThreadId is not null;
    public bool IsReply => !IsUser && !IsActivity;
    public bool IsActivity => Role == "activity";
}
public interface IWorkspaceClient
{
    Task<WorkspaceSnapshot> GetWorkspaceAsync(CancellationToken ct = default);
    Task<ThreadState> ReadThreadAsync(Guid id, CancellationToken ct = default);
    Task<ThreadState> CreateThreadAsync(Guid id, string title, string? prompt, CancellationToken ct = default);
    Task<ThreadState> SendThreadAsync(Guid id, Guid commandId, string text, CancellationToken ct = default);
    Task StopThreadAsync(Guid id, CancellationToken ct = default);
}
