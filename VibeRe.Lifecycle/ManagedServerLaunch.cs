using System.Diagnostics;
using System.Security.Cryptography;

namespace VibeRe.Lifecycle;

/// <summary>A short launch reservation that is serialized with application replacement.</summary>
public sealed class ManagedServerLaunch : IDisposable
{
    private readonly ManagedServerState state;
    private readonly FileStream gate;
    private PendingServerLaunch pending;
    private bool started;

    public ManagedServerLaunch(string installationDirectory, string executable)
        : this(new ManagedServerState(installationDirectory), executable) { }

    internal ManagedServerLaunch(ManagedServerState state, string executable)
    {
        this.state = state;
        if (!ManagedServerState.IsWithin(executable, state.InstallationDirectory))
            throw new InvalidOperationException("Only a server bundled with this application can be managed for updates.");
        gate = state.AcquireGate();
        try
        {
            state.ThrowIfUpdatePending();
            PipeName = "vibere-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            using var owner = Process.GetCurrentProcess();
            pending = new(ManagedServerState.ProtocolVersion, state.InstallationId, PipeName,
                ManagedServerState.Normalize(executable), owner.Id, ManagedProcessStartIdentity.Read(owner));
            state.Write(state.PendingPath(PipeName), pending);
        }
        catch { gate.Dispose(); throw; }
    }

    public string PipeName { get; }
    public static void ClearInheritedRegistration(ProcessStartInfo start)
    {
        start.Environment.Remove(ManagedServerState.PipeEnvironment);
        start.Environment.Remove(ManagedServerState.DirectoryEnvironment);
        start.Environment.Remove(ManagedServerState.IdentityEnvironment);
    }

    public void Configure(ProcessStartInfo start)
    {
        start.Environment[ManagedServerState.PipeEnvironment] = PipeName;
        start.Environment[ManagedServerState.DirectoryEnvironment] = state.InstallationDirectory;
        start.Environment[ManagedServerState.IdentityEnvironment] = state.InstallationId;
    }

    /// <summary>Record the direct child PID. Unix launchers must exec, rather than create another detached child.</summary>
    public void RecordStarted(Process process)
    {
        started = true;
        try
        {
            pending = pending with { ProcessId = process.Id, StartIdentity = ManagedProcessStartIdentity.Read(process) };
            state.Write(state.PendingPath(PipeName), pending);
        }
        catch (Exception exception) when ((exception is InvalidOperationException or IOException) && process.HasExited)
        {
            File.Delete(state.PendingPath(PipeName));
            throw new InvalidOperationException("The local server exited before startup completed. Check the server log.");
        }
    }

    public void WaitForRegistration(TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            if (File.Exists(state.DescriptorPath(PipeName)))
            {
                var descriptor = state.Read<ManagedServerDescriptor>(state.DescriptorPath(PipeName));
                if (descriptor.ProcessId != pending.ProcessId || descriptor.StartIdentity != pending.StartIdentity
                    || descriptor.InstallationId != state.InstallationId || descriptor.PipeName != PipeName
                    || !descriptor.ExecutablePath.Equals(pending.ExpectedExecutable, ManagedServerState.PathComparison))
                    throw new InvalidOperationException("The launched local server identity could not be verified. Close it yourself before updating.");
                File.Delete(state.PendingPath(PipeName));
                return;
            }
            if (pending.ProcessId is int processId && new ManagedProcessInspector().Inspect(processId) is null)
            {
                File.Delete(state.PendingPath(PipeName));
                throw new InvalidOperationException("The local server exited before startup completed. Check the server log.");
            }
            Thread.Sleep(25);
        }
        // Retain the reservation: the server could still finish starting after this timeout.
        throw new InvalidOperationException("The local server did not confirm managed startup. Check the server log; updates are blocked until its ownership can be verified.");
    }

    public void Dispose()
    {
        if (!started) File.Delete(state.PendingPath(PipeName));
        gate.Dispose();
    }
}
