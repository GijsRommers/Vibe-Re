using ThreadState = VibeRe.Client.Application.Workspace.ThreadState;
using VibeRe.Client.Application.Workspace;
using VibeRe.Contracts;

namespace VibeRe.Client.SignalR;

public sealed partial class SignalRAuthClient
{
    public async Task<WorkspaceSnapshot> GetWorkspaceAsync(CancellationToken ct = default)
    {
        var reply = await InvokeAsync<WorkspaceReply>("GetWorkspace", [], ct);
        return new(reply.Root, reply.Threads.Select(MapThread).ToArray());
    }
    public async Task<ThreadState> ReadThreadAsync(Guid id, CancellationToken ct = default) =>
        MapThread(await InvokeAsync<ThreadReply>("ReadThread", [id], ct));
    public async Task<ThreadState> CreateThreadAsync(Guid id, string title, string? prompt, CancellationToken ct = default) =>
        MapThread(await InvokeAsync<ThreadReply>("CreateThread", [id, title, prompt], ct));
    public async Task<ThreadState> SendThreadAsync(Guid id, Guid commandId, string text, CancellationToken ct = default) =>
        MapThread(await InvokeAsync<ThreadReply>("SendThread", [id, commandId, text], ct));
    public async Task StopThreadAsync(Guid id, CancellationToken ct = default) =>
        _ = await InvokeAsync<bool>("StopThread", [id], ct);

    private static ThreadState MapThread(ThreadReply reply) => new(reply.Id, reply.ParentThreadId, reply.Title,
        reply.Status, reply.WorktreePath, reply.Messages.Select(x => new ThreadMessage(x.Id, x.Role, x.Text, x.SourceThreadId)).ToArray());
}
