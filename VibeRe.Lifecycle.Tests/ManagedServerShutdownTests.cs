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
        processes.UsingInstallation.Add(new(999, "utc:13", Path.Combine(state.InstallationDirectory, "server", "vibe-re-server")));
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
        if (mismatch == "start") processes.Live[server.ProcessId] = identity with { StartIdentity = identity.StartIdentity + "-different" };
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
    public async Task RecycledServerPidRemovesOnlyStaleRecordAndNeverSignalsNewOccupant()
    {
        var server = AddServer();
        processes.Live[server.ProcessId] = new(server.ProcessId, "utc:99", Path.Combine(root, "unrelated-process"));
        processes.UsingInstallation.Clear();
        await using var lease = await Guard().AcquireUpdateLeaseAsync();
        Assert.Empty(shutdown.Requests);
        Assert.Empty(processes.Waited);
        Assert.True(processes.Live.ContainsKey(server.ProcessId));
        Assert.False(File.Exists(state.DescriptorPath(server.PipeName)));
    }

    [Fact]
    public async Task RecycledPidRunningAnotherInstalledServerStillBlocksUpdateWithoutShutdown()
    {
        var server = AddServer();
        var replacement = new ManagedProcessIdentity(server.ProcessId, "utc:99", server.ExecutablePath);
        processes.Live[server.ProcessId] = replacement;
        processes.UsingInstallation.Clear();
        processes.UsingInstallation.Add(replacement);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Guard().AcquireUpdateLeaseAsync());
        Assert.Empty(shutdown.Requests);
        Assert.Empty(processes.Waited);
        Assert.False(File.Exists(state.DescriptorPath(server.PipeName)));
        Assert.False(File.Exists(state.UpdatePath));
    }

    [Fact]
    public async Task StartupReservationWithoutIdentityBlocksUpdate()
    {
        var pipe = NewPipe();
        state.Write(state.PendingPath(pipe), new PendingServerLaunch(1, state.InstallationId, pipe,
            Path.Combine(state.InstallationDirectory, "server"), 100, "utc:1"));
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
            Path.Combine(state.InstallationDirectory, "server"), 100, "utc:1", 101, "utc:2"));
        await using var lease = await Guard().AcquireUpdateLeaseAsync();
        Assert.False(File.Exists(state.PendingPath(pipe)));
    }

    [Fact]
    public async Task RecycledStartupPidDoesNotBlockOrSignalUnrelatedProcess()
    {
        var pipe = NewPipe();
        state.Write(state.PendingPath(pipe), new PendingServerLaunch(1, state.InstallationId, pipe,
            Path.Combine(state.InstallationDirectory, "server"), 100, "utc:1", 101, "utc:2"));
        processes.Live[101] = new(101, "utc:99", Path.Combine(root, "unrelated-process"));
        await using var lease = await Guard().AcquireUpdateLeaseAsync();
        Assert.False(File.Exists(state.PendingPath(pipe)));
        Assert.Empty(shutdown.Requests);
        Assert.True(processes.Live.ContainsKey(101));
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

    [Theory]
    [InlineData("server")]
    [InlineData("cli/server")]
    public async Task DirectBundledServerSharesDesktopReplacementGate(string relativeDirectory)
    {
        Directory.CreateDirectory(Path.Combine(state.InstallationDirectory, "server"));
        Directory.CreateDirectory(Path.Combine(state.InstallationDirectory, "cli", "server"));
        File.WriteAllText(Path.Combine(state.InstallationDirectory, "VibeRe.Desktop.dll"), "fixture");
        File.WriteAllText(Path.Combine(state.InstallationDirectory, "cli", "vibe-re.dll"), "fixture");
        var server = new ManagedServerState(Path.Combine(state.InstallationDirectory, relativeDirectory),
            Path.Combine(root, "state"), "", "");
        Assert.Equal(state.InstallationId, server.InstallationId);
        Assert.Equal(state.UpdatePath, server.UpdatePath);
        await using var lease = await Guard().AcquireUpdateLeaseAsync();
        Assert.Throws<InvalidOperationException>(() => server.AcquireGate());
        Assert.Throws<InvalidOperationException>(() => server.ThrowIfUpdatePending());
    }

    [Fact]
    public void BundledServerSharesStandaloneCliIdentityWithoutAdoptingSiblingInstallation()
    {
        var cliDirectory = Path.Combine(root, "cli-only");
        Directory.CreateDirectory(Path.Combine(cliDirectory, "server"));
        File.WriteAllText(Path.Combine(cliDirectory, "vibe-re.dll"), "fixture");
        var cli = new ManagedServerState(cliDirectory, Path.Combine(root, "state"), "", "");
        var server = new ManagedServerState(Path.Combine(cliDirectory, "server"), Path.Combine(root, "state"), "", "");
        Assert.Equal(cli.InstallationId, server.InstallationId);
        Assert.NotEqual(state.InstallationId, server.InstallationId);
    }

    [Fact]
    public void UnrelatedStandaloneServerKeepsItsOwnInstallationIdentity()
    {
        var directory = Path.Combine(state.InstallationDirectory, "server");
        Directory.CreateDirectory(directory);
        var server = new ManagedServerState(directory, Path.Combine(root, "state"), "", "");
        Assert.Equal(ManagedServerState.Normalize(directory), server.InstallationDirectory);
        Assert.NotEqual(state.InstallationId, server.InstallationId);
        ManagedServerShutdownGuard.EnsureUnmanagedServerCanStart(directory);
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
            NewPipe(), 101, "utc:12", Path.Combine(other.InstallationDirectory, "server"));
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
            NewPipe(), 101, "utc:12", Path.Combine(state.InstallationDirectory, "server"));
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
            NewPipe(), 101, "utc:12", Path.Combine(state.InstallationDirectory, "server"));
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
        Assert.False(string.IsNullOrWhiteSpace(identity.StartIdentity));
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
    public void ExplicitRecoveryClearsOrphanReservationWithoutAnUpdateMarker()
    {
        var pipe = NewPipe();
        state.Write(state.PendingPath(pipe), new PendingServerLaunch(1, state.InstallationId, pipe,
            Path.Combine(state.InstallationDirectory, "server"), 100, "utc:1"));
        ManagedServerShutdownGuard.RecoverInterruptedUpdate(state, processes);
        Assert.False(File.Exists(state.PendingPath(pipe)));
        Assert.Empty(shutdown.Requests);
    }

    [Theory]
    [InlineData("launcher")]
    [InlineData("child")]
    [InlineData("installed")]
    [InlineData("updater")]
    [InlineData("inspection")]
    public void ExplicitRecoveryRetainsOrphanReservationWhenQuiescenceCannotBeConfirmed(string blocker)
    {
        var pipe = NewPipe();
        state.Write(state.PendingPath(pipe), new PendingServerLaunch(1, state.InstallationId, pipe,
            Path.Combine(state.InstallationDirectory, "server"), 100, "utc:1", 101, "utc:2"));
        if (blocker == "launcher") processes.Live[100] = new(100, "utc:1", Path.Combine(root, "launcher"));
        // A still-starting shell may not yet appear in the installation-path scan.
        if (blocker == "child") processes.Live[101] = new(101, "utc:2", Path.Combine(root, "shell"));
        if (blocker == "installed") processes.UsingInstallation.Add(new(999, "utc:3", Path.Combine(state.InstallationDirectory, "server")));
        if (blocker == "updater") processes.UpdaterRunning = true;
        if (blocker == "inspection") processes.FailInspection = true;
        WriteMarker("fixture");
        Assert.Throws<InvalidOperationException>(() => ManagedServerShutdownGuard.RecoverInterruptedUpdate(state, processes));
        Assert.True(File.Exists(state.PendingPath(pipe)));
        Assert.True(File.Exists(state.UpdatePath));
        Assert.Empty(shutdown.Requests);
    }

    [Fact]
    public void ExplicitRecoveryDoesNotSignalRecycledLauncherOrChildPids()
    {
        var pipe = NewPipe();
        state.Write(state.PendingPath(pipe), new PendingServerLaunch(1, state.InstallationId, pipe,
            Path.Combine(state.InstallationDirectory, "server"), 100, "utc:1", 101, "utc:2"));
        processes.Live[100] = new(100, "utc:98", Path.Combine(root, "unrelated-one"));
        processes.Live[101] = new(101, "utc:99", Path.Combine(root, "unrelated-two"));
        ManagedServerShutdownGuard.RecoverInterruptedUpdate(state, processes);
        Assert.False(File.Exists(state.PendingPath(pipe)));
        Assert.Equal(2, processes.Live.Count);
        Assert.Empty(shutdown.Requests);
    }

    [Fact]
    public void ExplicitRecoveryValidatesAllReservationsBeforeDeletingAny()
    {
        var orphan = NewPipe();
        state.Write(state.PendingPath(orphan), new PendingServerLaunch(1, state.InstallationId, orphan,
            Path.Combine(state.InstallationDirectory, "server"), 100, "utc:1"));
        var invalid = NewPipe();
        state.Write(state.PendingPath(invalid), new PendingServerLaunch(1, "other-installation", invalid,
            Path.Combine(state.InstallationDirectory, "server"), 100, "utc:1"));
        Assert.Throws<InvalidOperationException>(() => ManagedServerShutdownGuard.RecoverInterruptedUpdate(state, processes));
        Assert.True(File.Exists(state.PendingPath(orphan)));
        Assert.True(File.Exists(state.PendingPath(invalid)));
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

    [Fact]
    public void SymlinkedParentAndPhysicalInstallationHaveOneIdentity()
    {
        Directory.CreateDirectory(state.InstallationDirectory);
        var alias = Path.Combine(root, "alias-parent");
        Directory.CreateSymbolicLink(alias, root);
        var viaAlias = new ManagedServerState(Path.Combine(alias, "installation"), Path.Combine(root, "state"), "", "");
        Assert.Equal(state.InstallationDirectory, viaAlias.InstallationDirectory);
        Assert.Equal(state.InstallationId, viaAlias.InstallationId);
        Assert.Equal(state.DirectoryPath, viaAlias.DirectoryPath);
        Assert.True(ManagedServerState.IsWithin(Path.Combine(alias, "installation", "server", "vibe-re-server"), state.InstallationDirectory));
    }

    [Fact]
    public async Task GuardViaSymlinkedParentFindsAndStopsSameOwnedServer()
    {
        Directory.CreateDirectory(state.InstallationDirectory);
        var descriptor = AddServer();
        var alias = Path.Combine(root, "alias-parent");
        Directory.CreateSymbolicLink(alias, root);
        var viaAlias = new ManagedServerState(Path.Combine(alias, "installation"), Path.Combine(root, "state"), "", "");
        await using var lease = await new ManagedServerShutdownGuard(viaAlias, processes, shutdown, TimeSpan.FromSeconds(2)).AcquireUpdateLeaseAsync();
        Assert.Equal([descriptor.ProcessId], shutdown.Requests);
        Assert.Equal([descriptor.ProcessId], processes.Waited);
    }

    [Fact]
    public void SymlinkInsideInstallationCannotEnrollAnOutsideExecutable()
    {
        Directory.CreateDirectory(state.InstallationDirectory);
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        var alias = Path.Combine(state.InstallationDirectory, "linked-server");
        Directory.CreateSymbolicLink(alias, outside);
        var executable = Path.Combine(alias, "vibe-re-server");
        Assert.False(ManagedServerState.IsWithin(executable, state.InstallationDirectory));
        Assert.Throws<InvalidOperationException>(() => new ManagedServerLaunch(state, executable));
    }

    [Fact]
    public void ChainedAndRelativeAncestorLinksAreCanonicalized()
    {
        var target = Path.Combine(root, "physical");
        Directory.CreateDirectory(target);
        var first = Path.Combine(root, "first-alias");
        var second = Path.Combine(root, "second-alias");
        Directory.CreateSymbolicLink(first, "physical");
        Directory.CreateSymbolicLink(second, first);
        Assert.Equal(ManagedServerState.Normalize(Path.Combine(target, "absent", "server")),
            ManagedServerState.Normalize(Path.Combine(second, "absent", "server")));
    }

    [Fact]
    public void LinuxStartIdentityParsesKernelTicksAfterComplexCommandName()
    {
        var fields = Enumerable.Range(3, 20).Select(field => field == 3 ? "S" : field == 22 ? "123456789" : "0");
        var stat = "101 (server (test) with spaces\nand newline) " + string.Join(' ', fields) + " 999 1000\n";
        Assert.Equal(123456789L, ManagedProcessStartIdentity.ParseLinuxStat(stat, 101));
    }

    [Theory]
    [InlineData("999 (server) S 0 0 0", 101)]
    [InlineData("101 malformed", 101)]
    [InlineData("101 (server) S 0 0 0", 101)]
    public void LinuxStartIdentityRejectsMalformedOrDifferentProcess(string stat, int expectedProcessId)
        => Assert.Throws<InvalidOperationException>(() => ManagedProcessStartIdentity.ParseLinuxStat(stat, expectedProcessId));

    [Fact]
    public void CurrentProcessCreationIdentityIsStableAcrossSeparateProcessObjects()
    {
        using var first = Process.GetCurrentProcess();
        using var second = Process.GetProcessById(first.Id);
        Assert.Equal(ManagedProcessStartIdentity.Read(first), ManagedProcessStartIdentity.Read(second));
    }

    [Fact]
    public void LinuxCreationIdentityDistinguishesRebootsWithSamePidAndStartTicks()
    {
        var oldBoot = ManagedProcessStartIdentity.CreateLinuxIdentity("39e180a3-50c0-4967-b99d-4cef4a5c8797", 12345);
        var newBoot = ManagedProcessStartIdentity.CreateLinuxIdentity("9f2ba8d6-e2fb-4d38-97da-408f95ab5674", 12345);
        Assert.NotEqual(oldBoot, newBoot);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    [InlineData("9223372036854775808")]
    public void LinuxStartIdentityRejectsInvalidKernelToken(string invalidTicks)
    {
        var fields = Enumerable.Range(3, 20).Select(field => field == 3 ? "S" : field == 22 ? invalidTicks : "0");
        var stat = "101 (server) " + string.Join(' ', fields);
        Assert.Throws<InvalidOperationException>(() => ManagedProcessStartIdentity.ParseLinuxStat(stat, 101));
    }

    [Fact]
    public void LinuxMissingProcessFileRemainsAnExitedProcessSignal()
    {
        Assert.Throws<FileNotFoundException>(() => ManagedProcessStartIdentity.ReadLinuxProcessIdentity(101,
            "39e180a3-50c0-4967-b99d-4cef4a5c8797", _ => throw new FileNotFoundException()));
        Assert.Throws<DirectoryNotFoundException>(() => ManagedProcessStartIdentity.ReadLinuxProcessIdentity(101,
            "39e180a3-50c0-4967-b99d-4cef4a5c8797", _ => throw new DirectoryNotFoundException()));
    }

    [Fact]
    public void LinuxUnreadableProcessFileFailsClosedInsteadOfPretendingProcessExited()
    {
        Assert.Throws<InvalidOperationException>(() => ManagedProcessStartIdentity.ReadLinuxProcessIdentity(101,
            "39e180a3-50c0-4967-b99d-4cef4a5c8797", _ => throw new UnauthorizedAccessException()));
        Assert.Throws<InvalidOperationException>(() => ManagedProcessStartIdentity.ReadLinuxProcessIdentity(101,
            "39e180a3-50c0-4967-b99d-4cef4a5c8797", _ => throw new IOException("I/O error")));
    }

    [Fact]
    public void FirstRunCreatesMissingApplicationDataParentsAndPrivateState()
    {
        var appData = Path.Combine(root, "fresh-app-data", "nested");
        Assert.False(Directory.Exists(appData));
        var fresh = new ManagedServerState(state.InstallationDirectory, Path.Combine(appData, "VibeRe", "managed-servers"), "", "");
        Assert.True(Directory.Exists(fresh.DirectoryPath));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(fresh.DirectoryPath));
    }

    private ManagedServerShutdownGuard Guard(TimeSpan? timeout = null) => new(state, processes, shutdown, timeout ?? TimeSpan.FromSeconds(2));
    private ManagedServerDescriptor AddServer()
    {
        var descriptor = new ManagedServerDescriptor(1, state.InstallationId, state.InstallationDirectory,
            NewPipe(), 101, "utc:12", Path.Combine(state.InstallationDirectory, "server"));
        state.Write(state.DescriptorPath(descriptor.PipeName), descriptor);
        var identity = new ManagedProcessIdentity(descriptor.ProcessId, descriptor.StartIdentity, descriptor.ExecutablePath);
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
