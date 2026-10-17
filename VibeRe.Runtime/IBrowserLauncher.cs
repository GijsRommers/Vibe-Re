namespace VibeRe.Runtime;

public interface IBrowserLauncher
{
    Task OpenAsync(Uri uri);
}
