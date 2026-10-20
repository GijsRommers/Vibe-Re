using VibeRe.Domain.Chat;

namespace VibeRe.Server.Application.Workspace;

public static class Cockpit
{
    public static readonly Guid Id = Guid.Parse("00000000-0000-0000-0000-000000000001");
}

public interface IWorkspaceStore
{
    Task<IReadOnlyList<WorkspaceThread>> ListAsync(CancellationToken ct);
    Task<WorkspaceThread?> FindAsync(Guid id, CancellationToken ct);
    Task SaveAsync(WorkspaceThread thread, CancellationToken ct);
    Task<bool> HasMessageAsync(Guid id, CancellationToken ct);
    Task CompleteAsync(WorkspaceThread thread, WorkspaceMessage? report, CancellationToken ct);
}

public sealed record ModelMessage(string Role, string Text, IReadOnlyList<ModelToolCall>? Tools = null, string? ToolCallId = null);
public sealed record ModelToolCall(string Id, string Name, string Arguments);
public sealed record ModelReply(string Text, IReadOnlyList<ModelToolCall> Tools);
public sealed record ToolParameter(string Type, string Description);
public sealed record AgentTool(string Name, string Description, Dictionary<string, ToolParameter> Properties, string[] Required);
public interface IAgentModel
{
    Task<ModelReply> CompleteAsync(IReadOnlyList<ModelMessage> messages, IReadOnlyList<AgentTool> tools, CancellationToken ct);
}
public interface IWorkspaceTools
{
    Task PrepareAsync(string root, string worktree, CancellationToken ct);
    Task<string> ExecuteAsync(string worktree, string name, string arguments, CancellationToken ct);
}
public interface ICodingAgent
{
    Task<string> RunAsync(string root, string worktree, string prompt, Func<string, Task> progress, CancellationToken ct);
}
public sealed record WorkspaceOptions(string Root);
public sealed record WorkspaceState(string Root, IReadOnlyList<WorkspaceThread> Threads);
public sealed class WorkspaceException(string message) : Exception(message);
