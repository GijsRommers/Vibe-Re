using System.Diagnostics;
using VibeRe.Lifecycle;

namespace VibeRe.Client.Infrastructure.Adapters;

public sealed class LocalServerProcess : ILocalServerProcess
{
    public void Start(ServerConnection server, string logPath)
    {
        var (executable, arguments, managed) = FindCommand();
        arguments.Add("--urls");
        arguments.Add(server.Address);
        Launch(executable, arguments, logPath, managed);
    }

    private static (string Executable, List<string> Arguments, bool Managed) FindCommand()
    {
        var configured = Environment.GetEnvironmentVariable("VIBE_RE_SERVER_EXECUTABLE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!File.Exists(configured)) throw new LocalServerStartupException("Configured local server executable was not found.");
            return (Path.GetFullPath(configured), [], false);
        }
        var name = OperatingSystem.IsWindows() ? "vibe-re-server.exe" : "vibe-re-server";
        var sibling = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(sibling)) return (sibling, [], true);
        var bundled = Path.Combine(AppContext.BaseDirectory, "server", name);
        if (File.Exists(bundled)) return (bundled, [], true);
        // Source checkouts can start the server directly, without a separate build/setup command.
        foreach (var startingDirectory in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var directory = new DirectoryInfo(startingDirectory); directory is not null; directory = directory.Parent)
            {
                var project = Path.Combine(directory.FullName, "VibeRe.Server", "VibeRe.Server.csproj");
                if (File.Exists(project)) return ("dotnet", ["run", "--no-launch-profile", "--project", project, "--"], false);
            }
        }
        foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(path, name);
            if (File.Exists(candidate)) return (Path.GetFullPath(candidate), [], false);
        }
        throw new LocalServerStartupException("Local server executable was not found. Install vibe-re-server beside the client or set VIBE_RE_SERVER_EXECUTABLE.");
    }

    private static void Launch(string executable, List<string> arguments, string logPath, bool managed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath))!);
        ProcessStartInfo start;
        if (OperatingSystem.IsWindows())
        {
            start = new(executable) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
        }
        else
        {
            // Pass executable/arguments as positional parameters, never interpolate them into shell code.
            // exec preserves the child PID across nohup; detached background shells used to discard it.
            // Ignored terminal signals and redirected handles keep the server alive after the client exits.
            start = new("/bin/sh") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("trap '' INT QUIT; exec nohup \"$@\" >>\"$VIBE_RE_LOCAL_SERVER_LOG\" 2>&1 </dev/null");
            start.ArgumentList.Add("vibere-local-server");
            start.ArgumentList.Add(executable);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.Environment["VIBE_RE_LOCAL_SERVER_LOG"] = Path.GetFullPath(logPath);
        }
        try
        {
            // Explicit overrides/PATH/source servers are user-managed and never enrolled for app updates.
            using var launch = managed ? new ManagedServerLaunch(AppContext.BaseDirectory, executable) : null;
            ManagedServerLaunch.ClearInheritedRegistration(start);
            launch?.Configure(start);
            using var process = Process.Start(start) ?? throw new LocalServerStartupException("Could not launch the local server.");
            launch?.RecordStarted(process);
            launch?.WaitForRegistration(TimeSpan.FromSeconds(15));
        }
        catch (InvalidOperationException exception)
        {
            throw new LocalServerStartupException(exception.Message);
        }
    }
}
