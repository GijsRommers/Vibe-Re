using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VibeRe.Client.Application;
using VibeRe.Client.Application.Workspace;
using VibeRe.Domain.Access;
using VibeRe.Server.Application.Access;
using VibeRe.Server.Application.Workspace;
using VibeRe.Server.Infrastructure.Persistence;
using VibeRe.Server.Infrastructure.Workspace;

namespace VibeRe.Server.Tests;

public sealed class WorkspaceIntegrationTests
{
    [Fact]
    public async Task CockpitLaunchesNativeHarnessAndGetsOneDurableReportThenWakes()
    {
        using var repository = new Repository();
        var model = new ReplayModel();
        using var server = Server(repository.Root, model);
        await using var client = server.Connect();
        var workspace = (IWorkspaceClient)client;
        var initial = await workspace.GetWorkspaceAsync();
        Assert.Equal(CockpitThread.Id, Assert.Single(initial.Threads).Id);
        var command = Guid.NewGuid();
        await workspace.SendThreadAsync(CockpitThread.Id, command, "Create a coding thread", default);
        await workspace.SendThreadAsync(CockpitThread.Id, command, "Create a coding thread", default);
        var state = await Wait(async () => await workspace.GetWorkspaceAsync(), snapshot =>
            snapshot.Threads.Count == 2 && snapshot.Threads.All(x => x.Status is "completed" or "failed"));
        foreach (var failed in state.Threads.Where(x => x.Status == "failed"))
            Assert.Fail(string.Join("\n", (await workspace.ReadThreadAsync(failed.Id)).Messages.Select(x => x.Text)));
        var child = Assert.Single(state.Threads, x => x.Id != CockpitThread.Id);
        Assert.Equal(CockpitThread.Id, child.ParentThreadId);
        var detail = await workspace.ReadThreadAsync(child.Id);
        Assert.Equal("hello from the native harness", await File.ReadAllTextAsync(Path.Combine(detail.WorktreePath!, "greeting.txt")));
        Assert.False(File.Exists(Path.Combine(repository.Root, "greeting.txt")));
        Assert.Contains(detail.Messages, x => x.Role == "activity" && x.Text.Contains("Exit code: 0"));
        var cockpit = await workspace.ReadThreadAsync(CockpitThread.Id);
        Assert.Single(cockpit.Messages, x => x.Role == "user");
        Assert.Single(cockpit.Messages, x => x.Role == "report" && x.SourceThreadId == child.Id);
        Assert.Contains(cockpit.Messages, x => x.Role == "assistant" && x.Text == "Child report reviewed.");
        Assert.Equal(3, model.CodingSteps);
        var persisted = await server.Services.GetRequiredService<IWorkspaceStore>().FindAsync(Cockpit.Id, default);
        Assert.True(Assert.Single(persisted!.Messages, x => x.Role == "report").Handled);
    }

    [Fact]
    public async Task StopPreservesThreadAndReportsInterruption()
    {
        using var repository = new Repository();
        var model = new BlockingModel();
        using var server = Server(repository.Root, model);
        await using var client = server.Connect();
        var workspace = (IWorkspaceClient)client;
        await workspace.GetWorkspaceAsync();
        var thread = await workspace.CreateThreadAsync(Guid.NewGuid(), "Cancelable", "Inspect the repo");
        await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await workspace.StopThreadAsync(thread.Id);
        var stopped = await Wait(() => workspace.ReadThreadAsync(thread.Id), x => x.Status == "interrupted");
        Assert.Contains(stopped.Messages, x => x.Role == "error" && x.Text.Contains("preserved"));
        var cockpit = await Wait(() => workspace.ReadThreadAsync(CockpitThread.Id), x => x.Messages.Any(m => m.SourceThreadId == thread.Id));
        Assert.Single(cockpit.Messages, x => x.SourceThreadId == thread.Id);
        Assert.True(Directory.Exists(stopped.WorktreePath));
    }

