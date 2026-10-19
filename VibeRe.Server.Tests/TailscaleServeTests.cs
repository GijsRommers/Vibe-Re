using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using VibeRe.Contracts;
using VibeRe.Server.Application.Access;
using VibeRe.Server.Services;

namespace VibeRe.Server.Tests;

public class TailscaleServeTests
{
    private const string Host = "workstation.example-tailnet.ts.net";
    private const string Origin = "https://" + Host;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task ProxyTrustIsDisabledWithoutAnExplicitOrigin(string? origin)
    {
        var context = ServedRequest();
        await AssertRejectedAsync(context, Settings(origin));
    }

    [Theory]
    [InlineData("http://workstation.example-tailnet.ts.net")]
    [InlineData("wss://workstation.example-tailnet.ts.net")]
    [InlineData("https://example.com")]
    [InlineData("https://ts.net")]
    [InlineData("https://workstation.ts.net.attacker.example")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://[::1]")]
    [InlineData("https://user:password@workstation.example-tailnet.ts.net")]
    [InlineData("https://workstation.example-tailnet.ts.net/path")]
    [InlineData("https://workstation.example-tailnet.ts.net?query=1")]
    [InlineData("https://workstation.example-tailnet.ts.net#fragment")]
    [InlineData("https://workstation.example-tailnet.ts.net:0")]
    [InlineData("https://workstation.example-tailnet.ts.net:65536")]
    [InlineData("//workstation.example-tailnet.ts.net")]
    [InlineData("not an origin")]
    public void TrustConfigurationRequiresOneHttpsTsNetOrigin(string origin)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Middleware(Settings(origin)));
        Assert.Contains("Server:TailscaleOrigin", error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://localhost:5067")]
    [InlineData("http://LOCALHOST:5067")]
    [InlineData("http://127.0.0.1:5067")]
    [InlineData("http://127.0.0.2:5067")]
    [InlineData("http://[::1]:5067")]
    [InlineData("http://[::ffff:127.0.0.1]:5067")]
    [InlineData("http://127.0.0.1:5067;https://[::1]:5068")]
    public void TrustConfigurationAcceptsOnlyLoopbackListeners(string? listeners)
    {
        Assert.NotNull(Middleware(Settings(listeners: listeners)));
    }

    [Theory]
    [InlineData("http://0.0.0.0:5067")]
    [InlineData("http://[::]:5067")]
    [InlineData("http://*:5067")]
    [InlineData("http://+:5067")]
    [InlineData("http://192.168.1.10:5067")]
    [InlineData("http://100.101.102.103:5067")]
    [InlineData("http://server.example:5067")]
    // Uri.IsLoopback recognizes this alias, but Kestrel treats DNS bindings other than localhost as wildcard.
    [InlineData("http://loopback:5067")]
    [InlineData("http://LOOPBACK:5067/")]
    [InlineData("http://localhost:5067;http://0.0.0.0:5068")]
    [InlineData("ftp://localhost:5067")]
    [InlineData("http://user@localhost:5067")]
    [InlineData("http://localhost:5067/path")]
    [InlineData("http://localhost:5067?query=1")]
    [InlineData("http://localhost:5067#fragment")]
    [InlineData("not a listener")]
    public void TrustConfigurationRejectsEveryUnsafeListener(string listeners)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Middleware(Settings(listeners: listeners)));
        Assert.Contains("loopback", error.Message);
    }

    [Theory]
    [InlineData("http://localhost:5068", true)]
    [InlineData("http://[::1]:5068", true)]
    [InlineData("http://0.0.0.0:5068", false)]
    [InlineData("http://loopback:5068", false)]
    [InlineData(null, false)]
    public void KestrelEndpointsAreValidatedAlongsideUrls(string? endpointUrl, bool allowed)
    {
        var settings = Settings(extra: new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:Backend:Url"] = endpointUrl,
            ["Kestrel:Endpoints:Backend:Protocols"] = "Http1AndHttp2"
        });
        if (allowed) Assert.NotNull(Middleware(settings));
        else Assert.Throws<InvalidOperationException>(() => Middleware(settings));
    }

    [Theory]
    [InlineData("127.0.0.1", "100.101.102.103")]
    [InlineData("::1", "fd7a:115c:a1e0::1234")]
    [InlineData("::ffff:127.0.0.1", "127.0.0.1")]
    public async Task TrustedServeSetsHttpsWithoutChangingPeerOrGrantingLocalAdmin(string peer, string forwardedIp)
    {
        var context = ServedRequest(peer);
        context.Request.Headers["X-Forwarded-For"] = forwardedIp;
        context.Request.Headers["Tailscale-User-Login"] = "administrator@example.com";
        context.Request.Headers["Tailscale-User-Name"] = "Local administrator";
        var initialCookieName = ClientAccessPolicy.CookieName(context);
        var invoked = false;
        await Middleware(Settings(), async received =>
        {
            invoked = true;
            Assert.True(received.Request.IsHttps);
            Assert.Equal(Host, received.Request.Host.Host);
            Assert.Equal(IPAddress.Parse(peer), received.Connection.RemoteIpAddress);
            Assert.Equal(forwardedIp, received.Request.Headers["X-Forwarded-For"].ToString());
            Assert.Equal("https", received.Request.Headers["X-Forwarded-Proto"].ToString());
            Assert.False(ClientAccessPolicy.IsLocal(received));
            Assert.True(ClientAccessPolicy.IsSafeConnection(received));
            var identity = await new ClientAccessPolicy(Settings(), new NoDeviceAccess()).ResolveAsync(received);
            Assert.False(identity.Authorized);
            Assert.False(identity.IsAdministrator);
            Assert.False(ClientAccessPolicy.CanManage(received, identity));
            Assert.StartsWith("__Host-vibere-device-", ClientAccessPolicy.CookieName(received));
            Assert.NotEqual(initialCookieName, ClientAccessPolicy.CookieName(received));
        }).InvokeAsync(context);
        Assert.True(invoked);
    }

    [Theory]
    [InlineData(Origin, Host)]
    [InlineData(Origin + ":443/", Host + ":443")]
    [InlineData("https://WORKSTATION.EXAMPLE-TAILNET.TS.NET", "WORKSTATION.EXAMPLE-TAILNET.TS.NET")]
    [InlineData(Origin + ":8443", Host + ":8443")]
    public async Task TrustedAuthorityAllowsCaseAndExactConfiguredPort(string origin, string authority)
    {
        var context = ServedRequest();
        context.Request.Host = new HostString(authority);
        context.Request.Headers["X-Forwarded-Host"] = authority;
        context.Request.Headers.Origin = origin;
        var invoked = false;
        await Middleware(Settings(origin), received =>
        {
            invoked = true;
            Assert.True(ClientAccessPolicy.IsSafeConnection(received));
            return Task.CompletedTask;
        }).InvokeAsync(context);
        Assert.True(invoked);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("203.0.113.19")]
    [InlineData("100.101.102.103")]
    [InlineData("192.168.1.10")]
    [InlineData("::ffff:203.0.113.19")]
    [InlineData("fd7a:115c:a1e0::1234")]
    public async Task CorrectHeadersCannotMakeANonloopbackOrUnknownPeerTrusted(string? peer)
    {
        await AssertRejectedAsync(ServedRequest(peer));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("localhost:5067")]
    [InlineData("127.0.0.1:5067")]
    [InlineData("attacker.example")]
    [InlineData(Host + ":444")]
    [InlineData(Host + "/")]
    [InlineData(Host + "\\")]
    [InlineData(Host + "/child/..")]
    [InlineData("user@" + Host)]
    [InlineData(Host + "?query=1")]
    [InlineData(Host + "#fragment")]
    [InlineData(" " + Host)]
    [InlineData(Host + " ")]
    [InlineData(Host + ", " + Host)]
    public async Task BothHostAndForwardedHostMustBeExactAuthorities(string authority)
    {
        var wrongHost = ServedRequest();
        // Assign the raw wire header so HostString's encoder cannot normalize or reject the test input first.
        wrongHost.Request.Headers.Host = authority;
        await AssertRejectedAsync(wrongHost);

        var wrongForwardedHost = ServedRequest();
        wrongForwardedHost.Request.Headers["X-Forwarded-Host"] = authority;
        await AssertRejectedAsync(wrongForwardedHost);
    }

    [Theory]
    [InlineData("X-Forwarded-Host")]
    [InlineData("X-Forwarded-Proto")]
    [InlineData("X-Forwarded-For")]
    public async Task MissingEmptyRepeatedAndCommaJoinedProxyHeadersAreRejected(string header)
    {
        var missing = ServedRequest();
        missing.Request.Headers.Remove(header);
        await AssertRejectedAsync(missing);

        var empty = ServedRequest();
        empty.Request.Headers[header] = "";
        await AssertRejectedAsync(empty);

        var repeated = ServedRequest();
        var value = repeated.Request.Headers[header].ToString();
        repeated.Request.Headers[header] = new StringValues([value, value]);
        await AssertRejectedAsync(repeated);

        var joined = ServedRequest();
        joined.Request.Headers[header] = value + ", " + value;
        await AssertRejectedAsync(joined);
    }

    [Theory]
    [InlineData("X-Forwarded-Proto", "http")]
    [InlineData("X-Forwarded-Proto", "HTTPS")]
    [InlineData("X-Forwarded-Proto", "https ")]
    [InlineData("X-Forwarded-For", "not-an-ip")]
    [InlineData("X-Forwarded-For", "100.101.102.103:1234")]
    [InlineData("X-Forwarded-For", "unknown")]
    [InlineData("Forwarded", "for=100.101.102.103;proto=https")]
    [InlineData("Forwarded", "")]
    [InlineData("Tailscale-Funnel-Request", "true")]
    [InlineData("Tailscale-Funnel-Request", "false")]
    [InlineData("Tailscale-Funnel-Request", "")]
    public async Task MalformedMixedProxyAndFunnelRequestsAreRejected(string header, string value)
    {
        var context = ServedRequest();
        context.Request.Headers[header] = value;
        await AssertRejectedAsync(context);
    }

    [Theory]
    [InlineData("Forwarded")]
    [InlineData("forwarded")]
    [InlineData("X-Forwarded-For")]
    [InlineData("x-forwarded-unknown")]
    [InlineData("Tailscale-User-Login")]
    [InlineData("tailscale-unknown")]
    public async Task SuppliedProxyHeadersNeverPermitLocalhostAdminFallback(string header)
    {
        foreach (var origin in new string?[] { null, Origin })
        {
            var context = LocalRequest();
            context.Request.Headers[header] = "spoofed";
            Assert.False(ClientAccessPolicy.IsLocal(context));
            var identity = await new ClientAccessPolicy(Settings(origin), new NoDeviceAccess()).ResolveAsync(context);
            Assert.False(identity.Authorized);
            await AssertRejectedAsync(context, Settings(origin));
        }
    }

    [Theory]
    [InlineData(null, "127.0.0.1", "localhost:5067")]
    [InlineData(Origin, "127.0.0.1", "localhost:5067")]
    [InlineData(Origin, "::1", "[::1]:5067")]
    [InlineData(Origin, "::ffff:127.0.0.1", "127.0.0.1:5067")]
    public async Task DirectLoopbackAdminRemainsAvailableWithOrWithoutServe(string? origin, string peer, string host)
    {
        var context = LocalRequest();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Host = new HostString(host);
        var invoked = false;
        await Middleware(Settings(origin), async received =>
        {
            invoked = true;
            Assert.Equal("http", received.Request.Scheme);
            Assert.Equal(host, received.Request.Host.Value);
            Assert.True(ClientAccessPolicy.IsSafeConnection(received));
            var identity = await new ClientAccessPolicy(Settings(origin), new NoDeviceAccess()).ResolveAsync(received);
            Assert.True(identity.IsAdministrator);
            Assert.True(ClientAccessPolicy.CanManage(received, identity));
        }).InvokeAsync(context);
        Assert.True(invoked);
    }

    [Fact]
    public async Task ServedPairingGetsSecureHostOnlyCookieAndCannotManageLocalDevices()
    {
        using var fixture = new SignalRTestServer(token: null);
        using var server = WithServe(fixture);
        using var remote = new TestBrowser(server, served: true);
        using var local = new TestBrowser(server, served: false);

        var initial = await remote.StatusAsync();
        Assert.False(initial.Authorized);
        Assert.False(initial.IsLocal);
        Assert.False(initial.CanManageDevices);
        using (var unauthenticated = await remote.SendAsync(HttpMethod.Post,
            ServerProtocol.AuthHubPath + "/negotiate?negotiateVersion=1"))
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        using var begin = await remote.MutateAsync("/api/access/pairings", new BeginPairingRequest("Tailnet browser"));
        Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        var pairing = (await begin.Content.ReadFromJsonAsync<PairingReply>())!;
        using (var approve = await local.MutateAsync("/api/access/pairings/approve", new ApprovePairingRequest(pairing.ApprovalCode)))
            Assert.Equal(HttpStatusCode.NoContent, approve.StatusCode);
        using var redeem = await remote.MutateAsync("/api/access/pairings/redeem",
            new RedeemPairingRequest(pairing.PairingId, pairing.PairingSecret));
        Assert.Equal(HttpStatusCode.NoContent, redeem.StatusCode);
        var cookie = Assert.Single(redeem.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("__Host-vibere-device-", cookie);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);

        var paired = await remote.StatusAsync();
        Assert.True(paired.Authorized);
        Assert.Equal("Tailnet browser", paired.DeviceName);
        Assert.False(paired.IsLocal);
        Assert.False(paired.CanManageDevices);
        using (var negotiate = await remote.SendAsync(HttpMethod.Post,
            ServerProtocol.AuthHubPath + "/negotiate?negotiateVersion=1"))
            Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);
        using (var devices = await remote.SendAsync(HttpMethod.Get, "/api/access/devices"))
            Assert.Equal(HttpStatusCode.Forbidden, devices.StatusCode);
        using (var bootstrap = await remote.MutateAsync("/api/access/local-session"))
            Assert.Equal(HttpStatusCode.Forbidden, bootstrap.StatusCode);
        using (var foreignOrigin = await remote.SendAsync(HttpMethod.Get, "/api/access/status", origin: "https://attacker.example"))
            Assert.Equal(HttpStatusCode.Forbidden, foreignOrigin.StatusCode);

        var administrator = await local.StatusAsync();
        Assert.True(administrator.Authorized);
        Assert.True(administrator.IsLocal);
        Assert.True(administrator.CanManageDevices);
        using var devicesResponse = await local.SendAsync(HttpMethod.Get, "/api/access/devices");
        var registeredDevice = Assert.Single((await devicesResponse.Content.ReadFromJsonAsync<DeviceReply[]>())!);
        using (var revoked = await local.MutateAsync($"/api/access/devices/{registeredDevice.Id}", method: HttpMethod.Delete))
            Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.False((await remote.StatusAsync()).Authorized);
        using (var replay = await remote.SendAsync(HttpMethod.Post,
            ServerProtocol.AuthHubPath + "/negotiate?negotiateVersion=1"))
            Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Empty(fixture.Secrets.Values);
    }

    [Fact]
    public async Task ServeIdentityHeadersDoNotAuthorizeAndEvenRootBearerCannotManageLocally()
    {
        using var fixture = new SignalRTestServer();
        using var server = WithServe(fixture);
        using var remote = new TestBrowser(server, served: true);
        using (var status = await remote.SendAsync(HttpMethod.Get, "/api/access/status", identity: "administrator@example.com"))
        {
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
            Assert.False((await status.Content.ReadFromJsonAsync<AccessStatusReply>())!.Authorized);
        }
        using (var status = await remote.SendAsync(HttpMethod.Get, "/api/access/status", token: "server-token"))
        {
            var identity = (await status.Content.ReadFromJsonAsync<AccessStatusReply>())!;
            Assert.True(identity.Authorized);
            Assert.False(identity.IsLocal);
            Assert.False(identity.CanManageDevices);
        }
        using var devices = await remote.SendAsync(HttpMethod.Get, "/api/access/devices", token: "server-token");
        Assert.Equal(HttpStatusCode.Forbidden, devices.StatusCode);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("server-token", true)]
    public async Task RealLoopbackKestrelWebSocketStillRequiresCredentialsThroughServe(string? token, bool authorized)
    {
        using var fixture = new SignalRTestServer();
        using var server = WithServe(fixture);
        server.UseKestrel(0);
        using var http = server.CreateClient();
        // The TCP connection stays on loopback; only the HTTP authority and proxy headers emulate Serve.
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(http.BaseAddress!, ServerProtocol.AuthHubPath), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                options.AccessTokenProvider = () => Task.FromResult(token);
                options.Headers["Host"] = Host;
                options.Headers["Origin"] = Origin;
                options.Headers["X-Forwarded-Host"] = Host;
                options.Headers["X-Forwarded-Proto"] = "https";
                options.Headers["X-Forwarded-For"] = "100.101.102.103";
            }).Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (authorized)
        {
            await hub.StartAsync(timeout.Token);
            var status = await hub.InvokeAsync<AuthStatusReply>("GetStatus", timeout.Token);
            Assert.Equal(AuthState.SignedOut, status.State);
        }
        else
        {
            var error = await Record.ExceptionAsync(() => hub.StartAsync(timeout.Token));
            Assert.NotNull(error);
            Assert.Contains("401", error.Message);
            Assert.False(timeout.IsCancellationRequested, "The handshake must be rejected by authentication, not by timeout.");
            Assert.Equal(HubConnectionState.Disconnected, hub.State);
        }
        Assert.NotEmpty(fixture.HubRequests);
        Assert.Empty(fixture.Secrets.Values);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionPipelineRejectsProxyAndTailscaleHeadersOnLocalhost(bool enabled)
    {
        using var fixture = new SignalRTestServer(token: null);
        using var server = enabled ? WithServe(fixture) : fixture.WithWebHostBuilder(_ => { });
        using var http = server.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        foreach (var header in new[] { "Forwarded", "X-Forwarded-For", "Tailscale-User-Login" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/access/status");
            request.Headers.TryAddWithoutValidation(header, "spoofed");
            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }
        using var direct = await http.GetAsync("/api/access/status");
        var identity = (await direct.Content.ReadFromJsonAsync<AccessStatusReply>())!;
        Assert.True(identity.Authorized);
        Assert.True(identity.IsLocal);
        Assert.True(identity.CanManageDevices);
    }

    private static IConfiguration Settings(string? origin = Origin, string? listeners = null,
        Dictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Server:TailscaleOrigin"] = origin,
            ["urls"] = listeners
        };
        foreach (var (key, value) in extra ?? []) values[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static TailscaleServeMiddleware Middleware(IConfiguration configuration, RequestDelegate? next = null) =>
        new(next ?? (_ => Task.CompletedTask), configuration);

    private static DefaultHttpContext LocalRequest()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost:5067");
        return context;
    }

    private static DefaultHttpContext ServedRequest(string? peer = "127.0.0.1")
    {
        var context = LocalRequest();
        context.Connection.RemoteIpAddress = peer is null ? null : IPAddress.Parse(peer);
        context.Request.Host = new HostString(Host);
        context.Request.Headers["X-Forwarded-Host"] = Host;
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Request.Headers["X-Forwarded-For"] = "100.101.102.103";
        context.Request.Headers.Origin = Origin;
        return context;
    }

    private static async Task AssertRejectedAsync(HttpContext context, IConfiguration? configuration = null)
    {
        var originalScheme = context.Request.Scheme;
        var originalHost = context.Request.Host;
        var invoked = false;
        await Middleware(configuration ?? Settings(), _ =>
        {
            invoked = true;
            return Task.CompletedTask;
        }).InvokeAsync(context);
        Assert.False(invoked);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal(originalScheme, context.Request.Scheme);
        Assert.Equal(originalHost, context.Request.Host);
    }

    private static WebApplicationFactory<Program> WithServe(SignalRTestServer server) =>
        server.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Server:TailscaleOrigin"] = Origin,
                ["urls"] = "http://127.0.0.1:5067"
            })));

    private sealed class TestBrowser : IDisposable
    {
        private readonly HttpClient _http;
        private readonly bool _served;
        private readonly Uri _externalOrigin;
        private readonly CookieContainer _cookies = new();

        public TestBrowser(WebApplicationFactory<Program> server, bool served)
        {
            _served = served;
            _externalOrigin = new Uri(served ? Origin : "http://localhost");
            _http = server.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri(served ? "http://" + Host : "http://localhost"),
                HandleCookies = false,
                AllowAutoRedirect = false
            });
        }

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null,
            string? csrf = null, string? origin = null, string? token = null, string? identity = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (_served)
            {
                request.Headers.TryAddWithoutValidation("X-Forwarded-Host", Host);
                request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
                request.Headers.TryAddWithoutValidation("X-Forwarded-For", "100.101.102.103");
            }
            request.Headers.TryAddWithoutValidation("Origin", origin ?? _externalOrigin.GetLeftPart(UriPartial.Authority));
            var cookies = _cookies.GetCookieHeader(_externalOrigin);
            if (cookies.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", cookies);
            if (csrf is not null) request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf);
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (identity is not null) request.Headers.TryAddWithoutValidation("Tailscale-User-Login", identity);
            if (body is not null) request.Content = JsonContent.Create(body);
            var response = await _http.SendAsync(request);
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
                foreach (var cookie in setCookies) _cookies.SetCookies(_externalOrigin, cookie);
            return response;
        }

        public async Task<AccessStatusReply> StatusAsync()
        {
            using var response = await SendAsync(HttpMethod.Get, "/api/access/status");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<AccessStatusReply>())!;
        }

        public async Task<HttpResponseMessage> MutateAsync(string path, object? body = null, HttpMethod? method = null)
        {
            using var csrf = await SendAsync(HttpMethod.Get, "/api/access/csrf");
            Assert.Equal(HttpStatusCode.OK, csrf.StatusCode);
            if (_served && csrf.Headers.TryGetValues("Set-Cookie", out var cookies))
                Assert.All(cookies, cookie =>
                    Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase));
            using var document = JsonDocument.Parse(await csrf.Content.ReadAsStringAsync());
            return await SendAsync(method ?? HttpMethod.Post, path, body,
                document.RootElement.GetProperty("requestToken").GetString());
        }

        public void Dispose() => _http.Dispose();
    }

    // Resolving a tokenless request must not consult or mutate the device store.
    private sealed class NoDeviceAccess : IDeviceAccessService
    {
        public Task<DevicePairingRequest> BeginPairingAsync(string deviceName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> ApprovePairingAsync(string approvalCode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DevicePairingRedemption> RedeemPairingAsync(string pairingId, string pairingSecret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DeviceInfo?> ValidateSessionAsync(string token, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RevokeDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
