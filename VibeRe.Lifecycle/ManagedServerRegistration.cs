using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace VibeRe.Lifecycle;

/// <summary>Current-user IPC available only on an explicitly auto-launched bundled server.</summary>
public sealed class ManagedServerRegistration : IDisposable
{
    private readonly ManagedServerState state;
    private readonly ManagedServerDescriptor descriptor;
    private readonly NamedPipeServerStream pipe;
    private bool disposed;

    private ManagedServerRegistration(ManagedServerState state, string pipeName)
    {
        this.state = state;
        var pending = state.Read<PendingServerLaunch>(state.PendingPath(pipeName));
        using var process = Process.GetCurrentProcess();
        var executable = ManagedServerState.Normalize(Environment.ProcessPath
            ?? throw new InvalidOperationException("The managed server executable cannot be identified."));
        if (pending.Protocol != ManagedServerState.ProtocolVersion || pending.InstallationId != state.InstallationId || pending.PipeName != pipeName
            || !ManagedServerState.IsWithin(executable, state.InstallationDirectory)
            || !executable.Equals(pending.ExpectedExecutable, ManagedServerState.PathComparison)
            || (pending.ProcessId is int expectedId && expectedId != process.Id)
            || (pending.StartTimeUtcTicks is long expectedStart && expectedStart != process.StartTime.ToUniversalTime().Ticks))
            throw new InvalidOperationException("The managed server launch identity could not be verified.");
        descriptor = new(ManagedServerState.ProtocolVersion, state.InstallationId, state.InstallationDirectory,
            pipeName, process.Id, process.StartTime.ToUniversalTime().Ticks, executable);
        pipe = CreatePipe(pipeName);
        try { state.Write(state.DescriptorPath(pipeName), descriptor); }
        catch { pipe.Dispose(); throw; }
        // Ready is written first. An updater always sees either an outstanding launch or its ready record.
        // The launcher may still be recording its child PID; it removes any rewritten pending record.
        File.Delete(state.PendingPath(pipeName));
    }

    internal static NamedPipeServerStream CreatePipe(string pipeName) => new(pipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    public static ManagedServerRegistration? TryCreateFromEnvironment()
    {
        var pipeName = Environment.GetEnvironmentVariable(ManagedServerState.PipeEnvironment);
        var directory = Environment.GetEnvironmentVariable(ManagedServerState.DirectoryEnvironment);
        var identity = Environment.GetEnvironmentVariable(ManagedServerState.IdentityEnvironment);
        Environment.SetEnvironmentVariable(ManagedServerState.PipeEnvironment, null);
        Environment.SetEnvironmentVariable(ManagedServerState.DirectoryEnvironment, null);
        Environment.SetEnvironmentVariable(ManagedServerState.IdentityEnvironment, null);
        if (pipeName is null && directory is null && identity is null) return null;
        if (string.IsNullOrWhiteSpace(pipeName) || string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(identity))
            throw new InvalidOperationException("The managed server launch information is incomplete.");
        var state = new ManagedServerState(directory);
        if (state.InstallationId != identity) throw new InvalidOperationException("The managed server installation could not be verified.");
        return new ManagedServerRegistration(state, ManagedServerState.ValidatePipe(pipeName));
    }

    public Task ListenForShutdownAsync(Action stopApplication, CancellationToken cancellationToken)
        => ListenAsync(pipe, descriptor, stopApplication, cancellationToken);

    internal static async Task ListenAsync(NamedPipeServerStream pipe, ManagedServerDescriptor descriptor,
        Action stopApplication, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                var request = await ManagedPipeProtocol.ReadLineAsync(pipe, requestTimeout.Token);
                if (request == ManagedPipeProtocol.Request(descriptor))
                {
                    await ManagedPipeProtocol.WriteLineAsync(pipe, ManagedPipeProtocol.Response(descriptor), requestTimeout.Token);
                    stopApplication();
                    return;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (IOException) when (!cancellationToken.IsCancellationRequested) { }
            finally { if (pipe.IsConnected) pipe.Disconnect(); }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        pipe.Dispose();
        try
        {
            if (File.Exists(state.DescriptorPath(descriptor.PipeName))
                && state.Read<ManagedServerDescriptor>(state.DescriptorPath(descriptor.PipeName)) == descriptor)
                File.Delete(state.DescriptorPath(descriptor.PipeName));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException) { } // A stale descriptor is safe; the updater verifies the live process identity.
    }
}

internal static class ManagedPipeProtocol
{
    internal static string Request(ManagedServerDescriptor descriptor) => $"vibere-managed-v1:{descriptor.ProcessId}:{descriptor.StartTimeUtcTicks}:shutdown";
    internal static string Response(ManagedServerDescriptor descriptor) => $"vibere-managed-v1:{descriptor.ProcessId}:{descriptor.StartTimeUtcTicks}:stopping";
    internal static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var single = new byte[1];
        while (bytes.Count < 256)
        {
            if (await stream.ReadAsync(single, cancellationToken) == 0) throw new IOException("The managed server closed its update connection.");
            if (single[0] == '\n') return Encoding.ASCII.GetString(bytes.ToArray());
            bytes.Add(single[0]);
        }
        throw new IOException("Invalid managed-server update response.");
    }
    internal static async Task WriteLineAsync(Stream stream, string value, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(value + "\n"), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
