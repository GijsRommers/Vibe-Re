using System.Diagnostics;
using System.IO.Pipes;
using VibeRe.Lifecycle;

namespace VibeRe.Lifecycle.Tests;

public sealed class ManagedServerShutdownTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vibere-lifecycle-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManagedServerState state;
    private readonly FakeProcesses processes = new();
    private readonly FakeShutdown shutdown = new();

    public ManagedServerShutdownTests()
    {
        state = new ManagedServerState(Path.Combine(root, "installation"), Path.Combine(root, "state"), "", "");
    }

    [Fact]
    public async Task EmptyInstallationNeverSendsShutdown()
    {
        await using var lease = await Guard().AcquireUpdateLeaseAsync();
        Assert.Empty(shutdown.Requests);
        Assert.True(File.Exists(state.UpdatePath));
    }

    [Fact]
    public async Task OwnedServerStopsGracefullyAndBlocksLaunchUntilLeaseDisposed()
    {
        var server = AddServer();
        var lease = await Guard().AcquireUpdateLeaseAsync();
        Assert.Equal([server.ProcessId], shutdown.Requests);
        Assert.Equal([server.ProcessId], processes.Waited);
        Assert.False(File.Exists(state.DescriptorPath(server.PipeName)));
        Assert.Throws<InvalidOperationException>(() => new ManagedServerLaunch(state, server.ExecutablePath));
        await lease.DisposeAsync();
        Assert.False(File.Exists(state.UpdatePath));
        using var launch = new ManagedServerLaunch(state, server.ExecutablePath);
    }

    [Fact]
    public async Task OtherCliOrUserManagedServerBlocksBeforeAnyManagedServerStops()
    {
        AddServer();
        processes.UsingInstallation.Add(new(999, 13, Path.Combine(state.InstallationDirectory, "server", "vibe-re-server")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Guard().AcquireUpdateLeaseAsync());
        Assert.Contains("user-managed", error.Message);
        Assert.Empty(shutdown.Requests);
        Assert.False(File.Exists(state.UpdatePath));
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("start")]
    [InlineData("path")]
    [InlineData("installation")]
    public async Task UnverifiedProcessIsNeverStopped(string mismatch)
    {
        var server = AddServer();
        var identity = processes.Live[server.ProcessId];
        if (mismatch == "pid") processes.Live[server.ProcessId] = identity with { ProcessId = 999 };
        if (mismatch == "start") processes.Live[server.ProcessId] = identity with { StartTimeUtcTicks = identity.StartTimeUtcTicks + 1 };
        if (mismatch == "path") processes.Live[server.ProcessId] = identity with { ExecutablePath = Path.Combine(root, "unmanaged-server") };
        if (mismatch == "installation") state.Write(state.DescriptorPath(server.PipeName), server with { InstallationId = "other-installation" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Guard().AcquireUpdateLeaseAsync());
        Assert.Empty(shutdown.Requests);
        Assert.False(File.Exists(state.UpdatePath));
    }

    [Fact]
    public async Task InspectFailureFailsClosedWithoutShutdown()
    {
        AddServer();
        processes.FailInspection = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Guard().AcquireUpdateLeaseAsync());
        Assert.Empty(shutdown.Requests);
        Assert.False(File.Exists(state.UpdatePath));
    }

    [Fact]
    public async Task AlreadyExitedServerRecordIsSafelyRemoved()
    {
        var server = AddServer();
        processes.Live.Clear();
        processes.UsingInstallation.Clear();
        await using var lease = await Guard().AcquireUpdateLeaseAsync();
        Assert.Empty(shutdown.Requests);
        Assert.False(File.Exists(state.DescriptorPath(server.PipeName)));
    }

    [Fact]
    public async Task StartupReservationWithoutIdentityBlocksUpdate()
    {
        var pipe = NewPipe();
        state.Write(state.PendingPath(pipe), new PendingServerLaunch(1, state.InstallationId, pipe,
            Path.Combine(state.InstallationDirectory, "server"), 100, 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Guard().AcquireUpdateLeaseAsync());
        Assert.Empty(shutdown.Requests);
        Assert.True(File.Exists(state.PendingPath(pipe)));
        Assert.False(File.Exists(state.UpdatePath));
    }

    [Fact]
    public async Task ExitedKnownStartupReservationDoesNotBlockUpdate()
    {
        var pipe = NewPipe();
        state.Write(state.PendingPath(pipe), new PendingServerLaunch(1, state.InstallationId, pipe,
            Path.Combine(state.InstallationDirectory, "server"), 100, 1, 101, 2));
        await using var lease = await Guard().AcquireUpdateLeaseAsync();
        Assert.False(File.Exists(state.PendingPath(pipe)));
    }

    [Fact]
    public async Task ShutdownTimeoutDoesNotKillAndReleasesLaunchExclusion()
    {
        AddServer();
        shutdown.WaitForever = true;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Guard(TimeSpan.FromMilliseconds(20)).AcquireUpdateLeaseAsync());
        Assert.Contains("No process was force-closed", error.Message);
        Assert.Contains("Refresh connection", error.Message);
        Assert.Empty(processes.Waited);
        Assert.False(File.Exists(state.UpdatePath));
    }

    [Fact]
    public async Task CancellationReleasesLeaseAndDoesNotSuppressNextLaunch()
    {
        AddServer();
        shutdown.WaitForever = true;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Guard().AcquireUpdateLeaseAsync(cancellation.Token));
        Assert.False(File.Exists(state.UpdatePath));
        using var launch = new ManagedServerLaunch(state, Path.Combine(state.InstallationDirectory, "server"));
    }

    [Fact]
    public async Task FailedApplyMarkerBlocksRetryAndNormalLaunchUntilSuccessfulRepairHook()
    {
        WriteMarker(ManagedServerState.BuildIdentity);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Guard().AcquireUpdateLeaseAsync());
        Assert.Contains("--repair-update", error.Message);
        Assert.Throws<InvalidOperationException>(() => new ManagedServerLaunch(state, Path.Combine(state.InstallationDirectory, "server")));
        ManagedServerShutdownGuard.Complete(state, allowSameBuild: false);
        Assert.True(File.Exists(state.UpdatePath));
        ManagedServerShutdownGuard.Complete(state, allowSameBuild: true);
        Assert.False(File.Exists(state.UpdatePath));
        await using var lease = await Guard().AcquireUpdateLeaseAsync();
    }

    [Fact]
    public void SuccessfulNewBuildRestartClearsMarker()
    {
        WriteMarker("previous-build");
        ManagedServerShutdownGuard.Complete(state, allowSameBuild: false);
        Assert.False(File.Exists(state.UpdatePath));
    }

    [Fact]
    public void NestedBundledCliSharesDesktopInstallationIdentity()
    {
        Directory.CreateDirectory(Path.Combine(state.InstallationDirectory, "server"));
        File.WriteAllText(Path.Combine(state.InstallationDirectory, "VibeRe.Desktop.dll"), "fixture");
        var cli = new ManagedServerState(Path.Combine(state.InstallationDirectory, "cli"), Path.Combine(root, "state"), "", "");
        Assert.Equal(state.InstallationId, cli.InstallationId);
    }

    [Fact]
    public void AppImageIdentityIsStableAcrossDistinctMountsAndSeparateForAnotherImage()
    {
        if (!OperatingSystem.IsLinux()) return;
        var first = new ManagedServerState("/tmp/.mount_vibe1/usr/bin", Path.Combine(root, "state"), "/apps/VibeRe.AppImage", "/tmp/.mount_vibe1");
        var second = new ManagedServerState("/tmp/.mount_vibe2/usr/bin", Path.Combine(root, "state"), "/apps/VibeRe.AppImage", "/tmp/.mount_vibe2");
        var other = new ManagedServerState("/tmp/.mount_vibe3/usr/bin", Path.Combine(root, "state"), "/apps/Other.AppImage", "/tmp/.mount_vibe3");
        Assert.Equal(first.InstallationId, second.InstallationId);
        Assert.NotEqual(first.InstallationId, other.InstallationId);
    }

    [Fact]
    public void UnrelatedAppImageEnvironmentDoesNotAdoptAnotherInstallation()
    {
        var normal = new ManagedServerState(state.InstallationDirectory, Path.Combine(root, "state"), "/apps/Other.AppImage", "/unrelated/mount");
        Assert.Equal(state.InstallationId, normal.InstallationId);
        Assert.False(normal.IsAppImage);
    }

    [Fact]
    public async Task AnotherInstallationRecordsAreNeverReadOrStopped()
    {
        var other = new ManagedServerState(Path.Combine(root, "other-install"), Path.Combine(root, "state"), "", "");
        var descriptor = new ManagedServerDescriptor(1, other.InstallationId, other.InstallationDirectory,
            NewPipe(), 101, 12, Path.Combine(other.InstallationDirectory, "server"));
        other.Write(other.DescriptorPath(descriptor.PipeName), descriptor);
        await using var lease = await Guard().AcquireUpdateLeaseAsync();
        Assert.Empty(shutdown.Requests);
        Assert.True(File.Exists(other.DescriptorPath(descriptor.PipeName)));
    }

    [Fact]
    [Trait("Category", "IPC")]
    public async Task SameUserPipeRequestsOnlyExpectedProcessShutdown()
    {
        var descriptor = new ManagedServerDescriptor(1, state.InstallationId, state.InstallationDirectory,
            NewPipe(), 101, 12, Path.Combine(state.InstallationDirectory, "server"));
        using var pipe = ManagedServerRegistration.CreatePipe(descriptor.PipeName);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var requested = false;
        var listen = ManagedServerRegistration.ListenAsync(pipe, descriptor, () => requested = true, timeout.Token);
        await new ManagedShutdownClient().RequestShutdownAsync(descriptor, timeout.Token);
        await listen;
        Assert.True(requested);
    }

    [Fact]
    [Trait("Category", "IPC")]
    public async Task PipeRejectsWrongProcessIdentityAndAcceptsLaterValidRequest()
    {
        var descriptor = new ManagedServerDescriptor(1, state.InstallationId, state.InstallationDirectory,
            NewPipe(), 101, 12, Path.Combine(state.InstallationDirectory, "server"));
        using var pipe = ManagedServerRegistration.CreatePipe(descriptor.PipeName);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var requested = false;
        var listen = ManagedServerRegistration.ListenAsync(pipe, descriptor, () => requested = true, timeout.Token);
        using (var wrong = new NamedPipeClientStream(".", descriptor.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await wrong.ConnectAsync(timeout.Token);
            await ManagedPipeProtocol.WriteLineAsync(wrong, ManagedPipeProtocol.Request(descriptor with { ProcessId = 999 }), timeout.Token);
            var buffer = new byte[1];
            Assert.Equal(0, await wrong.ReadAsync(buffer, timeout.Token));
        }
        Assert.False(requested);
        await new ManagedShutdownClient().RequestShutdownAsync(descriptor, timeout.Token);
        await listen;
        Assert.True(requested);
    }

    [Fact]
    public void CurrentProcessIdentityCanBeVerifiedWithoutStoppingIt()
    {
        var identity = new ManagedProcessInspector().Inspect(Environment.ProcessId);
        Assert.NotNull(identity);
        Assert.Equal(Environment.ProcessId, identity.ProcessId);
        Assert.True(identity.StartTimeUtcTicks > 0);
        Assert.True(File.Exists(identity.ExecutablePath));
    }

    [Fact]
    public void UnixStateFilesAndDirectoriesArePrivate()
    {
        if (OperatingSystem.IsWindows()) return;
        WriteMarker("fixture");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(state.DirectoryPath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(state.UpdatePath));
    }

    [Fact]
    public async Task UnsafeUnixPermissionsAreRejected()
    {
        if (OperatingSystem.IsWindows()) return;
        AddServer();
        var path = Directory.GetFiles(state.DirectoryPath, "vibere-*.json").Single();
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Guard().AcquireUpdateLeaseAsync());
        Assert.Empty(shutdown.Requests);
    }

    [Fact]
    public void UserManagedLaunchNeverInheritsManagedRegistration()
    {
        var start = new ProcessStartInfo("fixture");
        start.Environment[ManagedServerState.PipeEnvironment] = "inherited";
        start.Environment[ManagedServerState.DirectoryEnvironment] = "inherited";
        start.Environment[ManagedServerState.IdentityEnvironment] = "inherited";
        ManagedServerLaunch.ClearInheritedRegistration(start);
        Assert.False(start.Environment.ContainsKey(ManagedServerState.PipeEnvironment));
        Assert.False(start.Environment.ContainsKey(ManagedServerState.DirectoryEnvironment));
        Assert.False(start.Environment.ContainsKey(ManagedServerState.IdentityEnvironment));
    }

    [Fact]
    public async Task MalformedOwnershipRecordFailsClosedWithActionableError()
    {
        var descriptor = AddServer();
        state.Write(state.DescriptorPath(descriptor.PipeName), new { Protocol = 1 });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Guard().AcquireUpdateLeaseAsync());
        Assert.Contains("ownership record is invalid", error.Message);
        Assert.Empty(shutdown.Requests);
        Assert.False(File.Exists(state.UpdatePath));
    }

    [Fact]
    public async Task CorruptPendingUpdateIsNotReplacedOrDeletedByRetry()
    {
        state.Write(state.UpdatePath, new { Invalid = "fixture" });
        var original = File.ReadAllText(state.UpdatePath);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Guard().AcquireUpdateLeaseAsync());
        Assert.Contains("--repair-update", error.Message);
        Assert.Equal(original, File.ReadAllText(state.UpdatePath));
    }

    [Fact]
    public void ExplicitRecoveryClearsValidMarkerWhenAllOtherProcessesAreClosed()
    {
        WriteMarker(ManagedServerState.BuildIdentity);
        ManagedServerShutdownGuard.RecoverInterruptedUpdate(state, processes);
        Assert.False(File.Exists(state.UpdatePath));
        Assert.Empty(shutdown.Requests);
    }

    [Fact]
    public void ExplicitRecoveryRefusesKnownUpdaterAndRetainsMarker()
    {
        WriteMarker(ManagedServerState.BuildIdentity);
        processes.UpdaterRunning = true;
        Assert.Throws<InvalidOperationException>(() => ManagedServerShutdownGuard.RecoverInterruptedUpdate(state, processes));
        Assert.True(File.Exists(state.UpdatePath));
    }

    [Fact]
    public void ExplicitRecoveryNeverStopsEvenOwnedRunningServers()
    {
        AddServer();
        WriteMarker(ManagedServerState.BuildIdentity);
        Assert.Throws<InvalidOperationException>(() => ManagedServerShutdownGuard.RecoverInterruptedUpdate(state, processes));
        Assert.True(File.Exists(state.UpdatePath));
        Assert.Empty(shutdown.Requests);
    }

    [Fact]
    public async Task ExplicitRecoveryCannotReleaseLiveUpdateLease()
    {
        await using var lease = await Guard().AcquireUpdateLeaseAsync();
        Assert.Throws<InvalidOperationException>(() => ManagedServerShutdownGuard.RecoverInterruptedUpdate(state, processes));
        Assert.True(File.Exists(state.UpdatePath));
    }

    private ManagedServerShutdownGuard Guard(TimeSpan? timeout = null) => new(state, processes, shutdown, timeout ?? TimeSpan.FromSeconds(2));
    private ManagedServerDescriptor AddServer()
    {
        var descriptor = new ManagedServerDescriptor(1, state.InstallationId, state.InstallationDirectory,
            NewPipe(), 101, 12, Path.Combine(state.InstallationDirectory, "server"));
        state.Write(state.DescriptorPath(descriptor.PipeName), descriptor);
        var identity = new ManagedProcessIdentity(descriptor.ProcessId, descriptor.StartTimeUtcTicks, descriptor.ExecutablePath);
        processes.Live[identity.ProcessId] = identity;
        processes.UsingInstallation.Add(identity);
        return descriptor;
    }
    private void WriteMarker(string build) => state.Write(state.UpdatePath, new PendingApplicationUpdate(1, state.InstallationId, build, "fixture"));
    private static string NewPipe() => "vibere-" + Guid.NewGuid().ToString("N");
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

    private sealed class FakeProcesses : IManagedProcessInspector
    {
        internal Dictionary<int, ManagedProcessIdentity> Live { get; } = [];
        internal List<ManagedProcessIdentity> UsingInstallation { get; } = [];
        internal List<int> Waited { get; } = [];
        internal bool FailInspection { get; set; }
        internal bool UpdaterRunning { get; set; }
        public bool HasPotentialUpdater() => UpdaterRunning;
        public ManagedProcessIdentity? Inspect(int processId) => FailInspection
            ? throw new InvalidOperationException("Process access denied.") : Live.GetValueOrDefault(processId);
        public IReadOnlyList<ManagedProcessIdentity> FindProcessesUsingInstallation(string installationDirectory, string? appImagePath = null) => UsingInstallation;
        public Task WaitForExitAsync(ManagedProcessIdentity identity, CancellationToken cancellationToken)
        { Waited.Add(identity.ProcessId); Live.Remove(identity.ProcessId); return Task.CompletedTask; }
    }
    private sealed class FakeShutdown : IManagedShutdownClient
    {
        internal List<int> Requests { get; } = [];
        internal bool WaitForever { get; set; }
        public async Task RequestShutdownAsync(ManagedServerDescriptor descriptor, CancellationToken cancellationToken)
        {
            Requests.Add(descriptor.ProcessId);
            if (WaitForever) await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }
}
