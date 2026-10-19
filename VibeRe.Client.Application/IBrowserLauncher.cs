namespace VibeRe.Client.Application;

public interface IBrowserLauncher
{
    Task OpenAsync(Uri uri);
}
