using System.Text.Json;

namespace VibeRe.Server.Application.Workspace;

// VibeRe owns the model/tool loop. Provider adapters supply completions, never thread identity or lifecycle.
public sealed class CodingHarness(IAgentModel model, IWorkspaceTools tools) : ICodingAgent
{
    public async Task<string> RunAsync(string root, string worktree, string prompt, Func<string, Task> progress, CancellationToken ct)
    {
        await tools.PrepareAsync(root, worktree, ct);
        var messages = new List<ModelMessage>
        {
            new("system", "You are VibeRe's coding agent. Work in the provided git worktree. " +
                "Use read_file, write_file, list_files and run_command to inspect, edit and validate. " +
                "Keep changes focused. Never commit, push, deploy or contact others unless explicitly requested. " +
                "A worktree separates git changes; commands still run on the server. " +
                "Finish with a concise report of actual changes, checks and remaining issues. " +
                "Working directory: " + worktree),
            new("user", prompt)
        };
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromMinutes(30));
        try
        {
            for (var step = 0; step < 40; step++)
            {
                var reply = await model.CompleteAsync(messages, ToolDefinitions, limit.Token);
                if (!string.IsNullOrWhiteSpace(reply.Text)) await progress(reply.Text);
                if (reply.Tools.Count == 0)
                    return string.IsNullOrWhiteSpace(reply.Text) ? throw new WorkspaceException("The model ended without a final report.") : reply.Text;
                messages.Add(new("assistant", reply.Text, reply.Tools));
                foreach (var call in reply.Tools)
                {
                    limit.Token.ThrowIfCancellationRequested();
                    await progress("→ " + call.Name + "\n" + call.Arguments);
                    string output;
                    try { output = await tools.ExecuteAsync(worktree, call.Name, call.Arguments, limit.Token); }
                    catch (Exception error) when (error is WorkspaceException or JsonException or ArgumentException or IOException or UnauthorizedAccessException)
                    { output = error is WorkspaceException ? error.Message : "The tool could not complete this request. Check the path and arguments."; }
                    await progress(output);
                    messages.Add(new("tool", output, ToolCallId: call.Id));
                }
                // Bound portable context while retaining the task and recent complete tool exchanges.
                if (messages.Sum(x => x.Text.Length) > 240000)
                    throw new WorkspaceException("The run reached its context limit. Send a follow-up to continue from the worktree.");
            }
            throw new WorkspaceException("The run reached its 40-step limit. Worktree changes are preserved; send a follow-up to continue.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new WorkspaceException("The run reached its 30-minute limit. Worktree changes are preserved."); }
    }

    private static ToolParameter Text(string description) => new("string", description);
    public static readonly AgentTool[] ToolDefinitions =
    [
        new("list_files", "List entries in a directory inside the worktree.", new() { ["path"] = Text("Relative directory; use . for root") }, ["path"]),
        new("read_file", "Read a UTF-8 file in the worktree (up to 32 KB).", new() { ["path"] = Text("Relative file path") }, ["path"]),
        new("write_file", "Create or replace a UTF-8 file in the worktree.", new() { ["path"] = Text("Relative file path"), ["content"] = Text("Complete file contents") }, ["path", "content"]),
        new("run_command", "Run a shell command in the worktree, bounded to two minutes. Returns exit code and output.", new() { ["command"] = Text("Shell command to inspect, build or test") }, ["command"])
    ];
}
