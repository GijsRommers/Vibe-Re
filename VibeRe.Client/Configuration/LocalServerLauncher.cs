using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Net;
using System.Text.Json;

namespace VibeRe.Client.Configuration;

public sealed class LocalServerLauncher
{
    public async Task EnsureRunningAsync(ServerConnection server, CancellationToken cancellationToken = default)
    {
        if (!server.StartLocally) return;
        var uri = new Uri(ServerConnection.NormalizeAddress(server.Address));
        if (!uri.IsLoopback) throw new ArgumentException("Only loopback servers can be started locally.");
        if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
            throw new LocalServerStartupException("Automatic server startup is available on desktop. Choose a reachable server on this device.");
        if (await IsReadyAsync(uri, cancellationToken)) return;

        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vibe-re");
        Directory.CreateDirectory(directory);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server.Address)))[..16];
        var lockPath = Path.Combine(directory, $"local-server-{hash}.lock");
        var deadline = DateTime.UtcNow.AddSeconds(45);
        await using var lease = await AcquireAsync(lockPath, uri, deadline, cancellationToken);
        if (lease is null || await IsReadyAsync(uri, cancellationToken)) return;
        // Check after taking the launch lock: another client may already be starting this server.
        if (await IsListeningAsync(uri, cancellationToken))
            throw new LocalServerStartupException("The local server port is occupied, but a compatible VibeRe server is not responding.");

        var logPath = Environment.GetEnvironmentVariable("VIBE_RE_SERVER_LOG") ?? Path.Combine(directory, "server.log");
        var (executable, arguments) = FindCommand();
        arguments.Add("--urls");
        arguments.Add(server.Address);
        Launch(executable, arguments, logPath);
        while (DateTime.UtcNow < deadline)
        {
            if (await IsReadyAsync(uri, cancellationToken)) return;
            await Task.Delay(100, cancellationToken);
        }
        throw new LocalServerStartupException($"Local server did not start in time. Check {logPath}.");
    }

    private static async Task<FileStream?> AcquireAsync(string path, Uri uri, DateTime deadline, CancellationToken ct)
    {
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
                if (await IsReadyAsync(uri, ct)) return null;
                await Task.Delay(100, ct);
            }
        }
        throw new LocalServerStartupException("Another client is starting the local server. Try again shortly.");
    }

    internal static async Task<bool> IsReadyAsync(Uri uri, CancellationToken ct)
    {
        using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(1) };
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, "/.well-known/vibe-re/server"))
            { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        try
        {
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return false;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return document.RootElement.TryGetProperty("application", out var app) && app.GetString() == "vibe-re" &&
                document.RootElement.TryGetProperty("protocolVersion", out var protocol) &&
                protocol.ValueKind == JsonValueKind.Number && protocol.TryGetInt32(out var value) && value == 1;
        }
        catch (HttpRequestException) { return false; }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    private static async Task<bool> IsListeningAsync(Uri uri, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(500));
        using var socket = new TcpClient();
        try { await socket.ConnectAsync(uri.Host, uri.Port, timeout.Token); return true; }
        catch (SocketException) { return false; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    internal static (string Executable, List<string> Arguments) FindCommand()
    {
        var configured = Environment.GetEnvironmentVariable("VIBE_RE_SERVER_EXECUTABLE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!File.Exists(configured)) throw new LocalServerStartupException("Configured local server executable was not found.");
            return (Path.GetFullPath(configured), []);
        }
        var name = OperatingSystem.IsWindows() ? "vibe-re-server.exe" : "vibe-re-server";
        var sibling = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(sibling)) return (sibling, []);
        var bundled = Path.Combine(AppContext.BaseDirectory, "server", name);
        if (File.Exists(bundled)) return (bundled, []);
        foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(path, name);
            if (File.Exists(candidate)) return (Path.GetFullPath(candidate), []);
        }
        // Source checkouts can start the server directly, without a separate build/setup command.
        foreach (var startingDirectory in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var directory = new DirectoryInfo(startingDirectory); directory is not null; directory = directory.Parent)
            {
                var project = Path.Combine(directory.FullName, "VibeRe.Server", "VibeRe.Server.csproj");
                if (File.Exists(project)) return ("dotnet", ["run", "--no-launch-profile", "--project", project, "--"]);
            }
        }
        throw new LocalServerStartupException("Local server executable was not found. Install vibe-re-server beside the client or set VIBE_RE_SERVER_EXECUTABLE.");
    }

    private static void Launch(string executable, List<string> arguments, string logPath)
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
            // nohup and file redirection keep the server alive after the client exits.
            start = new("/bin/sh") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("nohup \"$@\" >>\"$VIBE_RE_LOCAL_SERVER_LOG\" 2>&1 </dev/null &");
            start.ArgumentList.Add("vibere-local-server");
            start.ArgumentList.Add(executable);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.Environment["VIBE_RE_LOCAL_SERVER_LOG"] = Path.GetFullPath(logPath);
        }
        using var process = Process.Start(start) ?? throw new LocalServerStartupException("Could not launch the local server.");
    }
}
