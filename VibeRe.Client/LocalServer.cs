using System.Diagnostics;
using System.Net.Sockets;

namespace VibeRe.Client;

/// <summary>Starts a background <c>vibe-re-server</c> when nothing is listening on a local address.</summary>
internal static class LocalServer
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(500);

    private static string ExecutableName => OperatingSystem.IsWindows() ? "vibe-re-server.exe" : "vibe-re-server";

    public static async Task EnsureRunningAsync(Uri address, CancellationToken cancellationToken)
    {
        if (!address.IsLoopback || await IsListeningAsync(address, cancellationToken))
        {
            return;
        }

        var executable = FindExecutable() ?? throw new ServerUnavailableException(
            address,
            $"Couldn't reach the VibeRe server at {address}, and {ExecutableName} isn't installed. Run `make install`.");
        var home = HomeDirectory();
        Directory.CreateDirectory(home);
        var logPath = Path.Combine(home, "server.log");
        Start(executable, home, logPath);

        var elapsed = Stopwatch.StartNew();
        while (!await IsListeningAsync(address, cancellationToken))
        {
            if (elapsed.Elapsed > StartupTimeout)
            {
                throw new ServerUnavailableException(
                    address, $"Started {executable}, but it isn't listening on {address}. See {logPath}.");
            }

            await Task.Delay(100, cancellationToken);
        }
    }

    private static async Task<bool> IsListeningAsync(Uri address, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(address.Host, address.Port, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return false;
        }
    }

    /// <summary>Looks next to the running client first (how <c>make install</c> lays it out), then on PATH.</summary>
    private static string? FindExecutable()
    {
        var directories = new[] { Path.GetDirectoryName(Environment.ProcessPath), AppContext.BaseDirectory }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        return directories
            .Where(directory => !string.IsNullOrEmpty(directory))
            .Select(directory => Path.Combine(directory!, ExecutableName))
            .FirstOrDefault(File.Exists);
    }

    private static void Start(string executable, string workingDirectory, string logPath)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory,
            })?.Dispose();
            return;
        }

        // Detach through the shell: a background job ignores Ctrl+C in this terminal, nohup survives the
        // terminal closing, and the server's output goes to a log instead of the user's terminal.
        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
            ArgumentList = { "-c", "nohup \"$0\" >>\"$1\" 2>&1 </dev/null &", executable, logPath },
        };
        using var shell = Process.Start(startInfo)
                          ?? throw new InvalidOperationException($"Couldn't start {executable}.");
        shell.WaitForExit();
    }

    /// <summary>Per-user data directory: <c>$VIBE_RE_HOME</c>, or <c>~/.vibe-re</c>, matching the server.</summary>
    private static string HomeDirectory() =>
        Environment.GetEnvironmentVariable("VIBE_RE_HOME") is { Length: > 0 } home
            ? Path.GetFullPath(home)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vibe-re");
}
