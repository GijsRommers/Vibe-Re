using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using VibeRe.Contracts;

namespace VibeRe.Web.Services;

// No session token is returned to this app. The server owns its HttpOnly device cookie.
// Pairing credentials and antiforgery tokens live only in this scoped, in-memory client.
public sealed class BrowserAccessClient(HttpClient http)
{
    private string? _csrfToken;

    public async Task<AccessStatusReply> GetStatusAsync(CancellationToken cancellationToken) =>
        await GetAsync<AccessStatusReply>("api/access/status", cancellationToken);

    public async Task<PairingReply> BeginPairingAsync(string deviceName, CancellationToken cancellationToken)
    {
        using var response = await SendMutationAsync(HttpMethod.Post, "api/access/pairings",
            new BeginPairingRequest(deviceName), cancellationToken);
        return await ReadAsync<PairingReply>(response, cancellationToken);
    }

    public async Task<bool> RedeemPairingAsync(PairingReply pairing, CancellationToken cancellationToken)
    {
        using var response = await SendMutationAsync(HttpMethod.Post, "api/access/pairings/redeem",
            new RedeemPairingRequest(pairing.PairingId, pairing.PairingSecret), cancellationToken, acceptRateLimit: true);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            await Task.Delay(PairingRetryDelay(response.Headers.RetryAfter, DateTimeOffset.UtcNow), cancellationToken);
            return false; // The caller owns the next poll and its existing pairing-expiry deadline.
        }
        if (response.StatusCode == HttpStatusCode.Accepted) return false;
        if (response.StatusCode != HttpStatusCode.NoContent)
            throw new BrowserAccessException("The server returned an unexpected pairing response. Try again.");
        _csrfToken = null; // The identity changed when the server set the session cookie.
        return true;
    }

    public async Task ApprovePairingAsync(string code, CancellationToken cancellationToken)
    {
        using var response = await SendMutationAsync(HttpMethod.Post, "api/access/pairings/approve",
            new ApprovePairingRequest(code.Trim()), cancellationToken);
    }

    public Task<DeviceReply[]> GetDevicesAsync(CancellationToken cancellationToken) =>
        GetAsync<DeviceReply[]>("api/access/devices", cancellationToken);

    public async Task RevokeDeviceAsync(Guid id, CancellationToken cancellationToken)
    {
        using var response = await SendMutationAsync(HttpMethod.Delete, $"api/access/devices/{id:D}", null, cancellationToken);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        using var response = await SendMutationAsync(HttpMethod.Post, "api/access/disconnect", null, cancellationToken);
        _csrfToken = null;
    }

    public async Task CreateLocalSessionAsync(string adminToken, CancellationToken cancellationToken)
    {
        using var response = await SendMutationAsync(HttpMethod.Post, "api/access/local-session", null,
            cancellationToken, adminToken);
        _csrfToken = null;
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var request = NewRequest(HttpMethod.Get, path);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendMutationAsync(HttpMethod method, string path, object? body,
        CancellationToken cancellationToken, string? adminToken = null, bool acceptRateLimit = false)
    {
        _csrfToken ??= (await GetAsync<BrowserCsrfReply>("api/access/csrf", cancellationToken)).RequestToken;
        if (string.IsNullOrWhiteSpace(_csrfToken))
            throw new BrowserAccessException("The server did not provide a request verification token. Reload and try again.");
        using var request = NewRequest(method, path);
        request.Headers.Add("X-CSRF-TOKEN", _csrfToken);
        if (body is not null) request.Content = JsonContent.Create(body, BrowserJsonContext.Default.GetTypeInfo(body.GetType())!);
        if (adminToken is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var response = await http.SendAsync(request, cancellationToken);
        try
        {
            if (!acceptRateLimit || response.StatusCode != HttpStatusCode.TooManyRequests)
                await EnsureSuccessAsync(response, cancellationToken);
            return response;
        }
        catch
        {
            response.Dispose();
            // A changed session can invalidate the old anti-forgery token. Never replay mutations.
            _csrfToken = null;
            throw;
        }
    }

    internal static TimeSpan PairingRetryDelay(RetryConditionHeaderValue? retryAfter, DateTimeOffset now)
    {
        var delay = retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - now : TimeSpan.FromSeconds(5));
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay;
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.SameOrigin);
        request.SetBrowserRequestCache(BrowserRequestCache.NoStore);
        // Never replay a pairing secret or local admin credential to a redirect destination.
        request.SetBrowserRequestOption("redirect", "error");
        return request;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync((JsonTypeInfo<T>)BrowserJsonContext.Default.GetTypeInfo(typeof(T))!, cancellationToken)
        ?? throw new BrowserAccessException("The server returned an empty response. Try again.");

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "This browser is not connected. Pair it with this server to continue.",
            HttpStatusCode.Forbidden => "This action is not allowed. Refresh your connection and try again.",
            HttpStatusCode.TooManyRequests => "Too many attempts. Wait a moment before trying again.",
            _ => "The server could not complete the request. Try again."
        };
        try
        {
            if ((await response.Content.ReadFromJsonAsync(BrowserJsonContext.Default.BrowserErrorReply, cancellationToken)) is { Message.Length: > 0 } error)
                message = error.Message;
        }
        catch (JsonException) { }
        catch (NotSupportedException) { }
        throw new BrowserAccessException(message);
    }

}

public sealed class BrowserAccessException(string message) : Exception(message);
