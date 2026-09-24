using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using VibeRe.Application.Auth;
using VibeRe.Infrastructure.Auth.Mistral;

namespace VibeRe.Infrastructure.Tests.Auth.Mistral;

public class MistralBrowserSignInFlowTests
{
    private const string Api = "https://console.mistral.ai/api";
    private const string SignInUrl = "https://console.mistral.ai/vibe/sign-in/p1";
    private const string PollUrl = "https://console.mistral.ai/api/vibe/sign-in/p1/status";

    private static readonly MistralAuthOptions FastOptions = new() { PollInterval = TimeSpan.FromMilliseconds(1) };

    [Fact]
    public async Task Exchanges_token_and_verifier_for_api_key_after_user_approves()
    {
        var server = new FakeConsole(Pending(), Completed("xchg"));
        var interaction = new RecordingInteraction();

        var apiKey = await CreateFlow(server).AcquireSecretAsync(interaction);

        Assert.Equal("sk-test", apiKey);
        Assert.Equal(new Uri(SignInUrl), interaction.OpenedUrl);
        Assert.Equal(
            [AuthStatus.OpeningBrowser, AuthStatus.WaitingForUser, AuthStatus.Exchanging, AuthStatus.Completed],
            interaction.Statuses);

        var start = server.Bodies[$"{Api}/vibe/sign-in"];
        var exchange = server.Bodies[$"{Api}/vibe/sign-in/p1/exchange"];
        Assert.Equal("S256", (string?)start["code_challenge_method"]);
        Assert.Equal("xchg", (string?)exchange["exchange_token"]);
        Assert.Equal(
            (string?)start["code_challenge"],
            Pkce.ComputeChallenge((string)exchange["code_verifier"]!));
        Assert.Equal(2, server.PollCount);
    }

    [Fact]
    public async Task Tolerates_transient_poll_failures()
    {
        var server = new FakeConsole(Status(HttpStatusCode.BadGateway), Status(HttpStatusCode.BadGateway), Completed("xchg"));

        Assert.Equal("sk-test", await CreateFlow(server).AcquireSecretAsync(new RecordingInteraction()));
    }

    [Fact]
    public async Task Gives_up_after_consecutive_poll_failures()
    {
        var server = new FakeConsole(
            Status(HttpStatusCode.BadGateway), Status(HttpStatusCode.BadGateway), Status(HttpStatusCode.BadGateway));

        var error = await Assert.ThrowsAsync<AuthException>(() => CreateFlow(server).AcquireSecretAsync(new RecordingInteraction()));

        Assert.Equal(AuthErrorCode.PollFailed, error.Code);
    }

    [Theory]
    [InlineData("""{"status":"denied"}""", AuthErrorCode.Denied)]
    [InlineData("""{"status":"expired"}""", AuthErrorCode.Expired)]
    [InlineData("""{"status":"completed"}""", AuthErrorCode.MissingSecret)]
    [InlineData("""{"status":"sideways"}""", AuthErrorCode.UnknownState)]
    public async Task Maps_terminal_poll_states_to_errors(string pollBody, AuthErrorCode expected)
    {
        var server = new FakeConsole(Json(pollBody));

        var error = await Assert.ThrowsAsync<AuthException>(() => CreateFlow(server).AcquireSecretAsync(new RecordingInteraction()));

        Assert.Equal(expected, error.Code);
    }

    [Fact]
    public async Task Treats_gone_as_expired()
    {
        var server = new FakeConsole(Status(HttpStatusCode.Gone));

        var error = await Assert.ThrowsAsync<AuthException>(() => CreateFlow(server).AcquireSecretAsync(new RecordingInteraction()));

        Assert.Equal(AuthErrorCode.Expired, error.Code);
    }

    [Fact]
    public async Task Surfaces_provider_error_message()
    {
        var server = new FakeConsole(Json("""{"status":"error","message":"Organisation disabled"}"""));

        var error = await Assert.ThrowsAsync<AuthException>(() => CreateFlow(server).AcquireSecretAsync(new RecordingInteraction()));

        Assert.Equal(AuthErrorCode.ProviderError, error.Code);
        Assert.Equal("Organisation disabled", error.Message);
    }

    [Fact]
    public async Task Times_out_when_user_never_finishes()
    {
        var server = new FakeConsole(Enumerable.Repeat(Pending(), 10_000).ToArray())
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(200),
        };

        var error = await Assert.ThrowsAsync<AuthException>(() => CreateFlow(server).AcquireSecretAsync(new RecordingInteraction()));

