using Microsoft.AspNetCore.SignalR;
using VibeRe.Contracts;
using VibeRe.Domain.Chat;
using VibeRe.Server.Application.Workspace;

namespace VibeRe.Server.Services;

public sealed partial class ProviderAuthHub
{
    public Task<WorkspaceReply> GetWorkspace() => WorkspaceRequest(async () =>
    {
        var state = await workspace.SnapshotAsync(Context.ConnectionAborted);
        return new WorkspaceReply(state.Root, state.Threads.Select(ToReply).ToArray());
    });
    public Task<ThreadReply> ReadThread(Guid id) => WorkspaceRequest(async () =>
        ToReply(await workspace.ReadAsync(id, Context.ConnectionAborted)));
    public Task<ThreadReply> CreateThread(Guid id, string title, string? prompt) => WorkspaceRequest(async () =>
        ToReply(await workspace.CreateAsync(id, title, prompt, Context.ConnectionAborted)));
    public Task<ThreadReply> SendThread(Guid id, Guid commandId, string text) => WorkspaceRequest(async () =>
        ToReply(await workspace.SendAsync(id, commandId, text, Context.ConnectionAborted)));
    public Task<bool> StopThread(Guid id) => WorkspaceRequest(async () =>
    {
        await workspace.StopAsync(id, Context.ConnectionAborted);
        return true;
    });

    private async Task<T> WorkspaceRequest<T>(Func<Task<T>> action)
    {
        // Existing paired devices only grant provider-auth access. Do not silently grant code execution.
        var client = await access.EnsureAuthorizedAsync(Context);
        if (Context.GetHttpContext() is not { } http || !ClientAccessPolicy.CanManage(http, client))
            throw new HubException("Coding workspaces currently require a direct local administrator connection.");
        try { return await action(); }
        catch (WorkspaceException error) { throw new HubException(error.Message); }
        catch (OperationCanceledException) { throw new HubException("Workspace request canceled."); }
        catch (Exception error)
        {
            logger.LogError("Workspace request failed ({ExceptionType}).", error.GetType().Name);
            throw new HubException("Could not access this workspace. Check the local server and try again.");
        }
    }
    private static ThreadReply ToReply(WorkspaceThread thread) => new(thread.Id, thread.ParentThreadId,
        thread.Title, thread.Status, thread.WorktreePath, thread.Messages.Select(message =>
            new ThreadMessageReply(message.Id, message.Role, message.Text, message.SourceThreadId)).ToArray());
}
