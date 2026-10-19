using System.IO.Pipes;

namespace VibeRe.Lifecycle;

public sealed class ManagedServerShutdownGuard : IServerShutdownGuard
{
    private readonly ManagedServerState state;
    private readonly IManagedProcessInspector processes;
    private readonly IManagedShutdownClient shutdown;
    private readonly TimeSpan shutdownTimeout;

    public ManagedServerShutdownGuard(string installationDirectory, TimeSpan? shutdownTimeout = null)
        : this(new ManagedServerState(installationDirectory), new ManagedProcessInspector(), new ManagedShutdownClient(),
            shutdownTimeout ?? TimeSpan.FromSeconds(30)) { }

    internal ManagedServerShutdownGuard(ManagedServerState state, IManagedProcessInspector processes,
        IManagedShutdownClient shutdown, TimeSpan shutdownTimeout)
    { this.state = state; this.processes = processes; this.shutdown = shutdown; this.shutdownTimeout = shutdownTimeout; }

    public async Task<IAsyncDisposable> AcquireUpdateLeaseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var gate = state.AcquireGate();
        var marker = new PendingApplicationUpdate(ManagedServerState.ProtocolVersion, state.InstallationId,
            ManagedServerState.BuildIdentity, Guid.NewGuid().ToString("N"));
        var lease = new UpdateLease(state, gate, marker);
        try
        {
            state.ThrowIfUpdatePending();
            state.Write(state.UpdatePath, marker);
            lease.Activate();
            // Validate every record before stopping any server. Never infer ownership from an HTTP address,
            // executable name, listening port, saved connection, or user-managed server process alone.
            var descriptors = ReadOwnedServers();
            EnsureNoUnownedProcesses(descriptors);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(shutdownTimeout);
            try
            {
                foreach (var (descriptor, identity) in descriptors)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var current = processes.Inspect(descriptor.ProcessId);
                    if (current is null) continue;
                    VerifyProcess(descriptor, current);
                    await shutdown.RequestShutdownAsync(descriptor, timeout.Token);
                    await processes.WaitForExitAsync(identity, timeout.Token);
                    File.Delete(state.DescriptorPath(descriptor.PipeName));
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("The managed local server did not finish shutting down. Wait for it to stop, then try the update again. No process was force-closed. Use Refresh connection to restart any server that already stopped.");
            }
            catch (IOException exception)
            {
                throw new InvalidOperationException("The managed local server could not confirm shutdown. Close that server yourself, then try the update again. Use Refresh connection to restart any server that already stopped.", exception);
            }
            return lease;
        }
        catch { await lease.DisposeAsync(); throw; }
    }

    private void EnsureNoUnownedProcesses(List<(ManagedServerDescriptor Descriptor, ManagedProcessIdentity Identity)> owned)
    {
        var directories = owned.Select(server => server.Descriptor.InstallationDirectory)
            .Append(state.InstallationDirectory).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var directory in directories)
        foreach (var process in processes.FindProcessesUsingInstallation(directory, state.AppImagePath))
        {
            if (process.ProcessId == Environment.ProcessId) continue;
            if (!owned.Any(server => server.Identity == process))
                throw new InvalidOperationException("Close other VibeRe windows, CLI commands, and user-managed local servers from this installation, then try updating again. They will not be stopped automatically.");
        }
    }

    /// <summary>Call from desktop and CLI entrypoints before initialization, after any successful installer hooks.</summary>
    public static void EnsureApplicationCanStart(string installationDirectory)
    {
        var state = new ManagedServerState(installationDirectory);
        using var gate = state.AcquireGate();
        state.ThrowIfUpdatePending();
    }

    /// <summary>Direct bundled servers obey their client's gate; unrelated standalone servers need no client state.</summary>
    public static void EnsureUnmanagedServerCanStart(string serverDirectory)
    {
        var directory = ManagedServerState.Normalize(serverDirectory);
        var installation = ManagedServerState.ResolveInstallationDirectory(directory);
        if (!installation.Equals(directory, ManagedServerState.PathComparison))
            EnsureApplicationCanStart(installation);
    }

    private List<(ManagedServerDescriptor, ManagedProcessIdentity)> ReadOwnedServers()
    {
        foreach (var path in Directory.EnumerateFiles(state.DirectoryPath, "*.pending"))
        {
            var pending = state.Read<PendingServerLaunch>(path);
            ManagedServerState.ValidatePipe(pending.PipeName);
            if (pending.Protocol != ManagedServerState.ProtocolVersion || pending.InstallationId != state.InstallationId)
                throw UnverifiedOwnership();
            if (File.Exists(state.DescriptorPath(pending.PipeName))) continue;
            if (pending.ProcessId is int pid && pending.StartIdentity is not null
                && RecordedProcessHasExited(processes, pid, pending.StartIdentity))
            { File.Delete(path); continue; }
            throw new InvalidOperationException("A local server launch has not confirmed ownership yet. Wait for startup to finish, or close that server yourself, before updating. If startup was interrupted, finish reinstalling the app, close all VibeRe processes, then run the desktop app with --repair-update.");
        }
        var result = new List<(ManagedServerDescriptor, ManagedProcessIdentity)>();
        foreach (var path in Directory.EnumerateFiles(state.DirectoryPath, "vibere-*.json"))
        {
            var descriptor = state.Read<ManagedServerDescriptor>(path);
            ManagedServerState.ValidatePipe(descriptor.PipeName);
            if (descriptor.Protocol != ManagedServerState.ProtocolVersion || descriptor.InstallationId != state.InstallationId
                || !Path.GetFileName(path).Equals(descriptor.PipeName + ".json", StringComparison.Ordinal)
                || !ManagedServerState.IsWithin(descriptor.ExecutablePath, descriptor.InstallationDirectory)
                || (!state.IsAppImage && !descriptor.InstallationDirectory.Equals(state.InstallationDirectory, ManagedServerState.PathComparison)))
                throw UnverifiedOwnership();
            var identity = processes.Inspect(descriptor.ProcessId);
            // A recycled PID is not the recorded server. Remove only the stale record; never
            // send shutdown to its new occupant. The installation scan still catches any new app.
            if (identity is null || (identity.ProcessId == descriptor.ProcessId && identity.StartIdentity != descriptor.StartIdentity))
            { File.Delete(path); File.Delete(state.PendingPath(descriptor.PipeName)); continue; }
            VerifyProcess(descriptor, identity);
            result.Add((descriptor, identity));
        }
        return result;
    }

    private static void VerifyProcess(ManagedServerDescriptor descriptor, ManagedProcessIdentity process)
    {
        if (descriptor.ProcessId != process.ProcessId || descriptor.StartIdentity != process.StartIdentity
            || !descriptor.ExecutablePath.Equals(process.ExecutablePath, ManagedServerState.PathComparison)) throw UnverifiedOwnership();
    }

    private static InvalidOperationException UnverifiedOwnership() => new(
        "The local server's ownership could not be verified. Close it yourself before updating; user-managed servers are never stopped automatically.");

    private static bool RecordedProcessHasExited(IManagedProcessInspector processes, int processId, string startIdentity)
    {
        var process = processes.Inspect(processId);
        if (process is null) return true;
        if (process.ProcessId != processId) throw UnverifiedOwnership();
        return process.StartIdentity != startIdentity;
    }

    /// <summary>
    /// Explicit recovery only, after the user has completed reinstalling/repairing the installation.
    /// Does not stop processes. Refuses while other installed applications or known updater hosts run.
    /// </summary>
    public static void RecoverInterruptedUpdate(string installationDirectory)
        => RecoverInterruptedUpdate(new ManagedServerState(installationDirectory), new ManagedProcessInspector());

    internal static void RecoverInterruptedUpdate(ManagedServerState state, IManagedProcessInspector processes)
    {
        using var gate = state.AcquireGate();
        var hasUpdate = File.Exists(state.UpdatePath);
        var pendingPaths = Directory.GetFiles(state.DirectoryPath, "*.pending");
        if (!hasUpdate && pendingPaths.Length == 0) return;
        if (hasUpdate)
        {
            var marker = state.Read<PendingApplicationUpdate>(state.UpdatePath);
            if (marker.Protocol != ManagedServerState.ProtocolVersion || marker.InstallationId != state.InstallationId) throw UnverifiedOwnership();
        }
        // Ordinary update attempts cannot infer a child identity after a launcher crash. Only
        // explicit recovery, after completed reinstall and with all installed processes closed,
        // may reconcile an orphan reservation. Validate every record before deleting anything.
        foreach (var path in pendingPaths)
        {
            var pending = state.Read<PendingServerLaunch>(path);
            ManagedServerState.ValidatePipe(pending.PipeName);
            if (pending.Protocol != ManagedServerState.ProtocolVersion || pending.InstallationId != state.InstallationId
                || !Path.GetFileName(path).Equals(pending.PipeName + ".pending", StringComparison.Ordinal))
                throw UnverifiedOwnership();
            if (!RecordedProcessHasExited(processes, pending.LauncherProcessId, pending.LauncherStartIdentity))
                throw new InvalidOperationException("A local server launcher is still running. Close it before repairing VibeRe's update state.");
            if (pending.ProcessId is int processId
                && (pending.StartIdentity is null || !RecordedProcessHasExited(processes, processId, pending.StartIdentity)))
                throw new InvalidOperationException("A local server launch is still running or cannot be verified. Close it before repairing VibeRe's update state.");
        }
        if (processes.HasPotentialUpdater())
            throw new InvalidOperationException("An installer or updater is still running. Wait for it to finish before repairing VibeRe's update state.");
        if (processes.FindProcessesUsingInstallation(state.InstallationDirectory, state.AppImagePath).Any(process => process.ProcessId != Environment.ProcessId))
            throw new InvalidOperationException("Close all other VibeRe windows, CLI commands, and local servers from this installation before repairing its update state. No process was stopped.");
        foreach (var path in pendingPaths) File.Delete(path);
        if (hasUpdate) File.Delete(state.UpdatePath);
    }

    /// <summary>Call only from Velopack's successful post-update restart hook, before client startup.</summary>
    public static void CompletePendingUpdate(string installationDirectory) => Complete(new ManagedServerState(installationDirectory), allowSameBuild: false);

    /// <summary>Call only from Velopack's successful installation/repair first-run hook, before client startup.</summary>
    public static void CompleteInstallation(string installationDirectory) => Complete(new ManagedServerState(installationDirectory), allowSameBuild: true);

    internal static void Complete(ManagedServerState state, bool allowSameBuild)
    {
        using var gate = state.AcquireGate();
        if (!File.Exists(state.UpdatePath)) return;
        var marker = state.Read<PendingApplicationUpdate>(state.UpdatePath);
        if (marker.Protocol != ManagedServerState.ProtocolVersion || marker.InstallationId != state.InstallationId) throw UnverifiedOwnership();
        if (allowSameBuild || marker.BuildIdentity != ManagedServerState.BuildIdentity) File.Delete(state.UpdatePath);
    }

    private sealed class UpdateLease(ManagedServerState state, FileStream gate, PendingApplicationUpdate marker) : IAsyncDisposable
    {
        private bool disposed;
        private bool active;
        internal void Activate() => active = true;
        public ValueTask DisposeAsync()
        {
            if (disposed) return ValueTask.CompletedTask;
            disposed = true;
            try
            {
                if (active && File.Exists(state.UpdatePath) && state.Read<PendingApplicationUpdate>(state.UpdatePath) == marker)
                    File.Delete(state.UpdatePath);
            }
            finally { gate.Dispose(); }
            return ValueTask.CompletedTask;
        }
    }
}

internal interface IManagedShutdownClient
{
    Task RequestShutdownAsync(ManagedServerDescriptor descriptor, CancellationToken cancellationToken);
}
internal sealed class ManagedShutdownClient : IManagedShutdownClient
{
    public async Task RequestShutdownAsync(ManagedServerDescriptor descriptor, CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(".", descriptor.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(cancellationToken);
        await ManagedPipeProtocol.WriteLineAsync(pipe, ManagedPipeProtocol.Request(descriptor), cancellationToken);
        if (await ManagedPipeProtocol.ReadLineAsync(pipe, cancellationToken) != ManagedPipeProtocol.Response(descriptor))
            throw new IOException("The managed server identity did not match its shutdown response.");
    }
}
