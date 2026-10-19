using VibeRe.Client.Application;

namespace VibeRe.Mobile;

public sealed class MobileBrowserLauncher : IBrowserLauncher
{
    public async Task OpenAsync(Uri uri) => await Launcher.Default.OpenAsync(uri);
}
