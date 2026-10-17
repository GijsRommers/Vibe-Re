namespace VibeRe.Client;

public interface IBrowserLauncher
{
    Task OpenAsync(Uri uri);
}
