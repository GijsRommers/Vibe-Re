using VibeRe.Cli;
using VibeRe.Client;

const string usage = """
    Usage:
      vibe-re auth login  [--provider <id>] [--method <id>] [--no-browser]
      vibe-re auth status [--provider <id>]
      vibe-re auth logout [--provider <id>]

    Options:
      --provider <id>  Provider to authenticate with (default: mistral)
      --method <id>    Sign-in method, e.g. browser or api-key (default: the provider's preferred method)
      --no-browser     Print the sign-in URL instead of opening a browser

    Talks to the VibeRe server at $VIBE_RE_SERVER (default: http://localhost:5067).
    """;

if (args is not ["auth", var command, .. var rest] || !TryParseOptions(rest, out var options))
{
    Console.Error.WriteLine(usage);
    return 2;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

using var client = new VibeReClient();

try
{
    switch (command)
    {
        case "login":
            var signIn = await client.Auth.SignInAsync(
                options.Provider, options.Method, new ConsoleAuthInteraction(options.OpenBrowser), cts.Token);
            Console.WriteLine($"Signed in to {signIn.Provider.DisplayName}.");
            if (signIn.ActiveSource == CredentialSource.Environment)
            {
                Console.WriteLine($"Note: ${signIn.Provider.ApiKeyEnvironmentVariable} is set on the server and takes precedence over the stored key.");
            }

            return 0;

        case "status":
            return await PrintStatusAsync(client, options.Provider, cts.Token);

        case "logout":
            var signOut = await client.Auth.SignOutAsync(options.Provider, cts.Token);
            var provider = signOut.Provider;
            Console.WriteLine(signOut.Removed ? $"Signed out of {provider.DisplayName}." : $"No stored {provider.DisplayName} credential.");
            if (signOut.ActiveSource == CredentialSource.Environment)
            {
                Console.WriteLine($"Note: ${provider.ApiKeyEnvironmentVariable} is still set on the server; unset it to fully sign out.");
            }

            return 0;

        default:
            Console.Error.WriteLine(usage);
            return 2;
    }
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (VibeReClientException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

static async Task<int> PrintStatusAsync(VibeReClient client, string providerId, CancellationToken cancellationToken)
{
    var status = await client.Auth.GetStatusAsync(providerId, cancellationToken);
    var provider = status.Provider;
    var source = status.ActiveSource == CredentialSource.Environment
        ? $"${provider.ApiKeyEnvironmentVariable}"
        : "the stored key";
    switch (status.AccountStatus)
    {
        case AccountStatus.Active:
            Console.WriteLine(status.Plan is not null
                ? $"{provider.DisplayName}: signed in using {source}, plan: {status.Plan}."
                : $"{provider.DisplayName}: signed in using {source} (account details unavailable).");
            return 0;
        case AccountStatus.Rejected:
            Console.WriteLine($"{provider.DisplayName}: {source} was rejected. Run `vibe-re auth login --provider {provider.Id}`.");
            return 1;
        case AccountStatus.SubscriptionRequired:
            Console.WriteLine($"{provider.DisplayName}: {source} is recognized, but {status.Message}");
            return 1;
        default:
            Console.WriteLine($"{provider.DisplayName}: signed out. Run `vibe-re auth login --provider {provider.Id}`.");
            return 1;
    }
}

static bool TryParseOptions(string[] args, out CliOptions options)
{
    options = new CliOptions();
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--provider" when i + 1 < args.Length:
                options = options with { Provider = args[++i] };
                break;
            case "--method" when i + 1 < args.Length:
                options = options with { Method = args[++i] };
                break;
            case "--no-browser":
                options = options with { OpenBrowser = false };
                break;
            default:
                return false;
        }
    }

    return true;
}

internal sealed record CliOptions(
    string Provider = "mistral",
    string? Method = null,
    bool OpenBrowser = true);
