using System.Diagnostics;

namespace VibeRe.Client.Infrastructure.Adapters;

public sealed class SystemBrowserLauncher : IBrowserLauncher
{
    public Task OpenAsync(Uri uri)
    {
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}
