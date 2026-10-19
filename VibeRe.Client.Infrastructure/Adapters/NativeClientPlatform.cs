namespace VibeRe.Client.Infrastructure.Adapters;

internal static class NativeClientPlatform
{
    public static ClientCapabilities Capabilities { get; } =
        new(!OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS() && !OperatingSystem.IsBrowser());

    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vibe-re");
}
