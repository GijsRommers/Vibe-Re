using VibeRe.Cli;
using VibeRe.Client;

// Mistral is the only provider for now.
const string providerId = "mistral";

if (args.Length > 0)
{
    Console.Error.WriteLine("Usage: vibe-re");
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
    await client.EnsureServerRunningAsync(cts.Token);

    var status = await client.Auth.GetStatusAsync(providerId, cts.Token);
    // Signing in stores a key, which can't help while the server's environment variable takes precedence.
    if (status.AccountStatus is AccountStatus.SignedOut or AccountStatus.Rejected
        && status.ActiveSource != CredentialSource.Environment)
    {
        if (status.AccountStatus == AccountStatus.Rejected)
        {
            Console.WriteLine($"{status.Provider.DisplayName} rejected your saved key. Let's sign in again.");
        }

        await client.Auth.SignInAsync(providerId, method: null, new ConsoleAuthInteraction(), cts.Token);
        status = await client.Auth.GetStatusAsync(providerId, cts.Token);
    }

    var provider = status.Provider;
    var source = status.ActiveSource == CredentialSource.Environment
        ? $"${provider.ApiKeyEnvironmentVariable}"
        : "your key";
    switch (status.AccountStatus)
    {
        case AccountStatus.Active:
            Console.WriteLine(status.Plan is null
                ? $"Signed in to {provider.DisplayName}."
                : $"Signed in to {provider.DisplayName} ({status.Plan}).");
            return 0;
        case AccountStatus.SubscriptionRequired:
            Console.Error.WriteLine($"{provider.DisplayName} recognized {source}, but {status.Message}");
            return 1;
        case AccountStatus.Rejected:
            Console.Error.WriteLine(status.ActiveSource == CredentialSource.Environment
                ? $"{provider.DisplayName} rejected {source}. Fix or unset it, then run `make stop` so the server picks up the change."
                : $"{provider.DisplayName} rejected {source}.");
            return 1;
        default:
            Console.Error.WriteLine($"Not signed in to {provider.DisplayName}.");
            return 1;
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
