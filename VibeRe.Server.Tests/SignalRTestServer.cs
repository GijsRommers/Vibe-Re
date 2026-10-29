using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VibeRe.Contracts;
using VibeRe.Server.Application.Providers.Auth;
using VibeRe.Server.Application.Secrets;

namespace VibeRe.Server.Tests;

internal sealed class SignalRTestServer : WebApplicationFactory<Program>
{
    private readonly string? _temporaryDirectory;
    private readonly string _database;
    private readonly string? _token;
    private readonly IPAddress? _remoteIp;
    public TestSecrets Secrets { get; }
    public TestBrowserFlow Browser { get; }
    public TestTimeProvider Clock { get; } = new();
    public ConcurrentQueue<string> Responses { get; } = new();
    public ConcurrentQueue<string> HubRequests { get; } = new();
    public Action<IServiceCollection>? ConfigureTestServices { get; set; }

    public SignalRTestServer(string? database = null, TestSecrets? secrets = null,
        string? token = "server-token", string? remoteIp = "127.0.0.1")
    {
        if (database is null)
        {
            _temporaryDirectory = Path.Combine(Path.GetTempPath(), "vibere-server-test-" + Guid.NewGuid());
            Directory.CreateDirectory(_temporaryDirectory);
            database = Path.Combine(_temporaryDirectory, "server.db");
        }
        _database = database;
        _token = token;
        _remoteIp = remoteIp is null ? null : IPAddress.Parse(remoteIp);
        Secrets = secrets ?? new TestSecrets();
        Browser = new TestBrowserFlow(Clock);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:VibeRe"] = $"Data Source={_database};Pooling=False",
            ["Server:AccessToken"] = _token,
            ["Logging:LogLevel:Default"] = "Warning"
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISecretStore>();
            services.RemoveAll<IAuthFlow>();
            services.RemoveAll<IBrowserAuthFlow>();
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<ISecretStore>(Secrets);
            services.AddSingleton<IAuthFlow, TestAuthFlow>();
            services.AddSingleton<IBrowserAuthFlow>(Browser);
            services.AddSingleton<TimeProvider>(Clock);
            // TestServer has no socket peer. Supply one explicitly, including hostile cases.
            services.AddSingleton<IStartupFilter>(new RemoteIpFilter(this));
            ConfigureTestServices?.Invoke(services);
        });
    }

    public HubClient Connect(string? token = "server-token") => new(CreateHub(token));

    public HubConnection CreateHub(string? token = "server-token") =>
        new HubConnectionBuilder().WithUrl("http://localhost" + ServerProtocol.AuthHubPath, options =>
        {
            options.Transports = HttpTransportType.LongPolling;
            options.AccessTokenProvider = () => Task.FromResult(token);
            options.HttpMessageHandlerFactory = _ => new RecordingHandler(Server.CreateHandler(), Responses);
        }).Build();

    public void AssertResponsesExclude(params string?[] secrets)
    {
        Assert.NotEmpty(Responses);
        foreach (var response in Responses)
            foreach (var secret in secrets.Where(x => !string.IsNullOrEmpty(x)))
                Assert.DoesNotContain(secret!, response, StringComparison.Ordinal);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && _temporaryDirectory is not null && Directory.Exists(_temporaryDirectory))
            Directory.Delete(_temporaryDirectory, true);
    }

    private sealed class RemoteIpFilter(SignalRTestServer server) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                context.Connection.RemoteIpAddress = server._remoteIp;
                if (context.Request.Path.StartsWithSegments(ServerProtocol.AuthHubPath))
                    server.HubRequests.Enqueue(context.Request.Path);
                await continuation(context);
            });
            next(app);
        };
    }

    private sealed class RecordingHandler(HttpMessageHandler inner, ConcurrentQueue<string> responses) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            // Inspect real SignalR JSON framing, not mapped DTOs that could hide a transport leak.
            responses.Enqueue(Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync(cancellationToken)));
            return response;
        }
    }
}

// A thin hub client over the wire contract, so tests exercise the server without any client library.
internal sealed class HubClient(HubConnection hub) : IAsyncDisposable
{
    private static readonly TimeSpan BrowserLoginDeadline = TimeSpan.FromSeconds(15);