        Assert.Equal(AuthErrorCode.TimedOut, error.Code);
    }

    [Theory]
    [InlineData("https://evil.example/vibe/sign-in/p1", PollUrl)]
    [InlineData("http://console.mistral.ai/vibe/sign-in/p1", PollUrl)]
    [InlineData(SignInUrl, "https://console.mistral.ai/elsewhere/p1")]
    [InlineData(SignInUrl, "https://console.mistral.ai/api/../elsewhere/p1")]
    [InlineData(SignInUrl, "https://console.mistral.ai/api/%2e%2e/elsewhere/p1")]
    [InlineData(SignInUrl, "https://console.mistral.ai/apix/p1")]
    public async Task Rejects_urls_outside_the_configured_console(string signInUrl, string pollUrl)
    {
        var server = new FakeConsole { SignInUrl = signInUrl, PollUrl = pollUrl };

        var error = await Assert.ThrowsAsync<AuthException>(() => CreateFlow(server).AcquireSecretAsync(new RecordingInteraction()));

        Assert.Equal(AuthErrorCode.StartFailed, error.Code);
        Assert.Equal(0, server.PollCount);
    }

    [Fact]
    public async Task Rehomes_urls_onto_configured_console_when_origin_rewrite_is_allowed()
    {
        var server = new FakeConsole(Completed("xchg"))
        {
            SignInUrl = "https://public.example/vibe/sign-in/p1",
            PollUrl = "https://public.example/api/vibe/sign-in/p1/status",
        };
        var interaction = new RecordingInteraction();

        await CreateFlow(server, FastOptions with { AllowOriginRewrite = true }).AcquireSecretAsync(interaction);

        Assert.Equal(new Uri(SignInUrl), interaction.OpenedUrl);
        Assert.Equal(1, server.PollCount);
    }

    private static MistralBrowserSignInFlow CreateFlow(FakeConsole server, MistralAuthOptions? options = null) =>
        new(new HttpClient(server), options ?? FastOptions);

    private static Func<HttpResponseMessage> Pending() => Json("""{"status":"pending"}""");

    private static Func<HttpResponseMessage> Completed(string exchangeToken) =>
        Json($$"""{"status":"completed","exchange_token":"{{exchangeToken}}"}""");

    private static Func<HttpResponseMessage> Status(HttpStatusCode status) => () => new HttpResponseMessage(status);

    private static Func<HttpResponseMessage> Json(string body) =>
        () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private sealed class FakeConsole(params Func<HttpResponseMessage>[] pollResponses) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _pollResponses = new(pollResponses);

        public string SignInUrl { get; init; } = MistralBrowserSignInFlowTests.SignInUrl;

        public string PollUrl { get; init; } = MistralBrowserSignInFlowTests.PollUrl;

        public DateTimeOffset ExpiresAt { get; init; } = DateTimeOffset.UtcNow.AddMinutes(10);

        public Dictionary<string, JsonObject> Bodies { get; } = [];

        public int PollCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (request.Content is not null)
            {
                Bodies[url] = (await request.Content.ReadFromJsonAsync<JsonObject>(cancellationToken))!;
            }

            if (request.Method == HttpMethod.Post && url == $"{Api}/vibe/sign-in")
            {
                return JsonContent(new { process_id = "p1", sign_in_url = SignInUrl, poll_url = PollUrl, expires_at = ExpiresAt.ToString("O") });
            }

            if (request.Method == HttpMethod.Post && url == $"{Api}/vibe/sign-in/p1/exchange")
            {
                return JsonContent(new { api_key = "sk-test" });
            }

            if (request.Method == HttpMethod.Get && url == MistralBrowserSignInFlowTests.PollUrl)
            {
                PollCount++;
                return _pollResponses.Dequeue()();
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage JsonContent(object body) => new(HttpStatusCode.OK) { Content = System.Net.Http.Json.JsonContent.Create(body) };
    }
}

internal sealed class RecordingInteraction(string? secret = null) : IAuthInteraction
{
    public List<AuthStatus> Statuses { get; } = [];

    public Uri? OpenedUrl { get; private set; }

    public void ReportStatus(AuthStatus status) => Statuses.Add(status);

    public void OpenSignInUrl(Uri url, DateTimeOffset expiresAt) => OpenedUrl = url;

    public Task<string?> PromptSecretAsync(string prompt, CancellationToken cancellationToken) => Task.FromResult(secret);
}
