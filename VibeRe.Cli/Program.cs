using VibeRe.Cli;
using VibeRe.Core.Auth;
using VibeRe.Infrastructure.Auth.Mistral;
using VibeRe.Infrastructure.Credentials;

const string usage = """
    Usage:
      vibe-re auth login  [--provider <id>] [--method <id>] [--no-browser]
      vibe-re auth status [--provider <id>]
      vibe-re auth logout [--provider <id>]

    Options:
      --provider <id>  Provider to authenticate with (default: mistral)
      --method <id>    Sign-in method, e.g. browser or api-key (default: the provider's preferred method)
      --no-browser     Print the sign-in URL instead of opening a browser
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

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("VibeRe/0.1");
var auth = new AuthService([new MistralAuthProvider(http)], CredentialStores.CreateDefault());

try
{
    var provider = auth.GetProvider(options.Provider);
    switch (command)
    {
        case "login":
            await auth.SignInAsync(provider.Id, options.Method, new ConsoleAuthInteraction(options.OpenBrowser), cts.Token);
            Console.WriteLine($"Signed in to {provider.DisplayName}.");
            if ((await auth.GetStateAsync(provider.Id, cts.Token)).ActiveSource == CredentialSource.Environment)
            {
                Console.WriteLine($"Note: ${provider.ApiKeyEnvironmentVariable} is set and takes precedence over the stored key.");
            }

            return 0;

        case "status":
            return await PrintStatusAsync(auth, provider, cts.Token);

        case "logout":
            var removed = await auth.SignOutAsync(provider.Id, cts.Token);
            Console.WriteLine(removed ? $"Signed out of {provider.DisplayName}." : $"No stored {provider.DisplayName} credential.");
            if ((await auth.GetStateAsync(provider.Id, cts.Token)).ActiveSource == CredentialSource.Environment)
            {
                Console.WriteLine($"Note: ${provider.ApiKeyEnvironmentVariable} is still set; unset it to fully sign out.");
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
catch (Exception ex) when (ex is AuthException or ArgumentException)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

static async Task<int> PrintStatusAsync(AuthService auth, IAuthProvider provider, CancellationToken cancellationToken)
{
    var state = await auth.GetStateAsync(provider.Id, cancellationToken);
    var credential = await auth.ResolveCredentialAsync(provider.Id, cancellationToken);
    if (credential is null)
    {
        Console.WriteLine($"{provider.DisplayName}: signed out. Run `vibe-re auth login --provider {provider.Id}`.");
        return 1;
    }

    var source = state.ActiveSource == CredentialSource.Environment
        ? $"${state.EnvironmentVariable}"
        : "the stored key";
    try
    {
        var account = await provider.GetAccountAsync(credential, cancellationToken);
        Console.WriteLine(account is null
            ? $"{provider.DisplayName}: signed in using {source} (account details unavailable)."
            : $"{provider.DisplayName}: signed in using {source}, plan: {account.Plan}.");
        return 0;
    }
    catch (AuthException ex) when (ex.Code == AuthErrorCode.Unauthorized)
    {
        Console.WriteLine($"{provider.DisplayName}: {source} was rejected. Run `vibe-re auth login --provider {provider.Id}`.");
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
    string Provider = MistralAuthProvider.ProviderId,
    string? Method = null,
    bool OpenBrowser = true);
