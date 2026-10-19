using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using VibeRe.Lifecycle;

// Exercise real apphosts, outside the source tree, with no provider credentials.
if (args.Length != 2 || args[0] is not ("--root" or "--installed-root"))
    throw new ArgumentException("Usage: --root|--installed-root <directory containing cli, desktop, server>");
var source = Path.GetFullPath(args[1]);
var temp = Path.Combine(Path.GetTempPath(), "VibeRe package smoke " + Guid.NewGuid().ToString("N"));
var root = args[0] == "--installed-root" ? source : Path.Combine(temp, "installed apps");
var cwd = Path.Combine(temp, "unrelated working directory");
Directory.CreateDirectory(cwd);
var suffix = OperatingSystem.IsWindows() ? ".exe" : "";
var environment = new Dictionary<string, string?>
{
    ["VIBE_RE_CLIENT_DATABASE"] = Path.Combine(temp, "client.db"),
    ["VIBE_RE_SERVER_LOG"] = Path.Combine(temp, "server.log"),
    ["VIBE_RE_SERVER_EXECUTABLE"] = null,
    ["VIBE_RE_SERVER_TOKEN"] = "package-smoke-token",
    ["Server__AccessToken"] = null,
    ["ConnectionStrings__VibeRe"] = null,
    ["ASPNETCORE_CONTENTROOT"] = null,
    ["ASPNETCORE_ENVIRONMENT"] = "Production",
    ["DOTNET_ENVIRONMENT"] = "Production",
    // Prove the published apps don't use the SDK/shared frameworks.
    ["DOTNET_ROOT"] = Path.Combine(temp, "no-dotnet"),
    ["DOTNET_ROOT_X64"] = Path.Combine(temp, "no-dotnet"),
    ["DOTNET_ROOT_ARM64"] = Path.Combine(temp, "no-dotnet"),
    ["DOTNET_MULTILEVEL_LOOKUP"] = "0"
};
try
{
    if (root != source) CopyDirectory(source, root);
    var cli = Path.Combine(root, "cli", "vibe-re" + suffix);
    Require(!File.Exists(Path.Combine(root, "cli", "VibeRe.Desktop.dll")), "CLI output contains desktop files");
    Require(!File.Exists(Path.Combine(root, "server", "vibe-re.dll")), "Server output contains CLI files");
    Require(!File.Exists(Path.Combine(root, "desktop", "vibe-re.dll")), "Desktop output contains CLI files");
    await Run(cli, ["--help"], 0, "vibe-re");
    await Run(cli, ["servers", "list"], 0, "local");

    foreach (var app in new[] { "cli", "desktop", "server" })
    {
        var serverDir = app == "server" ? Path.Combine(root, app) : Path.Combine(root, app, "server");
        Require(File.Exists(Path.Combine(serverDir, "vibe-re-server" + suffix)), $"Missing bundled server for {app}");
        Require(File.Exists(Path.Combine(serverDir, "vibe-re-server.runtimeconfig.json")), "Missing runtime configuration");
        var database = Path.Combine(temp, app + ".db");
        // File-only configuration proves the server uses its own content root.
        var settings = JsonSerializer.Serialize(new
        {
            Server = new { AccessToken = "package-smoke-token" },
            ConnectionStrings = new { VibeRe = "Data Source=" + database }
        });
        File.WriteAllText(Path.Combine(serverDir, "appsettings.json"), settings);
        if (app == "server")
        {
            var external = Path.Combine(temp, "external settings");
            Directory.CreateDirectory(external);
            File.WriteAllText(Path.Combine(external, "appsettings.json"), settings);
            environment["ASPNETCORE_CONTENTROOT"] = external;
        }
        var address = FreeAddress();
        await Run(cli, ["servers", "update", "local", address], 0, address);
        Process? foreground = null;
        Process? desktop = null;
        try
        {
            if (app == "server")
            {
                // Environment settings override the shipped JSON.
                environment["Server__AccessToken"] = "environment-smoke-token";
                environment["VIBE_RE_SERVER_TOKEN"] = "environment-smoke-token";
                foreground = Start(Path.Combine(serverDir, "vibe-re-server" + suffix), ["--urls", address]);
                Drain(foreground);
            }
            else if (app == "desktop")
            {
                desktop = Start(Path.Combine(root, app, "VibeRe.Desktop" + suffix), []);
                Drain(desktop);
            }
            else
                await Run(cli, ["auth", "status"], 1, "Mistral: SignedOut");

            await Ready(address);
            await WebAssets(address, serverDir);
            Require(File.Exists(database), $"Server did not load {app}'s appsettings.json");
            // A real SignalR hub invocation succeeds using the configured client token.
            await Run(cli, ["auth", "status"], 1, "Mistral: SignedOut");
            environment["VIBE_RE_SERVER_TOKEN"] = "incorrect-token";
            await Run(cli, ["auth", "status"], 1, "access token for this server");
            environment["VIBE_RE_SERVER_TOKEN"] = app == "server" ? "environment-smoke-token" : "package-smoke-token";
            if (desktop is not null)
            {
                await Task.Delay(1500);
                Require(!desktop.HasExited, "Desktop exited during startup");
            }
            if (app == "server")
            {
                var blocked = false;
                try { await using var lease = await new ManagedServerShutdownGuard(serverDir).AcquireUpdateLeaseAsync(); }
                catch (InvalidOperationException) { blocked = true; }
                Require(blocked, "Updater must block for a manually started server rather than replace its running files");
                await Ready(address);
            }
            else
            {
                // Real installed apphosts must support safe quiesce; a client exiting alone does not stop its server.
                if (desktop is not null)
                {
                    desktop.Kill(entireProcessTree: false);
                    // WaitForExitAsync also waits for redirected stream EOF. On Windows the
                    // still-running child server can inherit those handles; wait only for the client.
                    Require(desktop.WaitForExit(10000), "Desktop did not exit before update shutdown test");
                    desktop.Dispose();
                    desktop = null;
                }
                await Ready(address);
                await using (var lease = await new ManagedServerShutdownGuard(Path.Combine(root, app)).AcquireUpdateLeaseAsync())
                {
                    using var stopped = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
                    var stillReady = false;
                    try { using var response = await stopped.GetAsync(address + "/.well-known/vibe-re/server"); stillReady = response.IsSuccessStatusCode; }
                    catch (HttpRequestException) { }
                    catch (TaskCanceledException) { }
                    Require(!stillReady, "Managed server remained live during update reservation");
                    if (app == "cli") await Run(cli, ["--help"], 1, "update");
                    // Exercise the production server entrypoint, not just the lifecycle helper:
                    // a direct launch has no managed registration and must still obey its client's gate.
                    var blockedDatabase = Path.Combine(temp, app + "-blocked-launch.db");
                    environment["ConnectionStrings__VibeRe"] = "Data Source=" + blockedDatabase;
                    try
                    {
                        await Run(Path.Combine(serverDir, "vibe-re-server" + suffix), ["--urls", address],
                            null, "another update is in progress");
                        Require(!File.Exists(blockedDatabase), $"Direct {app}/server launch reached database startup during replacement");
                    }
                    finally { environment["ConnectionStrings__VibeRe"] = null; }
                }
                // Disposing an un-applied update lease releases startup and preserves the same settings/data.
                if (app == "desktop")
                {
                    desktop = Start(Path.Combine(root, app, "VibeRe.Desktop" + suffix), []);
                    Drain(desktop);
                    await Ready(address);
                }
                await Run(cli, ["auth", "status"], 1, "Mistral: SignedOut");
            }
            Console.WriteLine($"PASS: {app} published startup, SignalR readiness v2, web assets, configuration and token access");
            Console.WriteLine($"PASS: {app} update ownership, graceful managed shutdown and restart safety");
        }
        finally
        {
            Stop(desktop);
            Stop(foreground);
            StopBundledServers();
            environment["Server__AccessToken"] = null;
            environment["ASPNETCORE_CONTENTROOT"] = null;
            environment["VIBE_RE_SERVER_TOKEN"] = "package-smoke-token";
        }
    }
}
catch
{
    var log = Path.Combine(temp, "server.log");
    if (File.Exists(log)) Console.Error.WriteLine(await File.ReadAllTextAsync(log));
    throw;
}
finally
{
    StopBundledServers();
    try { Directory.Delete(temp, true); }
    catch (IOException) { Console.WriteLine($"Smoke output retained: {temp}"); }
}

