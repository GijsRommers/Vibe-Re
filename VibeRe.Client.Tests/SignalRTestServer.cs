using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VibeRe.Client.Infrastructure;
using Protocol = VibeRe.Contracts;

namespace VibeRe.Client.Tests;

// A real SignalR JSON connection over TestServer's HTTP handler. This isolates the
// client boundary from provider/session implementations and never calls an auth provider.
internal sealed class SignalRTestServer : IDisposable
{
    private readonly IHost _host;
    private readonly TestServer _server;

    public SignalRTestServer(TestAuthScenario? scenario = null)
    {
        Scenario = scenario ?? new();
        _host = new HostBuilder().ConfigureWebHost(webHost => webHost.UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddSingleton(Scenario);
                services.AddSignalR(options => options.EnableDetailedErrors = false);
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapHub<TestAuthHub>(Protocol.ServerProtocol.AuthHubPath));
            })).Start();
        _server = _host.GetTestServer();
    }

    public TestAuthScenario Scenario { get; }

    public VibeReClient CreateClient() => new("https://server.example", httpHandler: _server.CreateHandler());

    public void Dispose() => _host.Dispose();
}

internal sealed class TestAuthScenario
{
    public ConcurrentQueue<string> Calls { get; } = new();
    public ConcurrentQueue<string> ApiKeys { get; } = new();
    public ConcurrentQueue<string> WatchedAttempts { get; } = new();
    public ConcurrentQueue<string> CanceledAttempts { get; } = new();

    public Func<string, CancellationToken, Task<Protocol.AuthStatusReply>> Authenticate { get; set; } =
        (_, _) => Task.FromResult(new Protocol.AuthStatusReply { State = Protocol.AuthState.Authenticated });

    public Func<CancellationToken, Task<Protocol.BrowserLoginReply>> Start { get; set; } =
        _ => Task.FromResult(StartedAttempt());

    public Func<string, CancellationToken, Task<Protocol.BrowserLoginReply>> Cancel { get; set; } =
        (id, _) => Task.FromResult(new Protocol.BrowserLoginReply { AttemptId = id, State = Protocol.BrowserLoginState.Canceled });

    public Func<string, CancellationToken, IAsyncEnumerable<Protocol.BrowserLoginReply>> Watch { get; set; } =
        (_, ct) => Replies([new() { State = Protocol.BrowserLoginState.Completed }], ct);

    public static Protocol.BrowserLoginReply StartedAttempt() => new()
    {
        AttemptId = "attempt", SignInUrl = "https://console.mistral.ai/login",
        ExpiresAtUnixSeconds = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
        State = Protocol.BrowserLoginState.Waiting, Message = "Open the browser."
    };

    public static async IAsyncEnumerable<Protocol.BrowserLoginReply> Replies(
        IEnumerable<Protocol.BrowserLoginReply> replies, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        foreach (var reply in replies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return reply;
        }
    }

    public static async IAsyncEnumerable<Protocol.BrowserLoginReply> Failure(Exception error,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.FromException(error);
        cancellationToken.ThrowIfCancellationRequested();
        yield break;
    }

    public static async IAsyncEnumerable<Protocol.BrowserLoginReply> WaitForCancellation(TaskCompletionSource started,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        started.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield break;
    }
}

public sealed class TestAuthHub : Hub
{
    private readonly TestAuthScenario _scenario;

    // The hub is public for SignalR's endpoint discovery; resolve the internal
    // scenario through DI without exposing it as a production-facing type.
    public TestAuthHub(IServiceProvider services) => _scenario = services.GetRequiredService<TestAuthScenario>();

    public Task<Protocol.AuthStatusReply> Login(string apiKey)
    {
        _scenario.Calls.Enqueue(nameof(Login));
        _scenario.ApiKeys.Enqueue(apiKey);
        return _scenario.Authenticate(nameof(Login), Context.ConnectionAborted);
    }

    public Task<Protocol.AuthStatusReply> GetStatus()
    {
        _scenario.Calls.Enqueue(nameof(GetStatus));
        return _scenario.Authenticate(nameof(GetStatus), Context.ConnectionAborted);
    }

    public Task<Protocol.AuthStatusReply> Logout()
    {
        _scenario.Calls.Enqueue(nameof(Logout));
        return _scenario.Authenticate(nameof(Logout), Context.ConnectionAborted);
    }

    public Task<Protocol.BrowserLoginReply> StartBrowserLogin()
    {
        _scenario.Calls.Enqueue(nameof(StartBrowserLogin));
        return _scenario.Start(Context.ConnectionAborted);
    }

    public Task<Protocol.BrowserLoginReply> CancelBrowserLogin(string attemptId)
    {
        _scenario.Calls.Enqueue(nameof(CancelBrowserLogin));
        _scenario.CanceledAttempts.Enqueue(attemptId);
        return _scenario.Cancel(attemptId, Context.ConnectionAborted);
    }

    public IAsyncEnumerable<Protocol.BrowserLoginReply> WatchBrowserLogin(string attemptId, CancellationToken cancellationToken)
    {
        _scenario.Calls.Enqueue(nameof(WatchBrowserLogin));
        _scenario.WatchedAttempts.Enqueue(attemptId);
        return _scenario.Watch(attemptId, cancellationToken);
    }
}
