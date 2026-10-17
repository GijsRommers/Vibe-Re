using System.Security.Cryptography;
using System.Text;
using VibeRe.Application.Providers.Auth;

namespace VibeRe.Application.Tests;

public class BrowserAuthServiceTests
{
    [Fact]
    public async Task BrowserLoginExchangesOnServerAndUsesSameCredentialAction()
    {
        var fixture = new AuthServiceTests.Fixture();
        var clock = new Clock();
        var flow = new BrowserFlow(clock);
        var browser = new BrowserAuthService([flow], fixture.Auth, clock);
        var start = await browser.StartAsync("mistral");
        Assert.Equal(BrowserAttemptState.Waiting, start.State);
        Assert.Null(await fixture.Resolver.ResolveAsync("mistral"));
        var results = await Task.WhenAll(browser.PollAsync(start.AttemptId), browser.PollAsync(start.AttemptId));
        Assert.All(results, result => Assert.Equal(BrowserAttemptState.Completed, result.State));
        Assert.Equal(1, flow.Exchanges);
        Assert.Equal("browser-key", await fixture.Resolver.ResolveAsync("mistral"));
        Assert.NotNull(flow.Verifier);
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(flow.Verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(flow.Challenge, challenge);
    }

    [Theory]
    [InlineData("denied", BrowserAttemptState.Failed)]
    [InlineData("expired", BrowserAttemptState.Expired)]
    [InlineData("error", BrowserAttemptState.Failed)]
    public async Task FailedAttemptDoesNotSaveCredential(string state, BrowserAttemptState expected)
    {
        var fixture = new AuthServiceTests.Fixture();
        var clock = new Clock();
        var flow = new BrowserFlow(clock) { PollState = state };
        var browser = new BrowserAuthService([flow], fixture.Auth, clock);
        var start = await browser.StartAsync("mistral");
        Assert.Equal(expected, (await browser.PollAsync(start.AttemptId)).State);
        Assert.Equal(0, flow.Exchanges);
        Assert.Null(await fixture.Resolver.ResolveAsync("mistral"));
    }

    [Fact]
    public async Task CanceledAndExpiredAttemptsCannotAuthenticate()
    {
        var fixture = new AuthServiceTests.Fixture();
        var clock = new Clock();
        var flow = new BrowserFlow(clock);
        var browser = new BrowserAuthService([flow], fixture.Auth, clock);
        var canceled = await browser.StartAsync("mistral");
        await browser.CancelProviderAsync("mistral");
        Assert.Equal(BrowserAttemptState.Canceled, (await browser.PollAsync(canceled.AttemptId)).State);
        var expired = await browser.StartAsync("mistral");
        clock.Now = clock.Now.AddMinutes(6);
        Assert.Equal(BrowserAttemptState.Expired, (await browser.PollAsync(expired.AttemptId)).State);
        Assert.Equal(0, flow.Exchanges);
        Assert.Null(await fixture.Resolver.ResolveAsync("mistral"));
    }

    [Fact]
    public async Task TerminalAttemptsDoNotConsumePendingLoginCapacity()
    {
        var fixture = new AuthServiceTests.Fixture();
        var clock = new Clock();
        var browser = new BrowserAuthService([new BrowserFlow(clock)], fixture.Auth, clock);
        for (var i = 0; i < 80; i++)
        {
            var canceled = await browser.StartAsync("mistral");
            await browser.CancelAsync(canceled.AttemptId);
            var completed = await browser.StartAsync("mistral");
            Assert.Equal(BrowserAttemptState.Completed, (await browser.PollAsync(completed.AttemptId)).State);
        }
        Assert.Equal(BrowserAttemptState.Waiting, (await browser.StartAsync("mistral")).State);
    }

    [Fact]
    public async Task PollingIsThrottledAndAttemptsAreBounded()
    {
        var fixture = new AuthServiceTests.Fixture();
        var clock = new Clock();
        var flow = new BrowserFlow(clock) { PollState = "pending" };
        var browser = new BrowserAuthService([flow], fixture.Auth, clock);
        var start = await browser.StartAsync("mistral");
        await browser.PollAsync(start.AttemptId);
        await browser.PollAsync(start.AttemptId);
        Assert.Equal(1, flow.Polls);
        for (var i = 1; i < 32; i++) await browser.StartAsync("mistral");
        await Assert.ThrowsAsync<InvalidOperationException>(() => browser.StartAsync("mistral"));
        clock.Now = clock.Now.AddMinutes(6);
        Assert.Equal(BrowserAttemptState.Waiting, (await browser.StartAsync("mistral")).State);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class BrowserFlow(Clock clock) : IBrowserAuthFlow
    {
        public string ProviderName => "mistral";
        public string PollState { get; set; } = "completed";
        public int Exchanges { get; private set; }
        public int Polls { get; private set; }
        public string? Challenge { get; private set; }
        public string? Verifier { get; private set; }
        public Task<BrowserProcess> StartAsync(string challenge, CancellationToken cancellationToken)
        {
            Challenge = challenge;
            return Task.FromResult(new BrowserProcess("process", new("https://console.mistral.ai/login"),
                new("https://console.mistral.ai/api/poll"), clock.Now.AddMinutes(5)));
        }
        public Task<BrowserPoll> PollAsync(Uri pollUri, CancellationToken cancellationToken)
        {
            Polls++;
            return Task.FromResult(new BrowserPoll(PollState, "exchange-token"));
        }
        public Task<string> ExchangeAsync(string processId, string exchangeToken, string verifier, CancellationToken cancellationToken)
        {
            Exchanges++;
            Verifier = verifier;
            return Task.FromResult("browser-key");
        }
    }
}
