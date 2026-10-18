using System.Runtime.InteropServices;

namespace VibeRe.Updates;

public static class UpdatePlatform
{
    public const string AppId = "GijsRommers.VibeRe";
    public const string RepositoryUrl = "https://github.com/GijsRommers/Vibe-Re";
    public const string ReleasesUrl = RepositoryUrl + "/releases";

    public static string? CurrentRuntimeIdentifier => Resolve(
        OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "osx" : "unknown",
        RuntimeInformation.ProcessArchitecture, RuntimeInformation.OSArchitecture);

    public static string? Resolve(string os, Architecture processArchitecture, Architecture osArchitecture)
    {
        // Do not switch architecture, including an x64 process running under ARM emulation.
        if (processArchitecture != osArchitecture) return null;
        return (os, processArchitecture) switch
        {
            ("win", Architecture.X64) => "win-x64",
            ("linux", Architecture.X64) => "linux-x64",
            ("osx", Architecture.Arm64) => "osx-arm64",
            _ => null
        };
    }

    public static string ChannelFor(string runtimeIdentifier) => runtimeIdentifier switch
    {
        "win-x64" or "linux-x64" or "osx-arm64" => runtimeIdentifier + "-stable",
        _ => throw new ArgumentException("This platform has no supported VibeRe update channel.", nameof(runtimeIdentifier))
    };
}
