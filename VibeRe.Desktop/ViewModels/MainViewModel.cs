using System.Diagnostics;
using VibeRe.Client;
using VibeRe.Client.Configuration;

namespace VibeRe.Desktop.ViewModels;

public sealed class MainViewModel() : ConnectionSettingsViewModel(ServerConnectionManager.CreateDesktop(), OpenBrowserAsync)
{
    private static Task OpenBrowserAsync(Uri uri)
    {
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}
