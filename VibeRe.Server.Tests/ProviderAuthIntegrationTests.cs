using System.Net;
using System.Text;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VibeRe.Server.Application.Providers.Auth;
using VibeRe.Server.Application.Secrets;
using VibeRe.Client.Application;
using VibeRe.Client.Infrastructure;
using Protocol = VibeRe.Protocol;

namespace VibeRe.Server.Tests;

public class ProviderAuthIntegrationTests
{
    [Fact]
    public async Task ReadinessIdentifiesRuntimeWithoutRequiringServerToken()
    {
        using var server = new Factory();
        using var http = server.CreateClient();
        var response = await http.GetAsync("/.well-known/vibe-re/server");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"application\":\"vibe-re\"", body);
        Assert.Contains("\"protocolVersion\":1", body);
        Assert.DoesNotContain("token", body);
    }

    [Fact]
    public async Task ClientsSeeSameServerLoginAndOtherServersRemainSignedOut()
    {
        using var first = new Factory();
        using var second = new Factory();
        await using var cli = first.Connect();
        await using var desktop = first.Connect();
        await using var otherServer = second.Connect();
        Assert.Equal(AuthState.SignedOut, (await cli.GetStatusAsync()).State);
        Assert.Equal(AuthState.Authenticated, (await cli.LoginAsync("valid-key")).State);
        Assert.Equal(AuthState.Authenticated, (await desktop.GetStatusAsync()).State);
        Assert.Equal(AuthState.SignedOut, (await otherServer.GetStatusAsync()).State);
        Assert.Equal(AuthState.InvalidCredential, (await desktop.LoginAsync("bad-key")).State);
        Assert.Equal(AuthState.Authenticated, (await cli.GetStatusAsync()).State);
        await desktop.LogoutAsync();
        Assert.Equal(AuthState.SignedOut, (await cli.GetStatusAsync()).State);
        Assert.Empty(first.Secrets.Values);
    }

    [Fact]
    public async Task BrowserAndApiKeyLoginBothPersistOnServerWithoutReturningSecrets()
    {
        using var server = new Factory();
        await using var client = server.Connect();
        var completion = await client.LoginWithBrowserAsync(attempt =>
        {
            Assert.DoesNotContain("verifier", attempt.ToString());
            return Task.CompletedTask;
        });
        Assert.Equal(BrowserLoginState.Completed, completion.State);
        Assert.Equal(AuthState.Authenticated, completion.AuthStatus!.State);
        Assert.DoesNotContain("browser-key", completion.ToString());
        Assert.NotNull(server.Browser.LastVerifier);
        Assert.All(server.Responses, response =>
        {
            Assert.DoesNotContain("browser-key", response);
            Assert.DoesNotContain(server.Browser.LastVerifier, response);
        });
        Assert.Contains("browser-key", server.Secrets.Values.Values);
        await client.LoginAsync("new-api-key");
        Assert.Contains("new-api-key", server.Secrets.Values.Values);
        Assert.DoesNotContain("browser-key", server.Secrets.Values.Values);
        Assert.Equal(AuthState.Authenticated, (await client.GetStatusAsync()).State);
        Assert.All(server.Responses, response => Assert.DoesNotContain("new-api-key", response));
    }

    [Fact]
    public async Task SharedBrowserLoginOpensClientUrlAndPersistsCredentialOnServer()
    {
        using var server = new Factory();
        await using var client = server.Connect();
        string? opened = null;
        var result = await client.LoginWithBrowserAsync(attempt =>
        {
            opened = attempt.SignInUrl;
            return Task.CompletedTask;
        });
        Assert.Equal("https://console.mistral.ai/login", opened);
        Assert.Equal(BrowserLoginState.Completed, result.State);
        Assert.Equal(AuthState.Authenticated, result.AuthStatus!.State);
        Assert.Contains("browser-key", server.Secrets.Values.Values);
    }

    [Fact]
    public async Task LogoutCancelsPendingBrowserLogin()
    {
        using var server = new Factory();
        await using var client = server.Connect();
        var result = await client.LoginWithBrowserAsync(async _ => { await client.LogoutAsync(); });
        Assert.Equal(BrowserLoginState.Canceled, result.State);
        Assert.Empty(server.Secrets.Values);
        Assert.Equal(AuthState.SignedOut, (await client.GetStatusAsync()).State);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-token")]
    public async Task MissingOrWrongServerTokenCannotReadOrChangeProviderCredentials(string? token)
    {
        using var server = new Factory();
        await using var client = server.Connect(token);
        var error = await Assert.ThrowsAsync<ServerConnectionException>(() => client.LoginAsync("valid-key"));
        Assert.Equal(StatusCode.Unauthenticated, Assert.IsType<RpcException>(error.InnerException).StatusCode);
        Assert.Empty(server.Secrets.Values);
    }

    [Fact]
    public async Task InvalidRequestAndStorageFailuresAreSafeRpcErrors()
    {
        using var server = new Factory();
        await using var client = server.Connect();
        var invalid = await Assert.ThrowsAsync<ServerConnectionException>(() => client.LoginAsync(""));
        Assert.Equal(StatusCode.InvalidArgument, Assert.IsType<RpcException>(invalid.InnerException).StatusCode);
        server.Secrets.Fail = true;
        var failed = await Assert.ThrowsAsync<ServerConnectionException>(() => client.LoginAsync("valid-key"));
        Assert.Equal(StatusCode.Internal, Assert.IsType<RpcException>(failed.InnerException).StatusCode);
        Assert.DoesNotContain("sensitive-storage-value", failed.ToString());
    }

    [Fact]
    public async Task ProviderMetadataAndSecretSurviveServerRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vibere-restart-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "server.db");
        var secrets = new Secrets();
        try
        {
            using (var first = new Factory(database, secrets))
            await using (var client = first.Connect()) await client.LoginAsync("persistent-key");
            using var restarted = new Factory(database, secrets);
            await using var reconnect = restarted.Connect();
            Assert.Equal(AuthState.Authenticated, (await reconnect.GetStatusAsync()).State);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(ProviderAuthState.SignedOut, Protocol.AuthState.SignedOut)]
    [InlineData(ProviderAuthState.Authenticated, Protocol.AuthState.Authenticated)]
    [InlineData(ProviderAuthState.InvalidCredential, Protocol.AuthState.InvalidCredential)]
    [InlineData(ProviderAuthState.SubscriptionRequired, Protocol.AuthState.SubscriptionRequired)]
    [InlineData(ProviderAuthState.Unavailable, Protocol.AuthState.Unavailable)]
    public async Task ProviderStatesMapToExplicitWireValues(ProviderAuthState state, Protocol.AuthState expected)
    {
        using var server = WithAuthStatus(new(state, "Provider status."));
        using var channel = server.CreateChannel();
        var client = new Protocol.ProviderAuth.ProviderAuthClient(channel);

        var result = await client.GetStatusAsync(new() { Provider = "mistral" }, Authorized());

        Assert.Equal(expected, result.State);
        Assert.Equal("Provider status.", result.Message);
    }

    [Theory]
    [InlineData(BrowserAttemptState.Waiting, Protocol.BrowserLoginState.Waiting)]
    [InlineData(BrowserAttemptState.Completed, Protocol.BrowserLoginState.Completed)]
    [InlineData(BrowserAttemptState.Failed, Protocol.BrowserLoginState.Failed)]
    [InlineData(BrowserAttemptState.Expired, Protocol.BrowserLoginState.Expired)]
    [InlineData(BrowserAttemptState.Canceled, Protocol.BrowserLoginState.Canceled)]
    public async Task BrowserStatesMapToExplicitWireValues(BrowserAttemptState state, Protocol.BrowserLoginState expected)
    {
        using var server = WithBrowserStatus(Attempt(state));
        using var channel = server.CreateChannel();
        var client = new Protocol.ProviderAuth.ProviderAuthClient(channel);

        var result = await client.StartBrowserLoginAsync(new() { Provider = "mistral" }, Authorized());

        Assert.Equal(expected, result.State);
        Assert.Equal("attempt-id", result.AttemptId);
        Assert.Equal("Browser status.", result.Message);
    }

    [Fact]
    public async Task UnknownProviderStateIsRejectedWithoutLeakingServiceMessage()
    {
        using var server = WithAuthStatus(new((ProviderAuthState)999, "sensitive-service-message"));
        using var channel = server.CreateChannel();
        var client = new Protocol.ProviderAuth.ProviderAuthClient(channel);

        var exception = await Assert.ThrowsAsync<RpcException>(() =>
            client.GetStatusAsync(new() { Provider = "mistral" }, Authorized()).ResponseAsync);

        Assert.Equal(StatusCode.Internal, exception.StatusCode);
        Assert.Equal("Could not access server credential storage.", exception.Status.Detail);
        Assert.All(server.Responses, response => Assert.DoesNotContain("sensitive-service-message", response));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownBrowserOrNestedProviderStateRejectsEntireReply(bool nestedProviderState)
    {
        var attempt = nestedProviderState
            ? Attempt(BrowserAttemptState.Completed) with { AuthStatus = new((ProviderAuthState)999, "sensitive-service-message") }
            : Attempt((BrowserAttemptState)999) with { Message = "sensitive-service-message" };
        using var server = WithBrowserStatus(attempt);
        using var channel = server.CreateChannel();
        var client = new Protocol.ProviderAuth.ProviderAuthClient(channel);

        var exception = await Assert.ThrowsAsync<RpcException>(() =>
            client.StartBrowserLoginAsync(new() { Provider = "mistral" }, Authorized()).ResponseAsync);

        Assert.Equal(StatusCode.Unavailable, exception.StatusCode);
        Assert.Equal("Could not start Mistral browser login. Try again.", exception.Status.Detail);
        Assert.All(server.Responses, response => Assert.DoesNotContain("sensitive-service-message", response));
    }

    [Fact]
    public async Task BrowserLoginCapacityReturnsSafeResourceExhaustedAndAllowsRetryAfterCancel()
    {
        using var server = new Factory();
        using var channel = server.CreateChannel();
        var client = new Protocol.ProviderAuth.ProviderAuthClient(channel);
        var first = await client.StartBrowserLoginAsync(new() { Provider = "mistral" }, Authorized());
        for (var i = 1; i < 32; i++)
        {
            await client.StartBrowserLoginAsync(new() { Provider = "mistral" }, Authorized());
        }

        var exception = await Assert.ThrowsAsync<RpcException>(() =>
            client.StartBrowserLoginAsync(new() { Provider = "mistral" }, Authorized()).ResponseAsync);

        Assert.Equal(StatusCode.ResourceExhausted, exception.StatusCode);
        Assert.Equal("Too many pending browser logins. Cancel an existing login or wait for it to expire.",
            exception.Status.Detail);
        await client.CancelBrowserLoginAsync(new() { AttemptId = first.AttemptId }, Authorized());
        var retry = await client.StartBrowserLoginAsync(new() { Provider = "mistral" }, Authorized());
        Assert.Equal(Protocol.BrowserLoginState.Waiting, retry.State);
    }

    [Theory]
    [InlineData("127.0.0.1", "localhost")]
    [InlineData("127.0.0.1", "LOCALHOST:5067")]
    [InlineData("127.0.0.1", "127.0.0.2")]
    [InlineData("::1", "[::1]")]
    [InlineData("::ffff:127.0.0.1", "localhost")]
    [InlineData("127.0.0.1", "[::ffff:127.0.0.1]")]
    public async Task LiteralLoopbackPeerAndHostAllowTokenlessLocalHttp(string peer, string host)
    {
        using var server = new Factory { AccessToken = null, RemoteIp = peer };
        using var channel = server.CreateChannel(host: host);
        var client = new Protocol.ProviderAuth.ProviderAuthClient(channel);

        var result = await client.GetStatusAsync(new() { Provider = "mistral" });

        Assert.Equal(Protocol.AuthState.SignedOut, result.State);
    }

    [Theory]
    [InlineData(null, "localhost")]
    [InlineData("203.0.113.10", "localhost")]
    [InlineData("::ffff:203.0.113.10", "localhost")]
    [InlineData("127.0.0.1", "rebind.example")]
    [InlineData("127.0.0.1", "localhost.example")]
    [InlineData("127.0.0.1", "localhost.")]
    [InlineData("127.0.0.1", "loopback")]
    [InlineData("127.0.0.1", "203.0.113.10")]
    [InlineData("127.0.0.1", "[::ffff:203.0.113.10]")]
    public async Task TokenlessAccessRequiresBothLiteralLoopbackHostAndPeer(string? peer, string host)
    {
        using var server = new Factory { AccessToken = null, RemoteIp = peer };
        using var channel = server.CreateChannel("https://localhost", host);
        var client = new Protocol.ProviderAuth.ProviderAuthClient(channel);

        var exception = await Assert.ThrowsAsync<RpcException>(() =>
            client.LoginAsync(new() { Provider = "mistral", ApiKey = "valid-key" }).ResponseAsync);

        Assert.Equal(StatusCode.PermissionDenied, exception.StatusCode);
        Assert.Equal("Remote access requires a server access token.", exception.Status.Detail);
        Assert.Empty(server.Secrets.Values);
    }

    [Theory]
    [InlineData(null, "localhost")]
    [InlineData("203.0.113.10", "server.example")]
    [InlineData("203.0.113.10", "localhost")]
    [InlineData("::ffff:203.0.113.10", "localhost")]
    [InlineData("127.0.0.1", "rebind.example")]
    [InlineData("127.0.0.1", "loopback")]
    public async Task NonLocalPlaintextIsRejectedEvenWithValidServerToken(string? peer, string host)
    {
        using var server = new Factory { RemoteIp = peer };
        using var channel = server.CreateChannel(host: host);
        var client = new Protocol.ProviderAuth.ProviderAuthClient(channel);

        var exception = await Assert.ThrowsAsync<RpcException>(() =>
            client.LoginAsync(new() { Provider = "mistral", ApiKey = "valid-key" }, Authorized()).ResponseAsync);

        Assert.Equal(StatusCode.PermissionDenied, exception.StatusCode);
        Assert.Equal("Remote access requires HTTPS.", exception.Status.Detail);
        Assert.Empty(server.Secrets.Values);
    }

    [Theory]
    [InlineData("203.0.113.10")]
    [InlineData("::ffff:203.0.113.10")]
    public async Task RemoteHttpsWithValidServerTokenCanAuthenticate(string peer)
    {
        using var server = new Factory { RemoteIp = peer };
        using var channel = server.CreateChannel("https://server.example");
        var client = new Protocol.ProviderAuth.ProviderAuthClient(channel);

        var result = await client.LoginAsync(new() { Provider = "mistral", ApiKey = "valid-key" }, Authorized());

        Assert.Equal(Protocol.AuthState.Authenticated, result.State);
        Assert.Contains("valid-key", server.Secrets.Values.Values);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-token")]
    public async Task RemoteHttpsStillRequiresTheServerToken(string? token)
    {
        using var server = new Factory { RemoteIp = "203.0.113.10" };
        using var channel = server.CreateChannel("https://server.example");
        var client = new Protocol.ProviderAuth.ProviderAuthClient(channel);
        var headers = new Metadata();
        if (token is not null)
        {
            headers.Add("authorization", "Bearer " + token);
        }

        var exception = await Assert.ThrowsAsync<RpcException>(() =>
            client.LoginAsync(new() { Provider = "mistral", ApiKey = "valid-key" }, headers).ResponseAsync);

        Assert.Equal(StatusCode.Unauthenticated, exception.StatusCode);
        Assert.Empty(server.Secrets.Values);
    }

    private static CallOptions Authorized() => new(
        new Metadata { { "authorization", "Bearer server-token" } }, deadline: DateTime.UtcNow.AddSeconds(15));

    private static Factory WithAuthStatus(ProviderAuthStatus status) => new()
    {
        ConfigureTestServices = services =>
        {
            services.RemoveAll<IAuthService>();
            services.AddSingleton<IAuthService>(new FixedAuthService(status));
        }
    };

    private static Factory WithBrowserStatus(BrowserAttemptStatus status) => new()
    {
        ConfigureTestServices = services =>
        {
            services.RemoveAll<IBrowserAuthService>();
            services.AddSingleton<IBrowserAuthService>(new FixedBrowserAuthService(status));
        }
    };

    private static BrowserAttemptStatus Attempt(BrowserAttemptState state) => new(
        "attempt-id", "https://console.mistral.ai/login", DateTimeOffset.UtcNow.AddMinutes(5), state, "Browser status.");

    private sealed class FixedAuthService(ProviderAuthStatus status) : IAuthService
    {
        public Task<ProviderAuthStatus> LoginAsync(string provider, string apiKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(status);

        public Task<ProviderAuthStatus> GetStatusAsync(string provider, CancellationToken cancellationToken = default) =>
            Task.FromResult(status);

        public Task LogoutAsync(string provider, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FixedBrowserAuthService(BrowserAttemptStatus status) : IBrowserAuthService
    {
        public Task<BrowserAttemptStatus> StartAsync(string provider, CancellationToken cancellationToken = default) =>
            Task.FromResult(status);

        public Task<BrowserAttemptStatus> PollAsync(string attemptId, CancellationToken cancellationToken = default) =>
            Task.FromResult(status);

        public Task<BrowserAttemptStatus> CancelAsync(string attemptId, CancellationToken cancellationToken = default) =>
            Task.FromResult(status);

        public Task CancelProviderAsync(string provider, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string? _temporaryDirectory;
        private readonly string _database;
        public Secrets Secrets { get; }
        public BrowserFlow Browser { get; } = new();
        public List<string> Responses { get; } = [];
        public Action<IServiceCollection>? ConfigureTestServices { get; set; }
        public string? AccessToken { get; init; } = "server-token";
        public string? RemoteIp { get; init; } = "127.0.0.1";
        public Factory(string? database = null, Secrets? secrets = null)
        {
            if (database is null)
            {
                _temporaryDirectory = Path.Combine(Path.GetTempPath(), "vibere-server-test-" + Guid.NewGuid());
                Directory.CreateDirectory(_temporaryDirectory);
                database = Path.Combine(_temporaryDirectory, "server.db");
            }
            _database = database;
            Secrets = secrets ?? new Secrets();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:VibeRe"] = $"Data Source={_database};Pooling=False",
                ["Server:AccessToken"] = AccessToken,
                ["Logging:LogLevel:Default"] = "Warning"
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISecretStore>();
                services.RemoveAll<IAuthFlow>();
                services.RemoveAll<IBrowserAuthFlow>();
                services.AddSingleton<ISecretStore>(Secrets);
                services.AddSingleton<IAuthFlow, Flow>();
                services.AddSingleton<IBrowserAuthFlow>(Browser);
                // TestServer has no socket peer. Supply one explicitly, including hostile cases.
                services.AddSingleton<IStartupFilter>(new RemoteIpFilter(
                    RemoteIp is { } address ? IPAddress.Parse(address) : null));
                ConfigureTestServices?.Invoke(services);
            });
        }

        public GrpcChannel CreateChannel(string address = "http://localhost", string? host = null) =>
            GrpcChannel.ForAddress(address, new GrpcChannelOptions
            {
                HttpHandler = new RecordingHandler(Server.CreateHandler(), Responses, host)
            });

        public IVibeReClient Connect(string? token = "server-token") =>
            new VibeReClient("http://localhost", token, new RecordingHandler(Server.CreateHandler(), Responses));

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && _temporaryDirectory is not null && Directory.Exists(_temporaryDirectory))
                Directory.Delete(_temporaryDirectory, true);
        }
    }

    private sealed class RemoteIpFilter(IPAddress? address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, continuation) =>
            {
                context.Connection.RemoteIpAddress = address;
                return continuation(context);
            });
            next(app);
        };
    }

    private sealed class RecordingHandler(HttpMessageHandler inner, List<string> responses, string? host = null) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (host is not null)
            {
                request.Headers.Host = host;
            }

            var response = await base.SendAsync(request, cancellationToken);
            // Inspect the actual transport payload so application mapping cannot hide leaked secrets.
            responses.Add(Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync(cancellationToken)));
            return response;
        }
    }

    private sealed class Secrets : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public bool Fail { get; set; }
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken)
        {
            if (Fail) throw new IOException("sensitive-storage-value");
            return Task.FromResult(Values.GetValueOrDefault(key));
        }
        public Task SetAsync(string key, string value, CancellationToken cancellationToken) { Values[key] = value; return Task.CompletedTask; }
        public Task DeleteAsync(string key, CancellationToken cancellationToken) { Values.Remove(key); return Task.CompletedTask; }
    }

    private sealed class Flow : IAuthFlow
    {
        public string ProviderName => "mistral";
        public Uri BaseUri => new("https://api.mistral.ai/");
        public Task<ProviderAuthStatus> ValidateAsync(string apiKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderAuthStatus(apiKey == "bad-key" ? ProviderAuthState.InvalidCredential
                : ProviderAuthState.Authenticated, "Verified by test provider."));
    }

    private sealed class BrowserFlow : IBrowserAuthFlow
    {
        public string? LastVerifier { get; private set; }
        public string ProviderName => "mistral";
        public Task<BrowserProcess> StartAsync(string challenge, CancellationToken cancellationToken) =>
            Task.FromResult(new BrowserProcess("process", new("https://console.mistral.ai/login"),
                new("https://console.mistral.ai/api/poll"), DateTimeOffset.UtcNow.AddMinutes(5)));
        public Task<BrowserPoll> PollAsync(Uri pollUri, CancellationToken cancellationToken) => Task.FromResult(new BrowserPoll(BrowserPollState.Completed, "exchange"));
        public Task<string> ExchangeAsync(string processId, string exchangeToken, string verifier, CancellationToken cancellationToken)
        {
            LastVerifier = verifier;
            return Task.FromResult("browser-key");
        }
    }
}
