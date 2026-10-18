using Velopack.Exceptions;
using VibeRe.Lifecycle;

namespace VibeRe.Updates;

/// <summary>
/// Explicit Check -> Download -> Restart flow. This class never schedules or silently applies an update.
/// StateChanged can run on a worker thread; UI subscribers must dispatch to their UI thread.
/// </summary>
public sealed class DesktopUpdateService(IUpdateBackend backend, IServerShutdownGuard shutdownGuard)
{
    private readonly SemaphoreSlim operation = new(1, 1);
    private UpdateCandidate? candidate;
    private UpdateStatus state = new(UpdateStage.Idle, "Check for a new VibeRe version.", backend.CurrentVersion?.ToString());
    public UpdateStatus State => Volatile.Read(ref state);
    public event Action<UpdateStatus>? StateChanged;

    public Task<UpdateStatus> CheckAsync(CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        candidate = null;
        if (backend.UnavailableReason is { } reason) return Set(UpdateStage.Unsupported, reason);
        Set(UpdateStage.Checking, "Checking GitHub Releases for this device…");
        candidate = await backend.CheckAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (candidate is null) return Set(UpdateStage.UpToDate, "No newer compatible stable release is available.");
        ValidateCandidate(candidate);
        return Set(UpdateStage.Available, $"VibeRe {candidate.Version} is available. Download it when you're ready.");
    }, cancellationToken);

    public Task<UpdateStatus> DownloadAsync(CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        if (candidate is null || State.Stage is not (UpdateStage.Available or UpdateStage.Error))
            return State;
        ValidateCandidate(candidate);
        Set(UpdateStage.Downloading, "Downloading and verifying the update…", 0);
        await backend.DownloadAsync(candidate, percent => Set(UpdateStage.Downloading,
            "Downloading and verifying the update…", Math.Clamp(percent, 0, 100)), cancellationToken).ConfigureAwait(false);
        await backend.VerifyAsync(candidate, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return Set(UpdateStage.ReadyToRestart,
            "Update downloaded and verified. Save your work, then choose Restart and update. The local server will stop briefly.", 100);
    }, cancellationToken);

    /// <param name="userConfirmed">True only for the user's explicit Restart and update action.</param>
    public Task<UpdateStatus> ApplyAndRestartAsync(bool userConfirmed, CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        // A missing consent must not change a ready state or stop any server.
        if (!userConfirmed || candidate is null || State.Stage != UpdateStage.ReadyToRestart) return State;
        ValidateCandidate(candidate);
        // Detect tampering before stopping the user's server, and again immediately before apply.
        await backend.VerifyAsync(candidate, cancellationToken).ConfigureAwait(false);
        Set(UpdateStage.Applying, "Stopping this installation's local server and restarting to update…");
        await using var lease = await shutdownGuard.AcquireUpdateLeaseAsync(cancellationToken).ConfigureAwait(false);
        await backend.VerifyAsync(candidate, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        backend.ApplyAndRestart(candidate); // Production exits here; the guard lease remains held until process exit.
        return State;
    }, cancellationToken);

    private void ValidateCandidate(UpdateCandidate value)
    {
        if (backend.CurrentVersion is not { } current || value.Version <= current || value.Version.IsPrerelease)
            throw new UpdateSafetyException("This update is not a newer stable version. Check for updates again.");
        if (value.Channel != backend.Channel || value.RuntimeIdentifier != backend.RuntimeIdentifier)
            throw new UpdateSafetyException("The update is for a different device or release channel. It was not installed.");
    }

    private async Task<UpdateStatus> RunAsync(Func<Task<UpdateStatus>> action, CancellationToken cancellationToken)
    {
        // Double-clicks and concurrent commands must not queue a surprise download/restart.
        if (!await operation.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return State;
        try { return await action().ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Set(UpdateStage.Error, "Update canceled. Your installed version is unchanged. Check again when you're ready.");
        }
        catch (ChecksumFailedException)
        {
            return Set(UpdateStage.Error, "The update failed its integrity check and was not installed. Check again or download a fresh installer from GitHub Releases.");
        }
        catch (AcquireLockFailedException)
        {
            return Set(UpdateStage.Error, "Another VibeRe update is in progress. Close other VibeRe windows and try again.");
        }
        catch (UpdateSafetyException ex)
        {
            return Set(UpdateStage.Error, ex.Message + (State.Stage == UpdateStage.Applying
                ? " Use Refresh connection to restart the local server if it stopped." : ""));
        }
        catch (Exception ex)
        {
            // Lifecycle guards use actionable InvalidOperationException messages for uncertain ownership.
            if (State.Stage == UpdateStage.Applying)
                return Set(UpdateStage.Error, ex is InvalidOperationException ? ex.Message
                    : "The updater could not start. Use Refresh connection to restart the local server if it stopped, then check again or use a fresh installer from GitHub Releases.");
            return Set(UpdateStage.Error,
                "VibeRe couldn't complete the update. The release feed may be private or unavailable, or no compatible release has been published yet. Check your connection and try again, or open GitHub Releases.");
        }
        finally { operation.Release(); }
    }

    private UpdateStatus Set(UpdateStage stage, string message, int? percent = null)
    {
        var next = new UpdateStatus(stage, message, backend.CurrentVersion?.ToString(), candidate?.Version.ToString(), percent);
        Volatile.Write(ref state, next);
        // An observer's UI failure must never turn a completed safety check into an update failure.
        if (StateChanged is { } handlers)
            foreach (Action<UpdateStatus> handler in handlers.GetInvocationList())
                try { handler(next); } catch { /* Subscribers own their rendering errors. */ }
        return next;
    }
}
