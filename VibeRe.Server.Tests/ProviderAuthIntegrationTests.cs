using System.Text;
using Grpc.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VibeRe.Server.Application.Providers.Auth;
using VibeRe.Server.Application.Secrets;
using VibeRe.Client.Application;
using VibeRe.Client.Infrastructure;

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
        using var cli = first.Connect();
        using var desktop = first.Connect();
        using var otherServer = second.Connect();
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
        using var client = server.Connect();
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
        using var client = server.Connect();
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
        using var client = server.Connect();
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
        using var client = server.Connect(token);
        var error = await Assert.ThrowsAsync<ServerConnectionException>(() => client.LoginAsync("valid-key"));
        Assert.Equal(StatusCode.Unauthenticated, Assert.IsType<RpcException>(error.InnerException).StatusCode);
        Assert.Empty(server.Secrets.Values);
    }

    [Fact]
    public async Task InvalidRequestAndStorageFailuresAreSafeRpcErrors()
    {
        using var server = new Factory();
        using var client = server.Connect();
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
            using (var client = first.Connect()) await client.LoginAsync("persistent-key");
            using var restarted = new Factory(database, secrets);
            using var reconnect = restarted.Connect();
            Assert.Equal(AuthState.Authenticated, (await reconnect.GetStatusAsync()).State);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string? _temporaryDirectory;
        private readonly string _database;
        public Secrets Secrets { get; }
        public BrowserFlow Browser { get; } = new();
        public List<string> Responses { get; } = [];
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
                ["Server:AccessToken"] = "server-token",
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
            });
        }

        public IVibeReClient Connect(string? token = "server-token") =>
            new VibeReClient("http://localhost", token, new RecordingHandler(Server.CreateHandler(), Responses));

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && _temporaryDirectory is not null && Directory.Exists(_temporaryDirectory))
                Directory.Delete(_temporaryDirectory, true);
        }
    }

    private sealed class RecordingHandler(HttpMessageHandler inner, List<string> responses) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
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
        public Task<BrowserPoll> PollAsync(Uri pollUri, CancellationToken cancellationToken) => Task.FromResult(new BrowserPoll("completed", "exchange"));
        public Task<string> ExchangeAsync(string processId, string exchangeToken, string verifier, CancellationToken cancellationToken)
        {
            LastVerifier = verifier;
            return Task.FromResult("browser-key");
        }
    }
}