Process Start(string file, string[] arguments)
{
    var start = new ProcessStartInfo(file) { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardInput = true,
        RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    foreach (var item in environment)
        if (item.Value is null) start.Environment.Remove(item.Key); else start.Environment[item.Key] = item.Value;
    ManagedServerLaunch.ClearInheritedRegistration(start);
    var process = Process.Start(start) ?? throw new Exception("Cannot launch " + file);
    process.StandardInput.Close();
    return process;
}

// A null expected exit code means any nonzero failure (unhandled .NET startup failures differ by OS).
async Task Run(string file, string[] arguments, int? exitCode, string expected)
{
    using var process = Start(file, arguments);
    // A detached server can retain inherited CLI pipe handles on Windows.
    // Stop reading when the CLI exits instead of waiting for the server's EOF.
    using var reading = new CancellationTokenSource();
    var lines = new ConcurrentQueue<string>();
    async Task Collect(StreamReader reader)
    {
        try { while (await reader.ReadLineAsync(reading.Token) is { } line) lines.Enqueue(line); }
        catch (OperationCanceledException) { }
    }
    var output = Collect(process.StandardOutput);
    var error = Collect(process.StandardError);
    try { await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token); }
    catch { Stop(process); throw; }
    await Task.Delay(100);
    await reading.CancelAsync();
    await Task.WhenAll(output, error);
    var message = string.Join(Environment.NewLine, lines);
    var expectedExit = exitCode is int code ? process.ExitCode == code : process.ExitCode != 0;
    Require(expectedExit && message.Contains(expected, StringComparison.OrdinalIgnoreCase),
        $"{Path.GetFileName(file)} {string.Join(' ', arguments)}: exit {process.ExitCode}, expected {exitCode?.ToString() ?? "nonzero"}/{expected}: {message}");
}

