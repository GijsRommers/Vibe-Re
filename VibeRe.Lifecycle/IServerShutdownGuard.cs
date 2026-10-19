namespace VibeRe.Lifecycle;

/// <summary>Quiesces only servers launched by this installation and prevents new launches during apply.</summary>
public interface IServerShutdownGuard
{
    /// <summary>
    /// Hold the returned lease until the updater exits this application. Disposing it cancels the apply
    /// reservation. A successful update clears the reservation in the restarted application's lifecycle hook.
    /// </summary>
    Task<IAsyncDisposable> AcquireUpdateLeaseAsync(CancellationToken cancellationToken = default);
}
