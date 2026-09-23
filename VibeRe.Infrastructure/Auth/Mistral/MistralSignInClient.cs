using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using VibeRe.Core.Auth;

namespace VibeRe.Infrastructure.Auth.Mistral;

internal sealed record SignInProcess(string ProcessId, Uri SignInUrl, Uri PollUrl, DateTimeOffset ExpiresAt);

internal sealed record PollResult(string Status, string? ExchangeToken, string? Message);

/// <summary>HTTP client for the console's browser sign-in endpoints (the ones Mistral Vibe uses).</summary>
internal sealed class MistralSignInClient(HttpClient http, MistralAuthOptions options)
{
    private static readonly HashSet<string> KnownStatuses = ["pending", "completed", "expired", "denied", "error"];

    private readonly Uri _consoleBase = new(options.ConsoleBaseUrl.TrimEnd('/'));
    private readonly Uri _apiBase = new(options.ConsoleApiBaseUrl.TrimEnd('/'));

    public async Task<SignInProcess> CreateProcessAsync(string codeChallenge, CancellationToken cancellationToken)
    {
        const string message = "Failed to start browser sign-in.";
        const AuthErrorCode code = AuthErrorCode.StartFailed;

        var payload = await SendAsync<CreateProcessResponse>(
            () => http.PostAsJsonAsync(
                $"{_apiBase}/vibe/sign-in",
                new CreateProcessRequest(codeChallenge, Pkce.ChallengeMethod),
                cancellationToken),
            message, code, cancellationToken);

        if (payload is not { ProcessId: { Length: > 0 } processId, SignInUrl: { } signInUrl, PollUrl: { } pollUrl }
            || !DateTimeOffset.TryParse(payload.ExpiresAt, out var expiresAt))
        {
            throw new AuthException(message, code);
        }

        return new SignInProcess(
            processId,
            ValidateUnderBase(signInUrl, _consoleBase, message, code),
            ValidateUnderBase(pollUrl, _apiBase, message, code),
            expiresAt);
    }

    public async Task<PollResult> PollAsync(Uri pollUrl, CancellationToken cancellationToken)
    {
        const string message = "Browser sign-in status could not be retrieved.";
        const AuthErrorCode code = AuthErrorCode.PollFailed;

        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(pollUrl, cancellationToken);
        }
        catch (Exception ex) when (IsTransportFailure(ex, cancellationToken))
        {
            throw new AuthException(message, code, ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Gone)
            {
                return new PollResult("expired", null, null);
            }

            var payload = await ReadJsonAsync<PollResponse>(response, message, code, cancellationToken);
            if (payload?.Status is not { } status || !KnownStatuses.Contains(status))
            {
                throw new AuthException("Browser sign-in returned an unknown state.", AuthErrorCode.UnknownState);
            }

            return new PollResult(status, payload.ExchangeToken, payload.Message);
        }
    }

    public async Task<string> ExchangeAsync(
        string processId,
        string exchangeToken,
        string codeVerifier,
        CancellationToken cancellationToken)
    {
        const string message = "Failed to exchange browser sign-in for an API key.";

        var payload = await SendAsync<ExchangeResponse>(
            () => http.PostAsJsonAsync(
                $"{_apiBase}/vibe/sign-in/{Uri.EscapeDataString(processId)}/exchange",
                new ExchangeRequest(exchangeToken, codeVerifier),
                cancellationToken),
            message, AuthErrorCode.ExchangeFailed, cancellationToken);

        return payload?.ApiKey is { Length: > 0 } apiKey
            ? apiKey
            : throw new AuthException("Browser sign-in exchange did not return an API key.", AuthErrorCode.MissingSecret);
    }

    /// <summary>
    /// Only follow URLs the server returns if they stay under the configured base, so a compromised or
    /// misconfigured response can't send the user (or our polling) somewhere else.
    /// </summary>
    private Uri ValidateUnderBase(string value, Uri baseUrl, string message, AuthErrorCode code)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            throw new AuthException(message, code);
        }

        var originMatches = string.Equals(url.Scheme, baseUrl.Scheme, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(url.IdnHost, baseUrl.IdnHost, StringComparison.OrdinalIgnoreCase)
                            && url.Port == baseUrl.Port;
        if ((!originMatches && !options.AllowOriginRewrite) || !IsPathUnderBase(url, baseUrl))
        {
            throw new AuthException(message, code);
        }

        return originMatches
            ? url
            : new UriBuilder(url) { Scheme = baseUrl.Scheme, Host = baseUrl.Host, Port = baseUrl.Port }.Uri;
    }

    private static bool IsPathUnderBase(Uri url, Uri baseUrl)
    {
        var path = Uri.UnescapeDataString(url.AbsolutePath);
        if (path.Split('/').Contains(".."))
        {
            return false;
        }

        var basePath = baseUrl.AbsolutePath.TrimEnd('/');
        return basePath.Length == 0 || path == basePath || path.StartsWith($"{basePath}/", StringComparison.Ordinal);
    }

    private static async Task<T?> SendAsync<T>(
        Func<Task<HttpResponseMessage>> send,
        string message,
        AuthErrorCode code,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await send();
        }
        catch (Exception ex) when (IsTransportFailure(ex, cancellationToken))
        {
            throw new AuthException(message, code, ex);
        }

        using (response)
        {
            return await ReadJsonAsync<T>(response, message, code, cancellationToken);
        }
    }

    private static async Task<T?> ReadJsonAsync<T>(
        HttpResponseMessage response,
        string message,
        AuthErrorCode code,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new AuthException($"{message} (HTTP {(int)response.StatusCode})", code);
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException || IsTransportFailure(ex, cancellationToken))
        {
            throw new AuthException(message, code, ex);
        }
    }

    // HttpClient reports its own timeout as a TaskCanceledException; only the caller's token means "cancelled".
    private static bool IsTransportFailure(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested);

    private sealed record CreateProcessRequest(
        [property: JsonPropertyName("code_challenge")] string CodeChallenge,
        [property: JsonPropertyName("code_challenge_method")] string CodeChallengeMethod);

    private sealed record CreateProcessResponse(
        [property: JsonPropertyName("process_id")] string? ProcessId,
        [property: JsonPropertyName("sign_in_url")] string? SignInUrl,
        [property: JsonPropertyName("poll_url")] string? PollUrl,
        [property: JsonPropertyName("expires_at")] string? ExpiresAt);

    private sealed record PollResponse(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("exchange_token")] string? ExchangeToken,
        [property: JsonPropertyName("message")] string? Message);

    private sealed record ExchangeRequest(
        [property: JsonPropertyName("exchange_token")] string ExchangeToken,
        [property: JsonPropertyName("code_verifier")] string CodeVerifier);

    private sealed record ExchangeResponse([property: JsonPropertyName("api_key")] string? ApiKey);
}
