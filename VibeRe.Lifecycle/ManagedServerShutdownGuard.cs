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

    private List<(ManagedServerDescriptor, ManagedProcessIdentity)> ReadOwnedServers()
    {
        foreach (var path in Directory.EnumerateFiles(state.DirectoryPath, "*.pending"))
        {
            var pending = state.Read<PendingServerLaunch>(path);
            ManagedServerState.ValidatePipe(pending.PipeName);
            if (pending.Protocol != ManagedServerState.ProtocolVersion || pending.InstallationId != state.InstallationId)
                throw UnverifiedOwnership();
            if (File.Exists(state.DescriptorPath(pending.PipeName))) continue;
            if (pending.ProcessId is int pid && pending.StartTimeUtcTicks is not null && processes.Inspect(pid) is null)
            { File.Delete(path); continue; }
            throw new InvalidOperationException("A local server launch has not confirmed ownership yet. Wait for startup to finish, or close that server yourself, before updating.");
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
            if (identity is null) { File.Delete(path); File.Delete(state.PendingPath(descriptor.PipeName)); continue; }
            VerifyProcess(descriptor, identity);
            result.Add((descriptor, identity));
        }
        return result;
    }

    private static void VerifyProcess(ManagedServerDescriptor descriptor, ManagedProcessIdentity process)
    {
        if (descriptor.ProcessId != process.ProcessId || descriptor.StartTimeUtcTicks != process.StartTimeUtcTicks
            || !descriptor.ExecutablePath.Equals(process.ExecutablePath, ManagedServerState.PathComparison)) throw UnverifiedOwnership();
    }

    private static InvalidOperationException UnverifiedOwnership() => new(
        "The local server's ownership could not be verified. Close it yourself before updating; user-managed servers are never stopped automatically.");

    /// <summary>
    /// Explicit recovery only, after the user has completed reinstalling/repairing the installation.
    /// Does not stop processes. Refuses while other installed applications or known updater hosts run.
    /// </summary>
    public static void RecoverInterruptedUpdate(string installationDirectory)
        => RecoverInterruptedUpdate(new ManagedServerState(installationDirectory), new ManagedProcessInspector());

    internal static void RecoverInterruptedUpdate(ManagedServerState state, IManagedProcessInspector processes)
    {
        using var gate = state.AcquireGate();
        if (!File.Exists(state.UpdatePath)) return;
        var marker = state.Read<PendingApplicationUpdate>(state.UpdatePath);
        if (marker.Protocol != ManagedServerState.ProtocolVersion || marker.InstallationId != state.InstallationId) throw UnverifiedOwnership();
        if (processes.HasPotentialUpdater())
            throw new InvalidOperationException("An installer or updater is still running. Wait for it to finish before repairing VibeRe's update state.");
        if (processes.FindProcessesUsingInstallation(state.InstallationDirectory, state.AppImagePath).Any(process => process.ProcessId != Environment.ProcessId))
            throw new InvalidOperationException("Close all other VibeRe windows, CLI commands, and local servers from this installation before repairing its update state. No process was stopped.");
        File.Delete(state.UpdatePath);
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
