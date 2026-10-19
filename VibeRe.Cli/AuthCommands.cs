using VibeRe.Client.Application;
using VibeRe.Client.Application.Configuration;

namespace VibeRe.Cli;

public sealed class AuthCommands(IServerConnector connector, ClientStartup startup, IBrowserLauncher browser)
{
    public async Task<int> ExecuteAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var verb = ValidateOptions(options);
        var token = Environment.GetEnvironmentVariable("VIBE_RE_SERVER_TOKEN");
        return options.IsLaunch
            ? await LaunchAsync(options.Server, token, cancellationToken)
            : await ExecuteCommandAsync(options, verb, token, cancellationToken);
    }

    private static string ValidateOptions(CliOptions options)
    {
        if (options.StartLocally)
            throw new ArgumentException("--local is only supported by servers add.");
        if (options.TokenFromStdin)
            throw new ArgumentException("--token-stdin is only supported by servers token.");
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
        return verb;
    }

    private async Task<int> LaunchAsync(string? server, string? token, CancellationToken ct)
    {
        var result = await startup.StartAsync(server, token, ct);
        if (result is ServerOptional)
        {
            Console.WriteLine("Server is optional. Use auth login to connect.");
            return 0;
        }

        await using var session = (ServerSession)result;
        PrintServer(session.Server);
        PrintStatus(session.Status);
        if (!session.Status.NeedsLogin)
            return ExitCode(session.Status);

        Console.WriteLine("Not logged in.");
        return await LoginBrowserAsync(session.Server, session.Client, ct);
    }

    private async Task<int> ExecuteCommandAsync(CliOptions options, string verb, string? token, CancellationToken ct)
    {
        var connection = await connector.ConnectAsync(options.Server, token, ct);
        await using var client = connection.Client;
        ct.ThrowIfCancellationRequested();
        PrintServer(connection.Server);
        if (verb == "login")
        {
            var method = options.ApiKeyFromStdin ? "api-key" : options.Method ?? "browser";
            return method == "api-key"
                ? await LoginApiKeyAsync(client, options.ApiKeyFromStdin, ct)
                : await LoginBrowserAsync(connection.Server, client, ct);
        }

        var status = verb == "logout"
            ? await client.LogoutAsync(ct)
            : await client.GetStatusAsync(ct);
        ct.ThrowIfCancellationRequested();
        PrintStatus(status);
        if (verb == "logout")
            return 0;
        if (status.NeedsLogin)
            Console.WriteLine($"Run: vibe-re --server {connection.Server.Name} auth login");
        return ExitCode(status);
    }

    private static async Task<int> LoginApiKeyAsync(IVibeReClient client, bool stdin, CancellationToken ct)
    {
        var key = stdin
            ? await Console.In.ReadLineAsync(ct) ?? ""
            : Environment.GetEnvironmentVariable("MISTRAL_API_KEY") ?? ConsoleInput.ReadSecret("Mistral API key: ", ct);
        var status = await client.LoginAsync(key, ct);
        ct.ThrowIfCancellationRequested();
        PrintStatus(status);
        return ExitCode(status);
    }

    private async Task<int> LoginBrowserAsync(ServerConnection server, IVibeReClient client, CancellationToken ct)
    {
        Console.WriteLine("Starting Mistral login…");
        var result = await client.LoginWithBrowserAsync(async attempt =>
        {
            ct.ThrowIfCancellationRequested();
            Console.WriteLine($"Open this URL to authorize server '{server.Name}':\n{attempt.SignInUrl}");
            try
            {
                await browser.OpenAsync(new Uri(attempt.SignInUrl));
            }
            catch when (!ct.IsCancellationRequested)
            {
                Console.WriteLine("Open the URL on any device. Waiting for login…");
            }
        }, ct);
        ct.ThrowIfCancellationRequested();
        Console.WriteLine(result.Message);
        return result.State == BrowserLoginState.Completed && result.AuthStatus?.State == AuthState.Authenticated ? 0 : 1;
    }

    private static int ExitCode(AuthStatus status) => status.State == AuthState.Authenticated ? 0 : 1;

    private static void PrintServer(ServerConnection server) => Console.WriteLine($"Server: {server.Name} ({server.Address})");

    private static void PrintStatus(AuthStatus status) => Console.WriteLine($"Mistral: {status.State} — {status.Message}");
}