async Task Ready(string address)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
    for (var attempt = 0; attempt < 100; attempt++)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address + "/.well-known/vibe-re/server")
                { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                using var identity = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var value = identity.RootElement;
                if (value.GetProperty("application").GetString() == "vibe-re" &&
                    value.GetProperty("protocolVersion").GetInt32() == 2 &&
                    value.GetProperty("transport").GetString() == "signalr") return;
            }
        }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
        await Task.Delay(100);
    }
    throw new Exception("Published server did not become ready: " + address);
}

async Task WebAssets(string address, string serverDir)
{
    using var http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };
    var home = await http.GetStringAsync("/");
    Require(home.Contains("blazor.webassembly.js"), "Published web entry point missing");
    var framework = Path.Combine(serverDir, "wwwroot", "_framework");
    Require(Directory.Exists(framework), "Bundled Blazor framework directory missing");
    foreach (var path in new[] { "/_framework/blazor.webassembly.js", "/app.css" })
    {
        using var response = await http.GetAsync(path);
        Require(response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType != "text/html",
            "Web asset missing or incorrectly served SPA fallback: " + path);
        Require((await response.Content.ReadAsByteArrayAsync()).Length > 0, "Empty web asset: " + path);
    }
    var wasm = Directory.GetFiles(framework, "*.wasm");
    Require(wasm.Length > 0, "No compiled WebAssembly assets in package");
    using var assembly = await http.GetAsync("/_framework/" + Uri.EscapeDataString(Path.GetFileName(wasm[0])));
    Require(assembly.IsSuccessStatusCode && assembly.Content.Headers.ContentType?.MediaType == "application/wasm",
        "Published WebAssembly file unavailable");
    using var unknown = await http.GetAsync("/api/package-smoke-missing");
    Require(unknown.StatusCode == HttpStatusCode.NotFound, "Unknown API route returned SPA fallback");
}

void StopBundledServers()
{
    foreach (var process in Process.GetProcessesByName("vibe-re-server"))
    {
        using (process)
        {
            try
            {
                if (process.MainModule?.FileName is { } file && Path.GetFullPath(file).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    Stop(process);
            }
            catch (System.ComponentModel.Win32Exception) { }
            catch (InvalidOperationException) { }
        }
    }
}

static void Stop(Process? process)
{
    if (process is null) return;
    try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(10000); } }
    catch (InvalidOperationException) { }
}

static void Drain(Process process)
{
    process.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine(e.Data); };
    process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine(e.Data); };
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
}

static string FreeAddress()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    return "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
}

static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
static void CopyDirectory(string source, string destination)
{
    Directory.CreateDirectory(destination);
    foreach (var file in Directory.GetFiles(source))
    {
        var target = Path.Combine(destination, Path.GetFileName(file));
        File.Copy(file, target);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(file));
    }
    foreach (var child in Directory.GetDirectories(source)) CopyDirectory(child, Path.Combine(destination, Path.GetFileName(child)));
}
