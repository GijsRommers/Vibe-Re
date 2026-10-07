using System.Diagnostics;
using System.Text;
using Grpc.Core;
using VibeRe.Client;
using VibeRe.Client.Configuration;
using VibeRe.Protocol;

namespace VibeRe.Cli;

public static class CliApplication
{
    public static async Task<int> RunAsync(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            var arguments = args.ToList();
            if (arguments.Contains("--help") || arguments.Contains("-h")) { PrintHelp(); return 0; }
            var serverName = TakeOption(arguments, "--server");
            var method = TakeOption(arguments, "--method");
            var stdin = arguments.Remove("--api-key-stdin");
            var tokenStdin = arguments.Remove("--token-stdin");
            var local = arguments.Remove("--local");
            var connections = ServerConnectionManager.CreateDesktop();
            var ct = cancellation.Token;
            if (arguments.FirstOrDefault() == "servers")
            {
                if (serverName is not null || method is not null || stdin) throw new ArgumentException("Unsupported option for servers.");
                return await ConfigureServersAsync(connections, arguments.Skip(1).ToArray(), tokenStdin, local, ct);
            }
            if (local) throw new ArgumentException("--local is only supported by servers add.");
            if (tokenStdin) throw new ArgumentException("--token-stdin is only supported by servers token.");
            var verb = arguments.Count == 0 ? "status" : arguments.Count == 2 && arguments[0] == "auth"
                ? arguments[1] : throw new ArgumentException("Unknown command. Use --help for usage.");
            if (verb is not ("login" or "status" or "logout")) throw new ArgumentException("Unknown auth command.");
            if (verb != "login" && (method is not null || stdin)) throw new ArgumentException("Login options require auth login.");
            if (method is not (null or "browser" or "api-key")) throw new ArgumentException("Choose --method browser or --method api-key.");
            if (stdin && method == "browser") throw new ArgumentException("--api-key-stdin requires API-key login.");
            using var client = await connections.ConnectAsync(serverName,
                Environment.GetEnvironmentVariable("VIBE_RE_SERVER_TOKEN"), ct);
            Console.WriteLine($"Server: {client.Server.Name} ({client.Server.Address})");
            if (verb == "logout") { PrintStatus(await client.LogoutAsync(ct)); return 0; }
            if (verb == "login") return await LoginAsync(client, stdin ? "api-key" : method ?? "browser", stdin, ct);

            var status = await client.GetStatusAsync(ct);
            PrintStatus(status);
            if (VibeReClient.NeedsLogin(status))
            {
                Console.WriteLine($"Log this server in with: vibe-re --server {client.Server.Name} auth login");
                if (!Console.IsInputRedirected)
                {
                    Console.Write("Log in now? [b]rowser / [k]ey / [s]kip: ");
                    var choice = Console.ReadLine()?.Trim().ToLowerInvariant();
                    if (choice is "b" or "browser" or "k" or "key")
                        return await LoginAsync(client, choice is "k" or "key" ? "api-key" : "browser", false, ct);
                }
            }
            return status.State == AuthState.Authenticated ? 0 : 1;
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Canceled."); return 130; }
        catch (RpcException e)
        {
            Console.Error.WriteLine(e.StatusCode == StatusCode.Unavailable
                ? "Could not connect to the server. Check the configured address."
                : e.Status.Detail);
            return 1;
        }
        catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 2; }
        catch (LocalServerStartupException e) { Console.Error.WriteLine(e.Message); return 1; }
        catch { Console.Error.WriteLine("Could not access saved server settings or credential storage."); return 1; }
        finally { Console.CancelKeyPress -= onCancel; }
    }

    private static async Task<int> ConfigureServersAsync(ServerConnectionManager connections, string[] args,
        bool tokenStdin, bool local, CancellationToken ct)
    {
        var action = args.FirstOrDefault() ?? "list";
        if (tokenStdin && action != "token") throw new ArgumentException("--token-stdin requires servers token.");
        if (local && action != "add") throw new ArgumentException("--local requires servers add.");
        ClientConfiguration config;
        switch (action)
        {
            case "list" when args.Length <= 1: config = await connections.LoadAsync(ct); break;
            case "add" when args.Length == 3: config = await connections.AddAsync(args[1], args[2], ct, local); break;
            case "update" when args.Length == 3: config = await connections.UpdateAddressAsync(args[1], args[2], ct); break;
            case "use" when args.Length == 2: config = await connections.SelectAsync(args[1], ct); break;
            case "remove" when args.Length == 2: config = await connections.RemoveAsync(args[1], ct); break;
            case "local" when args.Length == 3 && args[2] is "on" or "off":
                config = await connections.SetLocalStartupAsync(args[1], args[2] == "on", ct); break;
            case "token" when args.Length == 2:
                var token = tokenStdin ? await Console.In.ReadLineAsync(ct) ?? "" : ReadSecret("Server access token (empty to clear): ", ct);
                config = await connections.SetTokenAsync(args[1], token, ct);
                break;
            default: throw new ArgumentException("Use servers list/add/update/use/remove/local/token. See --help.");
        }
        foreach (var server in config.Servers)
            Console.WriteLine($"{(server.Name == config.ActiveServer ? "*" : " ")} {server.Name}\t{server.Address}\t{(server.HasAccessToken ? "token saved" : "no token")}\t{(server.StartLocally ? "auto-start local" : "connect only")}");
        if (config.Servers.Count == 0) Console.WriteLine("No servers configured. Use servers add NAME URL.");
        return 0;
    }

    private static async Task<int> LoginAsync(VibeReClient client, string method, bool stdin, CancellationToken ct)
    {
        if (method == "api-key")
        {
            var key = stdin ? await Console.In.ReadLineAsync(ct) ?? "" :
                Environment.GetEnvironmentVariable("MISTRAL_API_KEY") ?? ReadSecret("Mistral API key: ", ct);
            var status = await client.LoginAsync(key, ct);
            PrintStatus(status);
            return status.State == AuthState.Authenticated ? 0 : 1;
        }

        var result = await client.LoginWithBrowserAsync(attempt =>
        {
            Console.WriteLine($"Open this URL to authorize server '{client.Server.Name}':\n{attempt.SignInUrl}");
            try { Process.Start(new ProcessStartInfo(attempt.SignInUrl) { UseShellExecute = true }); }
            catch { Console.WriteLine("Open the URL on any device. Waiting for login…"); }
            return Task.CompletedTask;
        }, ct);
        Console.WriteLine(result.Message);
        return result.State == BrowserLoginState.Completed && result.AuthStatus?.State == AuthState.Authenticated ? 0 : 1;
    }

    private static string ReadSecret(string prompt, CancellationToken ct)
    {
        if (Console.IsInputRedirected) throw new ArgumentException("Use the stdin option for non-interactive secret input.");
        Console.Write(prompt);
        var value = new StringBuilder();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return value.ToString(); }
            if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; }
            else if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
        }
    }

    private static string? TakeOption(List<string> args, string option)
    {
        var index = args.IndexOf(option);
        if (index < 0) return null;
        if (index + 1 == args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"{option} requires a value.");
        var value = args[index + 1];
        args.RemoveRange(index, 2);
        return value;
    }

    private static void PrintStatus(AuthStatusReply status) => Console.WriteLine($"Mistral: {status.State} — {status.Message}");
    private static void PrintHelp() => Console.WriteLine("""
        vibe-re [--server NAME]                 Show status and offer login if needed
        vibe-re [--server NAME] auth status
        vibe-re [--server NAME] auth login [--method browser|api-key] [--api-key-stdin]
        vibe-re [--server NAME] auth logout
        vibe-re servers list
        vibe-re servers add NAME URL [--local]
        vibe-re servers update NAME URL
        vibe-re servers use NAME
        vibe-re servers remove NAME
        vibe-re servers local NAME on|off
        vibe-re servers token NAME [--token-stdin]

        Browser login is the default. API-key login reads MISTRAL_API_KEY or prompts without echo.
        The default local server starts automatically when needed. Remote servers never start a local process.
        Server settings are shared with desktop. Tokens are saved in the OS credential store.
        VIBE_RE_CLIENT_DATABASE overrides the client database path; VIBE_RE_SERVER_TOKEN overrides the selected server token.
        """);
}
