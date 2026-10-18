using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using VibeRe.Contracts;

namespace VibeRe.Client.Infrastructure.Adapters;

public sealed class DevicePairingClient : IDevicePairingClient
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumPairingTime = TimeSpan.FromMinutes(5);

    public async Task<string> PairAsync(string serverAddress, string deviceName,
        Func<DevicePairingChallenge, Task> showChallenge, CancellationToken cancellationToken = default)
    {
        var origin = new Uri(ServerAddress.Normalize(serverAddress));
        deviceName = deviceName.Trim();
        if (deviceName.Length is < 1 or > 100 || deviceName.Any(char.IsControl))
            throw new ArgumentException("Device name must contain 1 to 100 printable characters.");
        ArgumentNullException.ThrowIfNull(showChallenge);
        cancellationToken.ThrowIfCancellationRequested();

        // Each attempt owns its cookies and connection. Native redemption never receives a
        // browser Origin header, and no redirect can forward its credentials to another origin.
        using var http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer()
        })
        {
            BaseAddress = origin, Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 64 * 1024
        };
        http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoStore = true };
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(MaximumPairingTime);
        try
        {
            using var csrfResponse = await http.GetAsync("/api/access/csrf", attempt.Token);
            RequireOk(csrfResponse);
            var csrf = await ReadAsync(csrfResponse, PairingJsonContext.Default.PairingCsrfReply, attempt.Token);
            if (!IsOpaqueValue(csrf.RequestToken, 4096)) throw InvalidResponse();

            using var beginResponse = await PostAsync(http, "/api/access/pairings", csrf.RequestToken,
                new BeginPairingRequest(deviceName), PairingJsonContext.Default.BeginPairingRequest, attempt.Token);
            RequireOk(beginResponse);
            var pairing = await ReadAsync(beginResponse, PairingJsonContext.Default.PairingReply, attempt.Token);
            if (!IsOpaqueValue(pairing.PairingId, 512) || !IsOpaqueValue(pairing.PairingSecret, 512) ||
                !IsOpaqueValue(pairing.ApprovalCode, 32)) throw InvalidResponse();
            var remaining = pairing.ExpiresAt - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) throw Expired();
            // A server cannot make the client poll indefinitely, even with a malformed expiry.
            remaining = remaining < MaximumPairingTime ? remaining : MaximumPairingTime;
            attempt.CancelAfter(remaining);
            await showChallenge(new(pairing.ApprovalCode, pairing.ExpiresAt)).WaitAsync(attempt.Token);

            while (true)
            {
                attempt.Token.ThrowIfCancellationRequested();
                using var response = await PostAsync(http, "/api/access/pairings/redeem-native", csrf.RequestToken,
                    new RedeemPairingRequest(pairing.PairingId, pairing.PairingSecret),
                    PairingJsonContext.Default.RedeemPairingRequest, attempt.Token);
                if (response.StatusCode == HttpStatusCode.Accepted)
                {
                    await Task.Delay(PollInterval, attempt.Token);
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    // Redemption is still pending when the server's rate limiter rejects it.
                    // Never replay a failed begin or an uncertain successful redemption.
                    var retryAfter = response.Headers.RetryAfter;
                    var delay = retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(5);
                    if (delay <= TimeSpan.Zero) delay = PollInterval;
                    if (delay > TimeSpan.FromSeconds(30)) delay = TimeSpan.FromSeconds(30);
                    await Task.Delay(delay, attempt.Token);
                    continue;
                }
                RequireOk(response);
                var result = await ReadAsync(response, PairingJsonContext.Default.DeviceTokenReply, attempt.Token);
                if (!IsOpaqueValue(result.Token, 4096)) throw InvalidResponse();
                attempt.Token.ThrowIfCancellationRequested();
                return result.Token;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested) { throw Expired(); }
        catch (OperationCanceledException)
        { throw new ServerConnectionException("The server did not respond in time. Pair this device again."); }
        catch (HttpRequestException)
        { throw new ServerConnectionException("Could not pair with this server. Check its address and whether it is running."); }
        catch (JsonException) { throw InvalidResponse(); }
    }

    private static async Task<HttpResponseMessage> PostAsync<T>(HttpClient http, string path, string csrf,
        T body, JsonTypeInfo<T> type, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        request.Content = JsonContent.Create(body, type);
        return await http.SendAsync(request, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> type, CancellationToken ct) =>
        await response.Content.ReadFromJsonAsync(type, ct) ?? throw InvalidResponse();

    private static void RequireOk(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.OK) return;
        throw response.StatusCode switch
        {
            HttpStatusCode.NotFound or HttpStatusCode.Gone =>
                new ServerConnectionException("Pairing is unavailable or expired. Pair this device again on an updated server."),
            HttpStatusCode.TooManyRequests =>
                new ServerConnectionException("Too many pairing attempts. Wait a moment before trying again."),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new ServerConnectionException("The server did not allow pairing. Pair this device again."),
            _ => new ServerConnectionException("The server could not complete pairing. Check its address and try again.")
        };
    }

    private static bool IsOpaqueValue(string? value, int maximumLength) =>
        value is { Length: > 0 } && value.Length <= maximumLength && value.All(c => c is >= '!' and <= '~');
    private static ServerConnectionException InvalidResponse() => new("The server returned an invalid pairing response. Try again.");
    private static ServerConnectionException Expired() => new("Pairing expired. Pair this device again to get a new code.");
}

internal sealed record PairingCsrfReply(string RequestToken);

[JsonSerializable(typeof(PairingCsrfReply))]
[JsonSerializable(typeof(BeginPairingRequest))]
[JsonSerializable(typeof(PairingReply))]
[JsonSerializable(typeof(RedeemPairingRequest))]
[JsonSerializable(typeof(DeviceTokenReply))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
internal partial class PairingJsonContext : JsonSerializerContext;
