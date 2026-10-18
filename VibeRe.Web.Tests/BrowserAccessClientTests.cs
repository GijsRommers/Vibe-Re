using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using VibeRe.Contracts;
using VibeRe.Web.Services;

namespace VibeRe.Web.Tests;

public class BrowserAccessClientTests
{
    private static readonly PairingReply Pairing = new("pair-id", "pair-secret", "ABCD-1234", DateTimeOffset.UtcNow.AddMinutes(5));

    [Fact]
    public async Task PairingUsesSameOriginCsrfAndCookieRedemptionWithoutReturningToken()
    {
        var handler = new RecordingHandler(
            Json(new { requestToken = "csrf-one" }), Json(Pairing),
            new(HttpStatusCode.Accepted), new(HttpStatusCode.NoContent),
            Json(new { requestToken = "csrf-two" }), new(HttpStatusCode.NoContent));
        var api = Create(handler);
        var pairing = await api.BeginPairingAsync("My browser", default);
        Assert.False(await api.RedeemPairingAsync(pairing, default));
        Assert.True(await api.RedeemPairingAsync(pairing, default));
        await api.DisconnectAsync(default);

        Assert.Equal(["GET", "POST", "POST", "POST", "GET", "POST"], handler.Requests.Select(x => x.Method).ToArray());
        Assert.Equal("https://vibe.example.com/api/access/pairings", handler.Requests[1].Url);
        Assert.Equal("csrf-one", handler.Requests[1].Csrf);
        Assert.Equal("csrf-one", handler.Requests[2].Csrf);
        Assert.Equal("csrf-two", handler.Requests[5].Csrf);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("same-origin", request.FetchOptions["credentials"]);
            Assert.Equal("no-store", request.FetchOptions["cache"]);
            Assert.Equal("error", request.FetchOptions["redirect"]);
            Assert.Null(request.Authorization);
            Assert.DoesNotContain("secret", request.Url);
        });
        Assert.Contains("pair-secret", handler.Requests[2].Body);
        Assert.DoesNotContain("pair-secret", handler.Requests[1].Body);
    }

    [Fact]
    public async Task LocalTokenIsSentOnlyForTheBootstrapMutation()
    {
        var handler = new RecordingHandler(Json(new { requestToken = "one" }), new(HttpStatusCode.NoContent),
            Json(new AccessStatusReply(true, true, true)));
        var api = Create(handler);
        await api.CreateLocalSessionAsync("admin-secret", default);
        Assert.True((await api.GetStatusAsync(default)).Authorized);
        Assert.Null(handler.Requests[0].Authorization);
        Assert.Equal("Bearer admin-secret", handler.Requests[1].Authorization);
        Assert.Null(handler.Requests[2].Authorization);
        Assert.Equal("https://vibe.example.com/api/access/local-session", handler.Requests[1].Url);
        Assert.DoesNotContain("admin-secret", handler.Requests[1].Body);
        Assert.Equal("error", handler.Requests[1].FetchOptions["redirect"]);
    }

    [Fact]
    public async Task EveryAdministrativeMutationIsCsrfProtected()
    {
        var id = Guid.NewGuid();
        var handler = new RecordingHandler(Json(new { requestToken = "csrf" }), new(HttpStatusCode.NoContent), new(HttpStatusCode.NoContent));
        var api = Create(handler);
        await api.ApprovePairingAsync(" ABCD-1234 ", default);
        await api.RevokeDeviceAsync(id, default);
        Assert.Equal("csrf", handler.Requests[1].Csrf);
        Assert.Equal("csrf", handler.Requests[2].Csrf);
        Assert.Equal("DELETE", handler.Requests[2].Method);
        Assert.Equal("error", handler.Requests[2].FetchOptions["redirect"]);
        Assert.EndsWith($"/api/access/devices/{id:D}", handler.Requests[2].Url);
        Assert.Equal("ABCD-1234", JsonDocument.Parse(handler.Requests[1].Body).RootElement.GetProperty("approvalCode").GetString());
    }

    [Fact]
    public async Task FailedMutationIsNotReplayedAndCsrfIsRefreshedOnExplicitRetry()
    {
        var handler = new RecordingHandler(Json(new { requestToken = "old" }),
            Json(new { message = "Approval code expired." }, HttpStatusCode.Forbidden),
            Json(new { requestToken = "new" }), new(HttpStatusCode.NoContent));
        var api = Create(handler);
        var error = await Assert.ThrowsAsync<BrowserAccessException>(() => api.ApprovePairingAsync("CODE", default));
        Assert.Equal("Approval code expired.", error.Message);
        Assert.Equal(2, handler.Requests.Count);
        await api.ApprovePairingAsync("NEW", default);
        Assert.Equal("new", handler.Requests[3].Csrf);
    }

    [Fact]
    public async Task HtmlErrorBodiesAreNotDisplayed()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        { Content = new StringContent("<html>private server details</html>") });
        var error = await Assert.ThrowsAsync<BrowserAccessException>(() => Create(handler).GetStatusAsync(default));
        Assert.DoesNotContain("private", error.Message);
    }

    [Fact]
    public async Task UnknownSuccessfulRedemptionDoesNotClaimCompletion()
    {
        var handler = new RecordingHandler(Json(new { requestToken = "csrf" }), new(HttpStatusCode.OK));
        await Assert.ThrowsAsync<BrowserAccessException>(() => Create(handler).RedeemPairingAsync(Pairing, default));
    }

    [Fact]
    public async Task RateLimitedRedemptionReturnsPendingWithoutReplayingTheMutation()
    {
        var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
        var handler = new RecordingHandler(Json(new { requestToken = "csrf" }), limited, new(HttpStatusCode.NoContent));
        var api = Create(handler);
        Assert.False(await api.RedeemPairingAsync(Pairing, default));
        Assert.Equal(2, handler.Requests.Count); // CSRF + exactly one redeem; no implicit replay.
        Assert.True(await api.RedeemPairingAsync(Pairing, default));
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("csrf", handler.Requests[2].Csrf);
    }

    [Fact]
    public async Task PairingBackoffHonorsCallerCancellation()
    {
        var handler = new RecordingHandler(Json(new { requestToken = "csrf" }), new(HttpStatusCode.TooManyRequests));
        using var cancellation = new CancellationTokenSource();
        var pending = Create(handler).RedeemPairingAsync(Pairing, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void PairingRetryAfterIsBoundedAndAcceptsDateOrSeconds()
    {
        var now = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromSeconds(5), BrowserAccessClient.PairingRetryDelay(null, now));
        Assert.Equal(TimeSpan.FromSeconds(7), BrowserAccessClient.PairingRetryDelay(new RetryConditionHeaderValue(TimeSpan.FromSeconds(7)), now));
        Assert.Equal(TimeSpan.FromSeconds(30), BrowserAccessClient.PairingRetryDelay(new RetryConditionHeaderValue(TimeSpan.FromMinutes(5)), now));
        Assert.Equal(TimeSpan.FromSeconds(9), BrowserAccessClient.PairingRetryDelay(new RetryConditionHeaderValue(now.AddSeconds(9)), now));
        Assert.Equal(TimeSpan.Zero, BrowserAccessClient.PairingRetryDelay(new RetryConditionHeaderValue(now.AddSeconds(-1)), now));
    }

    [Fact]
    public async Task RateLimitedApprovalStillFailsWithoutAnAutomaticRetry()
    {
        var handler = new RecordingHandler(Json(new { requestToken = "csrf" }), new(HttpStatusCode.TooManyRequests));
        await Assert.ThrowsAsync<BrowserAccessException>(() => Create(handler).ApprovePairingAsync("CODE", default));
        Assert.Equal(2, handler.Requests.Count);
    }

    private static BrowserAccessClient Create(RecordingHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://vibe.example.com/") });

    private static HttpResponseMessage Json<T>(T body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(body) };

    private sealed record Request(string Method, string Url, string? Csrf, string? Authorization, string Body,
        IDictionary<string, object> FetchOptions);

    private sealed class RecordingHandler(params HttpResponseMessage[] replies) : HttpMessageHandler
    {
        public List<Request> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Options.TryGetValue(new HttpRequestOptionsKey<IDictionary<string, object>>("WebAssemblyFetchOptions"), out var options);
            Requests.Add(new(request.Method.Method, request.RequestUri!.AbsoluteUri,
                request.Headers.TryGetValues("X-CSRF-TOKEN", out var csrf) ? csrf.Single() : null,
                request.Headers.Authorization?.ToString(),
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken), options ?? new Dictionary<string, object>()));
            return replies[Requests.Count - 1];
        }
    }
}
