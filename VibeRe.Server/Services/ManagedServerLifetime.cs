using VibeRe.Lifecycle;

namespace VibeRe.Server.Services;

/// <summary>Never registered for a manually started server; shutdown uses only same-user local IPC.</summary>
public sealed class ManagedServerLifetime(ManagedServerRegistration registration, IHostApplicationLifetime lifetime)
    : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => registration.ListenForShutdownAsync(lifetime.StopApplication, stoppingToken);
}
