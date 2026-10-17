using VibeRe.Client;
using VibeRe.Client.Configuration;
using VibeRe.Protocol;

namespace VibeRe.Cli;

public sealed class AuthCommands(IServerConnector connector, IBrowserLauncher browser)
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

        using var client = await connector.ConnectAsync(options.Server,
            Environment.GetEnvironmentVariable("VIBE_RE_SERVER_TOKEN"), cancellationToken);
        Console.WriteLine($"Server: {client.Server.Name} ({client.Server.Address})");
        if (verb == "logout") { PrintStatus(await client.LogoutAsync(cancellationToken)); return 0; }
        if (verb == "login") return await LoginAsync(client,
            options.ApiKeyFromStdin ? "api-key" : options.Method ?? "browser", options.ApiKeyFromStdin, cancellationToken);

        var status = await client.GetStatusAsync(cancellationToken);
        PrintStatus(status);
        if (!VibeReClient.NeedsLogin(status)) return status.State == AuthState.Authenticated ? 0 : 1;

        Console.WriteLine("Log in to Mistral to get started.");
        if (options.IsLaunch) return await LoginAsync(client, "browser", false, cancellationToken);
        Console.WriteLine($"Run: vibe-re --server {client.Server.Name} auth login");
        return 1;
    }

    private async Task<int> LoginAsync(IVibeReClient client, string method, bool stdin, CancellationToken ct)
    {
        if (method == "api-key")
        {
            var key = stdin ? await Console.In.ReadLineAsync(ct) ?? "" :
                Environment.GetEnvironmentVariable("MISTRAL_API_KEY") ?? ConsoleInput.ReadSecret("Mistral API key: ", ct);
            var status = await client.LoginAsync(key, ct);
            PrintStatus(status);
            return status.State == AuthState.Authenticated ? 0 : 1;
        }

        var result = await client.LoginWithBrowserAsync(async attempt =>
        {
            Console.WriteLine($"Open this URL to authorize server '{client.Server.Name}':\n{attempt.SignInUrl}");
            try { await browser.OpenAsync(new Uri(attempt.SignInUrl)); }
            catch { Console.WriteLine("Open the URL on any device. Waiting for login…"); }
        }, ct);
        Console.WriteLine(result.Message);
        return result.State == BrowserLoginState.Completed && result.AuthStatus?.State == AuthState.Authenticated ? 0 : 1;
    }

    private static void PrintStatus(AuthStatusReply status) => Console.WriteLine($"Mistral: {status.State} — {status.Message}");
}
