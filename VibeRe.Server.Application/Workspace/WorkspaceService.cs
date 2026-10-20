using System.Collections.Concurrent;
using System.Text.Json;
using VibeRe.Domain.Chat;

namespace VibeRe.Server.Application.Workspace;

// App-owned identities, runs and lineage stay independent of the provider runtime.
// The gate protects command acceptance and commits, never long-running provider work.
public sealed class WorkspaceService(IWorkspaceStore store, IAgentModel model, ICodingAgent agent,
    WorkspaceOptions options) : IAsyncDisposable, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _runs = new();
    private readonly ConcurrentDictionary<Guid, Task> _tasks = new();
    private readonly CancellationTokenSource _shutdown = new();
    private bool _initialized;
    private int _disposed;

    public async Task<WorkspaceState> SnapshotAsync(CancellationToken ct = default) => await Locked(async () =>
        new WorkspaceState(options.Root, await store.ListAsync(ct)), ct);

    public Task<WorkspaceThread> ReadAsync(Guid id, CancellationToken ct = default) =>
        Locked(() => Find(id, ct), ct);

    public Task<WorkspaceThread> CreateAsync(Guid id, string title, string? prompt, CancellationToken ct = default) =>
        Locked(async () =>
        {
            if (id == Guid.Empty || id == Cockpit.Id) throw new WorkspaceException("Choose a new thread ID.");
            if (await store.FindAsync(id, ct) is { } existing) return existing;
            title = title.Trim();
            if (title.Length is 0 or > 160) throw new WorkspaceException("Give the thread a title of up to 160 characters.");
            var thread = new WorkspaceThread { Id = id, Title = title, ParentThreadId = Cockpit.Id, RepositoryPath = options.Root, UpdatedAt = DateTime.UtcNow };
            await store.SaveAsync(thread, ct);
            if (!string.IsNullOrWhiteSpace(prompt)) await AcceptLocked(thread, Guid.NewGuid(), prompt, ct);
            return thread;
        }, ct);

    public Task<WorkspaceThread> SendAsync(Guid id, Guid commandId, string text, CancellationToken ct = default) =>
        Locked(async () =>
        {
            var thread = await Find(id, ct);
            if (await store.HasMessageAsync(commandId, ct)) return thread;
            await AcceptLocked(thread, commandId, text, ct);
            return thread;
        }, ct);

    public async Task StopAsync(Guid id, CancellationToken ct = default)
    {
        await Locked(async () =>
        {
            var thread = await Find(id, ct);
            if (id == Cockpit.Id)
            {
                thread.AutoWake = false;
                await store.SaveAsync(thread, ct);
                foreach (var running in _runs.Values) running.Cancel();
            }
            else if (_runs.TryGetValue(id, out var run)) run.Cancel();
            return true;
        }, ct);
    }

    private async Task AcceptLocked(WorkspaceThread thread, Guid commandId, string text, CancellationToken ct)
    {
        text = text.Trim();
        if (commandId == Guid.Empty || text.Length is 0 or > 16000)
            throw new WorkspaceException("Write a message of up to 16,000 characters.");
        if (thread.Status == "running") throw new WorkspaceException("This thread is working. Stop it before sending another message.");
        if (_runs.Count >= 5) throw new WorkspaceException("Five threads are already working. Wait for one to finish.");
        if (thread.Id != Cockpit.Id && thread.Messages.Count == 0 && thread.Title == "New thread") thread.Title = text[..Math.Min(text.Length, 60)];
        thread.Messages.Add(new() { Id = commandId, ThreadId = thread.Id, Sequence = thread.Messages.Count + 1, Role = "user", Text = text });
        thread.AutoWake = true;
        thread.ActiveRunId = Guid.NewGuid();
        thread.Status = "running";
        thread.UpdatedAt = DateTime.UtcNow;
        await store.SaveAsync(thread, ct);
        LaunchLocked(thread);
    }

    private void LaunchLocked(WorkspaceThread thread)
    {
        var runId = thread.ActiveRunId!.Value;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _runs[thread.Id] = cancellation;
        _tasks[runId] = Task.Run(async () =>
        {
            // Acceptance commits before the provider is launched; losing a UI connection cannot replay it.
            try { await RunAsync(thread.Id, runId, cancellation.Token); }
            finally
            {
                await _gate.WaitAsync();
                try
                {
                    _runs.TryRemove(new KeyValuePair<Guid, CancellationTokenSource>(thread.Id, cancellation));
                    _tasks.TryRemove(runId, out _);
                    cancellation.Dispose();
                }
                finally { _gate.Release(); }
            }
        });
    }

    private async Task RunAsync(Guid id, Guid runId, CancellationToken ct)
    {
        string result;
        var status = "completed";
        var seenReportSequence = 0;
        try
        {
            var thread = await ReadAsync(id, ct);
            if (id == Cockpit.Id)
            {
                seenReportSequence = thread.Messages.Where(x => x.Role == "report").Select(x => x.Sequence).DefaultIfEmpty().Max();
                result = await RunCockpit(thread, ct);
            }
            else
            {
                var repository = string.IsNullOrEmpty(thread.RepositoryPath) ? options.Root : thread.RepositoryPath;
                var worktree = thread.WorktreePath ?? Path.Combine(Path.GetDirectoryName(repository)!,
                    ".vibere-worktrees", new DirectoryInfo(repository).Name, thread.Id.ToString("N"));
                await Locked(async () =>
                {
                    var current = await Find(id, ct);
                    current.WorktreePath = worktree;
                    await store.SaveAsync(current, ct);
                    return true;
                }, ct);
                var prompt = "You are a coding agent working on the following task in your own git worktree. " +
                    "Preserve existing work. Do not commit, push, deploy or contact other people unless explicitly requested. " +
                    "Finish with a clear report of changes, validation and remaining issues.\n\n" +
                    string.Join("\n\n", thread.Messages.Where(x => x.Role is "user" or "assistant").TakeLast(30)
                        .Select(x => $"{x.Role}: {x.Text}"));
                var updates = 0;
                result = await agent.RunAsync(repository, worktree, prompt, async text =>
                {
                    if (++updates <= 100) await AppendAsync(id, "activity", text, ct);
                }, ct);
            }
        }
        catch (OperationCanceledException) { status = "interrupted"; result = "Stopped. Changes already made in the thread's worktree are preserved."; }
        catch (WorkspaceException error) { status = "failed"; result = error.Message; }
        catch { status = "failed"; result = "The agent could not finish this run. Check the provider connection and try again."; }
        // Completion and parent delivery are one transaction, including failures and interruption.
        await Locked(async () =>
        {
            var thread = await Find(id, CancellationToken.None);
            if (thread.ActiveRunId != runId) return false;
            thread.Status = status;
            thread.ActiveRunId = null;
            thread.UpdatedAt = DateTime.UtcNow;
            thread.Messages.Add(Message(thread, status == "completed" ? "assistant" : "error", result));
            var report = id == Cockpit.Id ? null : new WorkspaceMessage
            {
                Id = runId, ThreadId = thread.ParentThreadId ?? Cockpit.Id, Role = "report", SourceThreadId = id,
                Text = $"{thread.Title} · {status}\n\n{result}"
            };
            if (id == Cockpit.Id)
                foreach (var message in thread.Messages.Where(x => x.Role == "report" && x.Sequence <= seenReportSequence)) message.Handled = true;
            await store.CompleteAsync(thread, report, CancellationToken.None);
            await WakeCockpitLocked(CancellationToken.None);
            return true;
        }, CancellationToken.None);
    }

    private async Task WakeCockpitLocked(CancellationToken ct)
    {
        if (_shutdown.IsCancellationRequested) return;
        var cockpit = await Find(Cockpit.Id, ct);
        if (!cockpit.AutoWake || cockpit.Status == "running" || !cockpit.Messages.Any(x => x.Role == "report" && !x.Handled)) return;
        cockpit.Status = "running";
        cockpit.ActiveRunId = Guid.NewGuid();
        cockpit.UpdatedAt = DateTime.UtcNow;
        await store.SaveAsync(cockpit, ct);
        LaunchLocked(cockpit);
    }

    private async Task<string> RunCockpit(WorkspaceThread thread, CancellationToken ct)
    {
        var messages = new List<ModelMessage>
        {
            new("system", "You are The Cockpit, the permanent orchestration chat for this workspace. " +
                "Use delegate_task to launch coding threads, list_threads and read_thread to inspect them, " +
                "send_to_thread for follow-up work, and stop_thread to cancel. Children run independently in isolated " +
                "git worktrees and automatically report completion, failure or interruption here. " +
                "When a child reports back, assess its result against the user goal and continue coordinating unfinished work. " +
                "Do not claim work is done until you have its report. You do not have direct file or shell tools. " +
                "Task prompts must be self-contained; children receive only the task and their own conversation. " +
                "Keep replies concise. Repository: " + options.Root)
        };
        messages.AddRange(thread.Messages.Where(x => x.Role != "activity").TakeLast(40)
            .Select(x => new ModelMessage(x.Role is "assistant" ? "assistant" : "user", x.Text)));
        for (var round = 0; round < 8; round++)
        {
            ct.ThrowIfCancellationRequested();
            var reply = await model.CompleteAsync(messages, CockpitTools, ct);
            if (reply.Tools.Count == 0) return string.IsNullOrWhiteSpace(reply.Text) ? "Ready for your next task." : reply.Text;
            messages.Add(new("assistant", reply.Text, reply.Tools));
            foreach (var tool in reply.Tools.Take(8))
            {
                string output;
                try { output = await ExecuteTool(tool, ct); }
                catch (Exception error) when (error is WorkspaceException or JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
                { output = JsonSerializer.Serialize(new { error = error is WorkspaceException ? error.Message : "Invalid tool arguments." }); }
                messages.Add(new("tool", output, ToolCallId: tool.Id));
            }
        }
        return "The requested threads have been dispatched. Their reports will appear here as they finish.";
    }

    private static ToolParameter Text(string description) => new("string", description);
    private static readonly AgentTool[] CockpitTools =
    [
        new("delegate_task", "Launch a self-contained coding task in its own git worktree. Reports back automatically.",
            new() { ["title"] = Text("Short thread title"), ["task"] = Text("Complete task, requirements and validation") }, ["title", "task"]),
        new("list_threads", "List threads and run status.", new(), []),
        new("read_thread", "Read the recent messages in a thread.", new() { ["thread_id"] = Text("Thread UUID") }, ["thread_id"]),
        new("send_to_thread", "Start follow-up work in an idle thread.", new() { ["thread_id"] = Text("Thread UUID"), ["message"] = Text("Follow-up task") }, ["thread_id", "message"]),
        new("stop_thread", "Stop an active coding thread.", new() { ["thread_id"] = Text("Thread UUID") }, ["thread_id"])
    ];

    private async Task<string> ExecuteTool(ModelToolCall tool, CancellationToken ct)
    {
        using var args = JsonDocument.Parse(tool.Arguments);
        var input = args.RootElement;
        switch (tool.Name)
        {
            case "delegate_task":
                var child = await CreateAsync(Guid.NewGuid(), input.GetProperty("title").GetString()!, input.GetProperty("task").GetString(), ct);
                await AppendAsync(Cockpit.Id, "activity", "Started thread: " + child.Title, ct);
                return JsonSerializer.Serialize(new { threadId = child.Id, title = child.Title, status = child.Status });
            case "list_threads":
                var state = await SnapshotAsync(ct);
                return JsonSerializer.Serialize(state.Threads.Select(x => new { id = x.Id, title = x.Title, status = x.Status, parentThreadId = x.ParentThreadId }));
            case "read_thread":
                var read = await ReadAsync(input.GetProperty("thread_id").GetGuid(), ct);
                return JsonSerializer.Serialize(new { read.Id, read.Title, read.Status, messages = read.Messages.TakeLast(30).Select(x => new { x.Role, x.Text }) });
            case "send_to_thread":
                var target = input.GetProperty("thread_id").GetGuid();
                if (target == Cockpit.Id) throw new WorkspaceException("The Cockpit cannot send a message to itself.");
                var updated = await SendAsync(target, Guid.NewGuid(), input.GetProperty("message").GetString()!, ct);
                return JsonSerializer.Serialize(new { updated.Id, updated.Status });
            case "stop_thread":
                var stopped = input.GetProperty("thread_id").GetGuid();
                if (stopped == Cockpit.Id) throw new WorkspaceException("Use the Stop button to stop The Cockpit.");
                await StopAsync(stopped, ct);
                return "Stop requested. A final report will follow.";
            default: throw new WorkspaceException("Unknown orchestration tool.");
        }
    }

    private Task AppendAsync(Guid id, string role, string text, CancellationToken ct) => Locked(async () =>
    {
        var thread = await Find(id, ct);
        thread.Messages.Add(Message(thread, role, text));
        await store.SaveAsync(thread, ct);
        return true;
    }, ct);

    private static WorkspaceMessage Message(WorkspaceThread thread, string role, string text) => new()
    {
        Id = Guid.NewGuid(), ThreadId = thread.Id, Sequence = thread.Messages.Count + 1,
        Role = role, Text = text.Length > 32000 ? text[..32000] + "\n[Output truncated]" : text
    };
    private async Task<WorkspaceThread> Find(Guid id, CancellationToken ct) =>
        await store.FindAsync(id, ct) ?? throw new WorkspaceException("Thread not found.");

    private async Task<T> Locked<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_initialized)
            {
                if (await store.FindAsync(Cockpit.Id, ct) is null)
                    await store.SaveAsync(new() { Id = Cockpit.Id, Title = "The Cockpit", UpdatedAt = DateTime.UtcNow }, ct);
                foreach (var thread in (await store.ListAsync(ct)).Where(x => x.Status == "running"))
                {
                    var run = thread.ActiveRunId;
                    thread.Status = "interrupted";
                    thread.ActiveRunId = null;
                    thread.Messages = (await Find(thread.Id, ct)).Messages;
                    thread.Messages.Add(Message(thread, "error", "The server restarted during this run. Send a follow-up to continue."));
                    await store.CompleteAsync(thread, thread.Id == Cockpit.Id ? null : new()
                    {
                        Id = run ?? Guid.NewGuid(), ThreadId = thread.ParentThreadId ?? Cockpit.Id, SourceThreadId = thread.Id,
                        Role = "report", Text = thread.Title + " · interrupted\nThe server restarted. Worktree changes are preserved."
                    }, ct);
                }
                _initialized = true;
                await WakeCockpitLocked(ct);
            }
            return await action();
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _shutdown.CancelAsync();
        await Task.WhenAll(_tasks.Values);
        _shutdown.Dispose();
        _gate.Dispose();
    }
}
