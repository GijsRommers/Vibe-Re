using VibeRe.Client;
using VibeRe.Runtime;
using VibeRe.Runtime.Configuration;
using VibeRe.Protocol;

namespace VibeRe.Cli;

public sealed class AuthCommands(IServerConnector connector, ClientStartup startup, IBrowserLauncher browser)
{
    public async Task<int> ExecuteAsync(CliOptions options, CancellationToken cancellationToken)
    {
        if (options.StartLocally) throw new ArgumentException("--local is only supported by servers add.");
        if (options.TokenFromStdin) throw new ArgumentException("--token-stdin is only supported by servers token.");
        var verb = options.Arguments switch
        {
            [] => "status",
            ["auth", "login" or "status" or "logout"] => options.Arguments[1],
            _ => throw new ArgumentException("Unknown command. Use --help for usage.")
        };
        if (verb != "login" && (options.Method is not null || options.ApiKeyFromStdin))
            throw new ArgumentException("Login options require auth login.");
        if (options.Method is not (null or "browser" or "api-key"))
            throw new ArgumentException("Choose --method browser or --method api-key.");
        if (options.ApiKeyFromStdin && options.Method == "browser")
            throw new ArgumentException("--api-key-stdin requires API-key login.");

        var token = Environment.GetEnvironmentVariable("VIBE_RE_SERVER_TOKEN");
        if (options.IsLaunch)
        {
            using var launch = await startup.StartAsync(options.Server, token, cancellationToken);
            if (launch is null) { Console.WriteLine("Server is optional. Use auth login to connect."); return 0; }
            PrintServer(launch);
            PrintStatus(launch.Status!);
            if (VibeReClient.NeedsLogin(launch.Status!)) Console.WriteLine("Not logged in.");
            return VibeReClient.NeedsLogin(launch.Status!)
                ? await LoginAsync(launch, "browser", false, cancellationToken)
                : ExitCode(launch.Status!);
        }

        using var session = await connector.ConnectAsync(options.Server, token, cancellationToken);
        PrintServer(session);
        if (verb == "login") return await LoginAsync(session,
            options.ApiKeyFromStdin ? "api-key" : options.Method ?? "browser", options.ApiKeyFromStdin, cancellationToken);
        var status = verb == "logout" ? await session.Client.LogoutAsync(cancellationToken) : await session.Client.GetStatusAsync(cancellationToken);
        PrintStatus(status);
        if (verb == "logout") return 0;
        if (VibeReClient.NeedsLogin(status)) Console.WriteLine($"Run: vibe-re --server {session.Server.Name} auth login");
        return ExitCode(status);
    }

    private static int ExitCode(AuthStatusReply status) => status.State == AuthState.Authenticated ? 0 : 1;
    private static void PrintServer(ServerSession session) => Console.WriteLine($"Server: {session.Server.Name} ({session.Server.Address})");

    private async Task<int> LoginAsync(ServerSession session, string method, bool stdin, CancellationToken ct)
    {
        if (method == "api-key")
        {
            var key = stdin ? await Console.In.ReadLineAsync(ct) ?? "" :
                Environment.GetEnvironmentVariable("MISTRAL_API_KEY") ?? ConsoleInput.ReadSecret("Mistral API key: ", ct);
            var status = await session.Client.LoginAsync(key, ct);
            PrintStatus(status);
            return ExitCode(status);
        }

        Console.WriteLine("Starting Mistral login…");
        var result = await session.Client.LoginWithBrowserAsync(async attempt =>
        {
            Console.WriteLine($"Open this URL to authorize server '{session.Server.Name}':\n{attempt.SignInUrl}");
            try { await browser.OpenAsync(new Uri(attempt.SignInUrl)); }
            catch { Console.WriteLine("Open the URL on any device. Waiting for login…"); }
        }, ct);
        Console.WriteLine(result.Message);
        return result.State == BrowserLoginState.Completed && result.AuthStatus?.State == AuthState.Authenticated ? 0 : 1;
    }

    private static void PrintStatus(AuthStatusReply status) => Console.WriteLine($"Mistral: {status.State} — {status.Message}");
}