    [Fact]
    public async Task RestartFinalizesInterruptedChildAndDoesNotDuplicateItsReport()
    {
        using var repository = new Repository();
        using var server = Server(repository.Root, new ReplayModel());
        var store = server.Services.GetRequiredService<IWorkspaceStore>();
        var id = Guid.NewGuid();
        await store.SaveAsync(new() { Id = Cockpit.Id, Title = "The Cockpit", AutoWake = false }, default);
        await store.SaveAsync(new() { Id = id, ParentThreadId = Cockpit.Id, Title = "Interrupted work", Status = "running", ActiveRunId = Guid.NewGuid() }, default);
        await using var client = server.Connect();
        var workspace = (IWorkspaceClient)client;
        await workspace.GetWorkspaceAsync();
        Assert.Equal("interrupted", (await workspace.ReadThreadAsync(id)).Status);
        await workspace.GetWorkspaceAsync();
        Assert.Single((await workspace.ReadThreadAsync(CockpitThread.Id)).Messages, x => x.SourceThreadId == id);
    }

    [Fact]
    public async Task ProviderOnlyPairedDeviceCannotAcquireCodeExecution()
    {
        using var server = new SignalRTestServer();
        var issuer = server.Services.GetRequiredService<IDeviceSessionIssuer>();
        var grant = await issuer.IssueSessionAsync("auth-only", TimeSpan.FromDays(1), DevicePermissions.ProviderAccess);
        await using var device = server.Connect(grant.Token);
        Assert.Equal(VibeRe.Client.Application.AuthState.SignedOut, (await device.GetStatusAsync()).State);
        var error = await Assert.ThrowsAsync<ServerConnectionException>(() => ((IWorkspaceClient)device).GetWorkspaceAsync());
        Assert.Contains("local administrator", error.Message);
    }

    private static SignalRTestServer Server(string root, IAgentModel model) => new()
    {
        ConfigureTestServices = services =>
        {
            services.RemoveAll<IAgentModel>(); services.AddSingleton(model);
            services.RemoveAll<WorkspaceOptions>(); services.AddSingleton(new WorkspaceOptions(root));
        }
    };
    private static async Task<T> Wait<T>(Func<Task<T>> read, Func<T, bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var state = await read();
            if (ready(state)) return state;
            await Task.Delay(30, timeout.Token);
        }
    }
    private sealed class ReplayModel : IAgentModel
    {
        public int CodingSteps;
        public Task<ModelReply> CompleteAsync(IReadOnlyList<ModelMessage> messages, IReadOnlyList<AgentTool> tools, CancellationToken ct)
        {
            if (tools.Any(x => x.Name == "delegate_task"))
            {
                if (messages.Any(x => x.Text.Contains("· completed"))) return Task.FromResult(new ModelReply("Child report reviewed.", []));
                if (messages.Any(x => x.Role == "tool")) return Task.FromResult(new ModelReply("Thread launched.", []));
                return Task.FromResult(new ModelReply("", [new("delegate1", "delegate_task", "{\"title\":\"Create greeting\",\"task\":\"Create greeting.txt and verify its contents\"}")]));
            }
            var step = Interlocked.Increment(ref CodingSteps);
            return Task.FromResult(step switch
            {
                1 => new ModelReply("Writing the file.", [new("write1", "write_file", "{\"path\":\"greeting.txt\",\"content\":\"hello from the native harness\"}")]),
                2 => new ModelReply("Validating.", [new("run1", "run_command", System.Text.Json.JsonSerializer.Serialize(new { command = OperatingSystem.IsWindows() ? "type greeting.txt" : "cat greeting.txt" }))]),
                _ => new ModelReply("Created greeting.txt and verified it with cat.", [])
            });
        }
    }
    private sealed class BlockingModel : IAgentModel
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ModelReply> CompleteAsync(IReadOnlyList<ModelMessage> messages, IReadOnlyList<AgentTool> tools, CancellationToken ct)
        {
            if (tools.Any(x => x.Name == "delegate_task")) return new("Stopped child acknowledged.", []);
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new("", []);
        }
    }
    private sealed class Repository : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "vibere-workspace-tests-" + Guid.NewGuid());
        public string Root { get; }
        public Repository()
        {
            Root = Path.Combine(_directory, "repo"); Directory.CreateDirectory(Root);
            Git("init", "-b", "main");
            File.WriteAllText(Path.Combine(Root, "README.md"), "Fixture repository");
            Git("add", "."); Git("-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-m", "Fixture");
        }
        private void Git(params string[] arguments)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = Root, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!; process.WaitForExit(); Assert.Equal(0, process.ExitCode);
        }
        public void Dispose() => Directory.Delete(_directory, true);
    }
}
