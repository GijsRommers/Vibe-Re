using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using VibeRe.Server.Application.Providers.Auth;
using VibeRe.Domain.Provider;

namespace VibeRe.Server.Infrastructure.Providers.Mistral.Auth;

// Mistral Vibe's browser sign-in protocol. No loopback callback is needed on the client or server.
public sealed class MistralBrowserAuthFlow(HttpClient client) : IBrowserAuthFlow
{
    private static readonly Uri ConsoleOrigin = new("https://console.mistral.ai/");
    public string ProviderName => ProviderIdentifiers.Mistral;

    public async Task<BrowserProcess> StartAsync(string challenge, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(new Uri(ConsoleOrigin, "api/vibe/sign-in"),
            new { code_challenge = challenge, code_challenge_method = "S256" }, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = await ReadAsync(response, cancellationToken);
        var root = json.RootElement;
        var id = root.GetProperty("process_id").GetString()!;
        if (string.IsNullOrWhiteSpace(id) || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
        {
            throw new InvalidDataException("Invalid Mistral sign-in process.");
        }
        return new(id, ValidateUri(root.GetProperty("sign_in_url").GetString()!, "/"),
            ValidateUri(root.GetProperty("poll_url").GetString()!, "/api/"),
            root.GetProperty("expires_at").GetDateTimeOffset());
    }

    public async Task<BrowserPoll> PollAsync(Uri pollUri, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(ValidateUri(pollUri.AbsoluteUri, "/api/"), cancellationToken);
        if (response.StatusCode == HttpStatusCode.Gone)
        {
            return new(BrowserPollState.Expired);
        }
        response.EnsureSuccessStatusCode();
        using var json = await ReadAsync(response, cancellationToken);
        var root = json.RootElement;
        return new(MapPollState(root.GetProperty("status").GetString()),
            root.TryGetProperty("exchange_token", out var token) ? token.GetString() : null);
    }

    public async Task<string> ExchangeAsync(string processId, string exchangeToken, string verifier, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri(ConsoleOrigin, $"api/vibe/sign-in/{Uri.EscapeDataString(processId)}/exchange"),
            new { exchange_token = exchangeToken, code_verifier = verifier }, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = await ReadAsync(response, cancellationToken);
        var key = json.RootElement.GetProperty("api_key").GetString();
        return !string.IsNullOrWhiteSpace(key) ? key : throw new InvalidDataException("Mistral did not return an API key.");
    }

    internal static Uri ValidateUri(string value, string pathPrefix)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host != ConsoleOrigin.Host || uri.Port != 443 || uri.UserInfo.Length != 0 ||
            !uri.AbsolutePath.StartsWith(pathPrefix, StringComparison.Ordinal) ||
            Uri.UnescapeDataString(uri.AbsolutePath).Split('/').Any(x => x is "." or ".."))
        {
            throw new InvalidDataException("Untrusted Mistral browser login URL.");
        }

        return uri;
    }

    private static BrowserPollState MapPollState(string? state) => state switch
    {
        "pending" => BrowserPollState.Pending,
        "completed" => BrowserPollState.Completed,
        "expired" => BrowserPollState.Expired,
        "denied" => BrowserPollState.Denied,
        _ => BrowserPollState.Failed
    };

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }
}
