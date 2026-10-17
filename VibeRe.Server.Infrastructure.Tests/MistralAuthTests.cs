using System.Net;
using System.Text;
using System.Text.Json;
using VibeRe.Server.Application.Providers.Auth;
using VibeRe.Server.Infrastructure.Providers.Mistral.Auth;

namespace VibeRe.Server.Infrastructure.Tests;

public class MistralAuthTests
{
    [Theory]
    [InlineData(200, ProviderAuthState.Authenticated)]
    [InlineData(401, ProviderAuthState.InvalidCredential)]
    [InlineData(403, ProviderAuthState.InvalidCredential)]
    [InlineData(402, ProviderAuthState.SubscriptionRequired)]
    [InlineData(429, ProviderAuthState.Unavailable)]
    [InlineData(500, ProviderAuthState.Unavailable)]
    public async Task ValidationUsesMainApiAndPreservesProviderFailureMeaning(int status, ProviderAuthState expected)
    {
        using var client = new HttpClient(new Handler(async request =>
        {
            Assert.Equal("Bearer test-key", request.Headers.Authorization!.ToString());
            await Task.CompletedTask;
            if (request.RequestUri!.Host == "console.mistral.ai")
                return new(HttpStatusCode.Unauthorized);
            Assert.Equal("https://api.mistral.ai/v1/models", request.RequestUri.AbsoluteUri);
            return new((HttpStatusCode)status) { Content = new StringContent("sensitive-provider-error") };
        }));
        var result = await new MistralAuthFlow(client).ValidateAsync("test-key");
        Assert.Equal(expected, result.State);
        Assert.DoesNotContain("sensitive-provider-error", result.Message);
    }

    [Theory]
    [InlineData(200, ProviderAuthState.Authenticated)]
    [InlineData(503, ProviderAuthState.Unavailable)]
    public async Task BrowserAccountCredentialsAreNotRejectedBasedOnMainApiAlone(int accountStatus, ProviderAuthState expected)
    {
        using var client = new HttpClient(new Handler(request =>
            Task.FromResult(new HttpResponseMessage(request.RequestUri!.Host == "console.mistral.ai"
                ? (HttpStatusCode)accountStatus : HttpStatusCode.Unauthorized))));
        Assert.Equal(expected, (await new MistralAuthFlow(client).ValidateAsync("browser-key")).State);
    }

    [Fact]
    public async Task BrowserProtocolValidatesUrlsAndExchangesPkceForKey()
    {
        using var client = new HttpClient(new Handler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/vibe/sign-in")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert.Equal("S256", body.RootElement.GetProperty("code_challenge_method").GetString());
                return Json(new { process_id = "process-1", sign_in_url = "https://console.mistral.ai/codestral/cli/authenticate#state=state",
                    poll_url = "https://console.mistral.ai/api/vibe/sign-in/poll/token", expires_at = DateTimeOffset.UtcNow.AddMinutes(5) });
            }
            if (path == "/api/vibe/sign-in/poll/token") return Json(new { status = "completed", exchange_token = "exchange-token" });
            Assert.Equal("/api/vibe/sign-in/process-1/exchange", path);
            using var exchange = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("verifier", exchange.RootElement.GetProperty("code_verifier").GetString());
            Assert.Equal("exchange-token", exchange.RootElement.GetProperty("exchange_token").GetString());
            return Json(new { api_key = "browser-key" });
        }));
        var flow = new MistralBrowserAuthFlow(client);
        var attempt = await flow.StartAsync("challenge", default);
        var poll = await flow.PollAsync(attempt.PollUri, default);
        Assert.Equal("completed", poll.State);
        Assert.Equal("browser-key", await flow.ExchangeAsync(attempt.ProcessId, poll.ExchangeToken!, "verifier", default));
    }

    [Theory]
    [InlineData("https://evil.example/api/poll")]
    [InlineData("http://console.mistral.ai/api/poll")]
    [InlineData("https://console.mistral.ai:8443/api/poll")]
    [InlineData("https://user:password@console.mistral.ai/api/poll")]
    [InlineData("https://console.mistral.ai/elsewhere/poll")]
    [InlineData("https://console.mistral.ai/api/%2e%2e%2felsewhere")]
    public void UntrustedBrowserPollingUrlsAreRejected(string uri) =>
        Assert.Throws<InvalidDataException>(() => MistralBrowserAuthFlow.ValidateUri(uri, "/api/"));

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request);
    }
}