    public Task<AuthStatusReply> LoginAsync(string apiKey) => InvokeAsync<AuthStatusReply>("Login", apiKey);
    public Task<AuthStatusReply> GetStatusAsync() => InvokeAsync<AuthStatusReply>("GetStatus");
    public Task<AuthStatusReply> LogoutAsync() => InvokeAsync<AuthStatusReply>("Logout");

    public async Task<BrowserLoginReply> LoginWithBrowserAsync(Func<BrowserLoginReply, Task> showBrowser)
    {
        var attempt = await InvokeAsync<BrowserLoginReply>("StartBrowserLogin");
        await showBrowser(attempt);
        using var deadline = new CancellationTokenSource(BrowserLoginDeadline);
        var latest = attempt;
        await foreach (var update in hub.StreamAsync<BrowserLoginReply>("WatchBrowserLogin", attempt.AttemptId, deadline.Token))
        {
            latest = update;
            if (update.State != BrowserLoginState.Waiting) break;
        }
        return latest;
    }

    private async Task<T> InvokeAsync<T>(string method, params object?[] arguments)
    {
        if (hub.State == HubConnectionState.Disconnected) await hub.StartAsync();
        return await hub.InvokeCoreAsync<T>(method, arguments);
    }

    public ValueTask DisposeAsync() => hub.DisposeAsync();
}

internal sealed class TestSecrets : ISecretStore
{
    public ConcurrentDictionary<string, string> Values { get; } = new();
    public bool Fail { get; set; }
    public Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        if (Fail) throw new IOException("sensitive-storage-value");
        return Task.FromResult(Values.GetValueOrDefault(key));
    }
    public Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        if (Fail) throw new IOException("sensitive-storage-value");
        Values[key] = value;
        return Task.CompletedTask;
    }
    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        if (Fail) throw new IOException("sensitive-storage-value");
        Values.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}

internal sealed class TestAuthFlow : IAuthFlow
{
    public string ProviderName => "mistral";
    public Uri BaseUri => new("https://api.mistral.ai/");
    public Task<ProviderAuthStatus> ValidateAsync(string apiKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProviderAuthStatus(apiKey == "bad-key" ? ProviderAuthState.InvalidCredential
            : ProviderAuthState.Authenticated, "Verified by test provider."));
}

internal sealed class TestTimeProvider : TimeProvider
{
    private long _ticks = DateTimeOffset.UtcNow.UtcTicks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
}

internal sealed class TestBrowserFlow(TestTimeProvider clock) : IBrowserAuthFlow
{
    public string? LastVerifier { get; private set; }
    public int StartCount { get; private set; }
    public TaskCompletionSource Polled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public BrowserPollState PollState { get; set; } = BrowserPollState.Completed;
    public bool FailPoll { get; set; }
    public bool FailStart { get; set; }
    public TaskCompletionSource ExchangeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<string>? ExchangeResult { get; set; }
    public string ProviderName => "mistral";
    public Task<BrowserProcess> StartAsync(string challenge, CancellationToken cancellationToken)
    {
        StartCount++;
        if (FailStart) throw new IOException("sensitive-provider-value");
        return Task.FromResult(new BrowserProcess("process", new("https://console.mistral.ai/login"),
            new("https://console.mistral.ai/api/poll"), clock.GetUtcNow().AddMinutes(5)));
    }
    public Task<BrowserPoll> PollAsync(Uri pollUri, CancellationToken cancellationToken)
    {
        Polled.TrySetResult();
        if (FailPoll) throw new IOException("sensitive-provider-value");
        return Task.FromResult(new BrowserPoll(PollState, PollState == BrowserPollState.Completed ? "exchange-secret" : null));
    }
    public async Task<string> ExchangeAsync(string processId, string exchangeToken, string verifier, CancellationToken cancellationToken)
    {
        LastVerifier = verifier;
        ExchangeStarted.TrySetResult();
        return ExchangeResult is null ? "browser-key" : await ExchangeResult.Task.WaitAsync(cancellationToken);
    }
}
