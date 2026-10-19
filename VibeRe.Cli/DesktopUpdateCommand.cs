using System.Diagnostics;

namespace VibeRe.Cli;

public static class DesktopUpdateCommand
{
    public static int Run(string cliDirectory)
    {
        var desktop = FindDesktop(cliDirectory, OperatingSystem.IsWindows());
        if (desktop is null)
        {
            Console.Error.WriteLine("The desktop updater is not in this CLI-only installation. Install the complete VibeRe desktop package to use in-app updates.");
            return 1;
        }
        try
        {
            var start = new ProcessStartInfo(desktop) { UseShellExecute = false };
            start.ArgumentList.Add("--updates");
            using var process = Process.Start(start);
            if (process is null) throw new InvalidOperationException();
            Console.WriteLine("Opened VibeRe updates. Check, download, and approve installation in the desktop window.");
            return 0;
        }
        catch
        {
            Console.Error.WriteLine("Could not open the desktop updater. Open VibeRe and choose Updates, or use its platform installer.");
            return 1;
        }
    }

    public static string? FindDesktop(string cliDirectory, bool windows)
    {
        var parent = Directory.GetParent(Path.GetFullPath(cliDirectory).TrimEnd(Path.DirectorySeparatorChar));
        if (parent is null) return null;
        var name = "VibeRe.Desktop" + (windows ? ".exe" : "");
        // Velopack bundle and legacy portable package layouts. Never search PATH or the working directory.
        return new[] { Path.Combine(parent.FullName, name), Path.Combine(parent.FullName, "desktop", name) }
            .FirstOrDefault(File.Exists);
    }
}
