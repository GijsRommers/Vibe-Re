using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using VibeRe.Contracts;
using VibeRe.Server.Application.Providers.Auth;
using VibeRe.Server.Application.Access;

namespace VibeRe.Server.Tests;

public class DeviceAccessIntegrationTests
{
    private const string RootToken = "server-token";
    private const string RemoteOrigin = "https://server.example";

    [Fact]
    public async Task BrowserPairingWaitsForLocalApprovalAndReturnsOnlyARestrictedCookie()
    {
        using var server = new SignalRTestServer();
        using var browser = new AccessClient(server, RemoteOrigin);
        using var admin = new AccessClient(server, token: RootToken);

        var initial = await browser.StatusAsync();
        Assert.False(initial.Authorized);
        Assert.False(initial.CanManageDevices);
        Assert.False(initial.IsLocal);
        var pairing = await browser.BeginPairingAsync("Living room tablet");
        using (var pending = await browser.RedeemAsync(pairing))
        {
            Assert.Equal(HttpStatusCode.Accepted, pending.StatusCode);
            Assert.False(pending.Headers.Contains("Set-Cookie"));
        }
        Assert.False((await browser.StatusAsync()).Authorized);
        Assert.Empty(await admin.DevicesAsync());

        await admin.ApproveAsync(pairing);
        using var redeemed = await browser.RedeemAsync(pairing);
        Assert.Equal(HttpStatusCode.NoContent, redeemed.StatusCode);
        Assert.Empty(await redeemed.Content.ReadAsStringAsync());
        var cookie = DeviceCookie(redeemed);
        var attributes = cookie.Split(';', StringSplitOptions.TrimEntries);
        Assert.StartsWith("__Host-vibere-device-", attributes[0], StringComparison.Ordinal);
        Assert.Contains(attributes, item => item.Equals("httponly", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(attributes, item => item.Equals("secure", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(attributes, item => item.Equals("samesite=strict", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(attributes, item => item.Equals("path=/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(attributes, item => item.StartsWith("domain=", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(browser.Cookies.GetCookieHeader(new Uri("https://sub.server.example")));
        Assert.Empty(browser.Cookies.GetCookieHeader(new Uri("http://server.example")));

        var status = await browser.StatusAsync();
        Assert.True(status.Authorized);
        Assert.False(status.CanManageDevices);
        Assert.Equal("Living room tablet", status.DeviceName);
        Assert.NotNull(status.ExpiresAt);
        var device = Assert.Single(await admin.DevicesAsync());
        Assert.Equal(status.DeviceName, device.Name);
        Assert.Equal(status.ExpiresAt, device.ExpiresAt);
        Assert.Null(device.RevokedAt);
        var credential = CookieValue(cookie);
        Assert.All(browser.Bodies.Concat(admin.Bodies), body => Assert.DoesNotContain(credential, body));
        Assert.Empty(server.Secrets.Values);
        Assert.Equal(0, server.Browser.StartCount);
    }

    [Fact]
    public async Task PairingProofIsRequiredAndSuccessfulRedemptionIsSingleUse()
    {
        using var server = new SignalRTestServer();
        using var browser = new AccessClient(server, RemoteOrigin);
        using var attacker = new AccessClient(server, RemoteOrigin);
        using var admin = new AccessClient(server, token: RootToken);
        var pairing = await browser.BeginPairingAsync("Tablet");
        await admin.ApproveAsync(pairing);
        using (var invalid = await attacker.MutateAsync(HttpMethod.Post, "/api/access/pairings/redeem",
                   new RedeemPairingRequest(pairing.PairingId, new string('a', 43))))
            Assert.Equal(HttpStatusCode.NotFound, invalid.StatusCode);
        using (var valid = await browser.RedeemAsync(pairing))
            Assert.Equal(HttpStatusCode.NoContent, valid.StatusCode);
        using (var replay = await attacker.RedeemAsync(pairing))
        {
            Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
            Assert.False(replay.Headers.Contains("Set-Cookie"));
        }
        Assert.False((await attacker.StatusAsync()).Authorized);
        Assert.Single(await admin.DevicesAsync());
    }

    [Fact]
    public async Task ExpiredPairingCannotBeApprovedOrRedeemed()
    {
        using var server = new SignalRTestServer();
        using var browser = new AccessClient(server, RemoteOrigin);
        using var admin = new AccessClient(server, token: RootToken);
        var pairing = await browser.BeginPairingAsync("Tablet");
        server.Clock.Advance(TimeSpan.FromMinutes(6));
        using (var approved = await admin.MutateAsync(HttpMethod.Post, "/api/access/pairings/approve",
                   new ApprovePairingRequest(pairing.ApprovalCode)))
            Assert.Equal(HttpStatusCode.NotFound, approved.StatusCode);
        using (var redeemed = await browser.RedeemAsync(pairing))
            Assert.Equal(HttpStatusCode.NotFound, redeemed.StatusCode);
        Assert.Empty(await admin.DevicesAsync());
    }

    [Fact]
    public async Task NativeRedemptionReturnsAProviderTokenWithoutIssuingABrowserCookie()
    {
        using var server = new SignalRTestServer();
        using var native = new AccessClient(server, RemoteOrigin, browser: false);
        using var admin = new AccessClient(server, token: RootToken);
        var pairing = await native.BeginPairingAsync("CLI laptop");
        await admin.ApproveAsync(pairing);
        using var response = await native.RedeemAsync(pairing, native: true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var grant = (await response.Content.ReadFromJsonAsync<DeviceTokenReply>())!;
        Assert.False(string.IsNullOrWhiteSpace(grant.Token));
        Assert.Equal("CLI laptop", grant.Device.Name);
        using var status = await native.SendAsync(HttpMethod.Get, "/api/access/status", token: grant.Token);
        var access = (await status.Content.ReadFromJsonAsync<AccessStatusReply>())!;
        Assert.True(access.Authorized);
        Assert.False(access.CanManageDevices);
        using var negotiate = await native.SendAsync(HttpMethod.Post,
            ServerProtocol.AuthHubPath + "/negotiate?negotiateVersion=1", token: grant.Token);
        Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);
        using var devices = await native.SendAsync(HttpMethod.Get, "/api/access/devices", token: grant.Token);
        Assert.Equal(HttpStatusCode.Forbidden, devices.StatusCode);
        Assert.Empty(server.Secrets.Values);
    }

    [Theory]
    [InlineData(RemoteOrigin)]
    [InlineData("https://attacker.example")]
    [InlineData("null")]
    public async Task NativeRedemptionRejectsEveryBrowserOriginWithoutConsumingThePairing(string origin)
    {
        using var server = new SignalRTestServer();
        using var native = new AccessClient(server, RemoteOrigin, browser: false);
        using var admin = new AccessClient(server, token: RootToken);
        var pairing = await native.BeginPairingAsync("CLI laptop");
        await admin.ApproveAsync(pairing);
        using (var rejected = await native.MutateAsync(HttpMethod.Post, "/api/access/pairings/redeem-native",
                   new RedeemPairingRequest(pairing.PairingId, pairing.PairingSecret), origin: origin))
        {
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
            Assert.False(rejected.Headers.Contains("Set-Cookie"));
        }
        using var accepted = await native.RedeemAsync(pairing, native: true);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Single(await admin.DevicesAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(RootToken)]
    public async Task LocalSessionBootstrapRequiresTheConfiguredRootCredential(string? configuredToken)
    {
        using var server = new SignalRTestServer(token: configuredToken);
        using var local = new AccessClient(server);
        var before = await local.StatusAsync();
        Assert.True(before.IsLocal);
        Assert.Equal(configuredToken is null, before.CanManageDevices);
        if (configuredToken is not null)
        {
            using var denied = await local.MutateAsync(HttpMethod.Post, "/api/access/local-session");
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using var established = await local.MutateAsync(HttpMethod.Post, "/api/access/local-session", token: configuredToken);
        Assert.Equal(HttpStatusCode.NoContent, established.StatusCode);
        Assert.Empty(await established.Content.ReadAsStringAsync());
        var cookie = DeviceCookie(established);
        Assert.StartsWith("vibere-device-", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("; secure", cookie, StringComparison.OrdinalIgnoreCase);
        var after = await local.StatusAsync();
        Assert.True(after.Authorized);
        Assert.True(after.CanManageDevices);
        Assert.True(after.IsLocal);
        Assert.NotEmpty(await local.DevicesAsync());
    }

    [Fact]
    public async Task ResetClearsRevokedLocalCookieWithoutBypassingCsrfOrAuthorization()
    {
        using var server = new SignalRTestServer(token: null);
        using var browser = new AccessClient(server);
        Assert.True((await browser.StatusAsync()).CanManageDevices);
        using (var established = await browser.MutateAsync(HttpMethod.Post, "/api/access/local-session"))
            Assert.Equal(HttpStatusCode.NoContent, established.StatusCode);
        var id = Assert.Single(await browser.DevicesAsync()).Id;
        Assert.True(await server.Services.GetRequiredService<IDeviceAccessService>().RevokeDeviceAsync(id));

        var revoked = await browser.StatusAsync();
        Assert.False(revoked.Authorized);
        Assert.True(revoked.IsLocal);
        Assert.Null(revoked.DeviceName);
        using (var unprotected = await browser.SendAsync(HttpMethod.Post, "/api/access/disconnect"))
            Assert.Equal(HttpStatusCode.BadRequest, unprotected.StatusCode);
        Assert.False((await browser.StatusAsync()).Authorized);

        using (var reset = await browser.MutateAsync(HttpMethod.Post, "/api/access/disconnect"))
            Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        var restored = await browser.StatusAsync();
        Assert.True(restored.Authorized);
        Assert.True(restored.CanManageDevices);
        Assert.Empty(server.Secrets.Values);
    }

    [Fact]
    public async Task ProviderOnlyDeviceCannotApproveListRevokeOrBootstrapEvenOnLoopback()
    {
        using var server = new SignalRTestServer();
        using var device = new AccessClient(server);
        using var admin = new AccessClient(server, token: RootToken);
        await PairBrowserAsync(device, admin);
        var pending = await device.BeginPairingAsync("Another browser");
        var id = Assert.Single(await admin.DevicesAsync()).Id;
        Assert.True((await device.StatusAsync()).Authorized);
        Assert.False((await device.StatusAsync()).CanManageDevices);
        using (var list = await device.SendAsync(HttpMethod.Get, "/api/access/devices"))
            Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        using (var approve = await device.MutateAsync(HttpMethod.Post, "/api/access/pairings/approve",
                   new ApprovePairingRequest(pending.ApprovalCode)))
            Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);
        using (var revoke = await device.MutateAsync(HttpMethod.Delete, $"/api/access/devices/{id}"))
            Assert.Equal(HttpStatusCode.Forbidden, revoke.StatusCode);
        using (var bootstrap = await device.MutateAsync(HttpMethod.Post, "/api/access/local-session"))
            Assert.Equal(HttpStatusCode.Forbidden, bootstrap.StatusCode);
        Assert.Null(Assert.Single(await admin.DevicesAsync()).RevokedAt);
        using var redeem = await device.RedeemAsync(pending);
        Assert.Equal(HttpStatusCode.Accepted, redeem.StatusCode);
    }

    [Fact]
    public async Task LocalAdminCookieIsOriginScopedAndItsCredentialCannotAdministerARemoteConnection()
    {
        using var server = new SignalRTestServer();
        using var local = new AccessClient(server);
        using var established = await local.MutateAsync(HttpMethod.Post, "/api/access/local-session", token: RootToken);
        Assert.Equal(HttpStatusCode.NoContent, established.StatusCode);
        Assert.True((await local.StatusAsync()).CanManageDevices);
        using var remote = new AccessClient(server, RemoteOrigin);
        var cookie = DeviceCookie(established);
        remote.Cookies.SetCookies(remote.Address, cookie);
        var status = await remote.StatusAsync();
        Assert.False(status.Authorized);
        Assert.False(status.IsLocal);
        Assert.False(status.CanManageDevices);
        // Even explicitly replaying the administrator credential as a native header cannot
        // bypass the additional local-connection requirement for device administration.
        var credential = CookieValue(cookie);
        using var replay = await remote.SendAsync(HttpMethod.Get, "/api/access/status", token: credential);
        var replayStatus = (await replay.Content.ReadFromJsonAsync<AccessStatusReply>())!;
        Assert.True(replayStatus.Authorized);
        Assert.False(replayStatus.CanManageDevices);
        using var list = await remote.SendAsync(HttpMethod.Get, "/api/access/devices", token: credential);
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        using var bootstrap = await remote.MutateAsync(HttpMethod.Post, "/api/access/local-session", token: credential);
        Assert.Equal(HttpStatusCode.Forbidden, bootstrap.StatusCode);
    }

    [Fact]
    public async Task DeviceCookiesForDifferentPortsOnOneHostDoNotOverwriteEachOther()
    {
        using var server = new SignalRTestServer();
        using var admin = new AccessClient(server, token: RootToken);
        var jar = new CookieContainer();
        using var first = new AccessClient(server, "https://localhost:7443", cookies: jar);
        using var second = new AccessClient(server, "https://localhost:7444", cookies: jar);
        var firstCookie = await PairBrowserAsync(first, admin, "First port");
        var secondCookie = await PairBrowserAsync(second, admin, "Second port");
        Assert.NotEqual(firstCookie.Split('=', 2)[0], secondCookie.Split('=', 2)[0]);
        // Cookies are sent to both ports. Origin-specific names prevent collisions; they do
        // not make credentials confidential from other services on the same host.
        var firstPair = firstCookie.Split(';', 2)[0];
        var secondPair = secondCookie.Split(';', 2)[0];
        Assert.Contains(firstPair, jar.GetCookieHeader(first.Address));
        Assert.Contains(secondPair, jar.GetCookieHeader(first.Address));
        Assert.Contains(firstPair, jar.GetCookieHeader(second.Address));
        Assert.Contains(secondPair, jar.GetCookieHeader(second.Address));
        Assert.Equal("First port", (await first.StatusAsync()).DeviceName);
        Assert.Equal("Second port", (await second.StatusAsync()).DeviceName);
        using var disconnected = await second.MutateAsync(HttpMethod.Post, "/api/access/disconnect");
        Assert.Equal(HttpStatusCode.NoContent, disconnected.StatusCode);
        Assert.False((await second.StatusAsync()).Authorized);
        Assert.True((await first.StatusAsync()).Authorized);
    }

    [Theory]
    [InlineData("127.0.0.1", "https://server.example")]
    [InlineData("203.0.113.19", "https://localhost")]
    public async Task RemoteLegacyRootCanUseProviderHubButCannotManageDevices(string peer, string address)
    {
        using var server = new SignalRTestServer(remoteIp: peer);
        using var remote = new AccessClient(server, address, RootToken, browser: false);
        var status = await remote.StatusAsync();
        Assert.True(status.Authorized);
        Assert.False(status.IsLocal);
        Assert.False(status.CanManageDevices);
        using (var negotiate = await remote.SendAsync(HttpMethod.Post,
                   ServerProtocol.AuthHubPath + "/negotiate?negotiateVersion=1"))
            Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);
        var pairing = await remote.BeginPairingAsync("Remote CLI");
        using (var list = await remote.SendAsync(HttpMethod.Get, "/api/access/devices"))
            Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        using (var approve = await remote.MutateAsync(HttpMethod.Post, "/api/access/pairings/approve",
                   new ApprovePairingRequest(pairing.ApprovalCode)))
            Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);
        using (var revoke = await remote.MutateAsync(HttpMethod.Delete, $"/api/access/devices/{Guid.NewGuid()}"))
            Assert.Equal(HttpStatusCode.Forbidden, revoke.StatusCode);
        using (var bootstrap = await remote.MutateAsync(HttpMethod.Post, "/api/access/local-session"))
            Assert.Equal(HttpStatusCode.Forbidden, bootstrap.StatusCode);
        Assert.Empty(server.Secrets.Values);
    }

    [Theory]
    [InlineData("POST", "/api/access/pairings")]
    [InlineData("POST", "/api/access/pairings/approve")]
    [InlineData("POST", "/api/access/pairings/redeem")]
    [InlineData("POST", "/api/access/pairings/redeem-native")]
    [InlineData("POST", "/api/access/local-session")]
    [InlineData("POST", "/api/access/disconnect")]
    [InlineData("DELETE", "/api/access/devices/00000000-0000-0000-0000-000000000001")]
    public async Task EveryAccessMutationRequiresMatchingAntiforgeryCookieAndHeader(string method, string path)
    {
        using var server = new SignalRTestServer();
        using var client = new AccessClient(server, token: RootToken, browser: false);
        var body = new { deviceName = "Tablet", approvalCode = "ABCDEFGH", pairingId = "id", pairingSecret = "secret" };
        using (var missing = await client.SendAsync(new HttpMethod(method), path, body))
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        await client.CsrfAsync();
        using (var invalid = await client.SendAsync(new HttpMethod(method), path, body, csrf: "invalid-request-token"))
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Empty(server.Secrets.Values);
    }

    [Fact]
    public async Task AntiforgeryTokenFromAnotherBrowserCannotCreatePairing()
    {
        using var server = new SignalRTestServer();
        using var first = new AccessClient(server, RemoteOrigin);
        using var second = new AccessClient(server, RemoteOrigin);
        var csrf = await first.CsrfAsync();
        await second.CsrfAsync();
        using var response = await second.SendAsync(HttpMethod.Post, "/api/access/pairings",
            new BeginPairingRequest("Cross-browser attempt"), csrf: csrf);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("https://attacker.example")]
    [InlineData("https://server.example:444")]
    [InlineData("http://server.example")]
    [InlineData("null")]
    public async Task ForeignAndOpaqueOriginsCannotUseDeviceCookieForApiOrHub(string origin)
    {
        using var server = new SignalRTestServer();
        using var device = new AccessClient(server, RemoteOrigin);
        using var admin = new AccessClient(server, token: RootToken);
        await PairBrowserAsync(device, admin);
        foreach (var path in new[] { "/api/access/status", "/api/access/csrf", ServerProtocol.AuthHubPath + "?id=unknown" })
        {
            using var response = await device.SendAsync(HttpMethod.Get, path, origin: origin);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
            Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
        }
        using var mutation = await device.MutateAsync(HttpMethod.Post, "/api/access/disconnect", origin: origin);
        Assert.Equal(HttpStatusCode.Forbidden, mutation.StatusCode);
        Assert.True((await device.StatusAsync()).Authorized);
    }

    [Fact]
    public async Task InvalidAuthorizationHeaderCannotFallBackToValidDeviceCookie()
    {
        using var server = new SignalRTestServer();
        using var device = new AccessClient(server, RemoteOrigin);
        using var admin = new AccessClient(server, token: RootToken);
        await PairBrowserAsync(device, admin);
        using (var response = await device.SendAsync(HttpMethod.Get, "/api/access/status", token: "wrong-token"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False((await response.Content.ReadFromJsonAsync<AccessStatusReply>())!.Authorized);
        }
        using var negotiate = await device.SendAsync(HttpMethod.Post,
            ServerProtocol.AuthHubPath + "/negotiate?negotiateVersion=1", token: "wrong-token");
        Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);
        Assert.True((await device.StatusAsync()).Authorized);
    }

    [Fact]
    public async Task DurableDeviceCredentialInQueryCannotAuthorizeHubEvenWithAValidCookie()
    {
        using var server = new SignalRTestServer();
        using var device = new AccessClient(server, RemoteOrigin);
        using var admin = new AccessClient(server, token: RootToken);
        var cookie = await PairBrowserAsync(device, admin);
        using var response = await device.SendAsync(HttpMethod.Post,
            ServerProtocol.AuthHubPath + "/negotiate?negotiateVersion=1&access_token=" + CookieValue(cookie));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(CookieValue(cookie), await response.Content.ReadAsStringAsync());
        Assert.True((await device.StatusAsync()).Authorized);
    }

    [Theory]
    [InlineData("127.0.0.1", "http://server.example")]
    [InlineData("203.0.113.19", "http://localhost")]
    [InlineData("203.0.113.19", "http://192.168.1.10")]
    [InlineData(null, "http://localhost")]
    public async Task EveryAccessEndpointRejectsPlainHttpUnlessPeerAndHostAreBothLoopback(string? peer, string address)
    {
        using var server = new SignalRTestServer(remoteIp: peer);
        using var client = new AccessClient(server, address, RootToken, browser: false);
        foreach (var path in new[] { "/api/access/status", "/api/access/csrf", "/api/access/devices" })
        {
            using var response = await client.SendAsync(HttpMethod.Get, path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }
        using var mutation = await client.SendAsync(HttpMethod.Post, "/api/access/pairings", new BeginPairingRequest("LAN"));
        Assert.Equal(HttpStatusCode.Forbidden, mutation.StatusCode);
        Assert.Empty(server.Secrets.Values);
    }

    [Fact]
    public async Task DeviceCookieCannotAuthorizeAnotherServerEvenWhenExplicitlyCopied()
    {
        using var first = new SignalRTestServer();
        using var second = new SignalRTestServer();
        using var device = new AccessClient(first, RemoteOrigin);
        using var admin = new AccessClient(first, token: RootToken);
        var cookie = await PairBrowserAsync(device, admin);
        using var other = new AccessClient(second, RemoteOrigin);
        other.Cookies.SetCookies(other.Address, cookie);
        Assert.False((await other.StatusAsync()).Authorized);
        using var negotiate = await other.SendAsync(HttpMethod.Post,
            ServerProtocol.AuthHubPath + "/negotiate?negotiateVersion=1");
        Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);
        Assert.True((await device.StatusAsync()).Authorized);
        Assert.Empty(second.Secrets.Values);
    }

    [Fact]
    public async Task DisconnectRevokesOnlyCurrentDeviceAndLeavesMistralAndOtherDevicesConnected()
    {
        using var server = new SignalRTestServer();
        using var device = new AccessClient(server, RemoteOrigin);
        using var other = new AccessClient(server, RemoteOrigin);
        using var admin = new AccessClient(server, token: RootToken);
        var cookie = await PairBrowserAsync(device, admin, "First tablet");
        await PairBrowserAsync(other, admin, "Second tablet");
        await using var provider = server.Connect();
        await provider.LoginAsync("persisted-provider-key");

        using var disconnected = await device.MutateAsync(HttpMethod.Post, "/api/access/disconnect");
        Assert.Equal(HttpStatusCode.NoContent, disconnected.StatusCode);
        Assert.False((await device.StatusAsync()).Authorized);
        Assert.True((await other.StatusAsync()).Authorized);
        // Replaying the old cookie is denied by persistent revocation, not merely cookie deletion.
        device.Cookies.SetCookies(device.Address, cookie);
        Assert.False((await device.StatusAsync()).Authorized);
        Assert.Contains("persisted-provider-key", server.Secrets.Values.Values);
        Assert.Equal(VibeRe.Client.Application.AuthState.Authenticated, (await provider.GetStatusAsync()).State);
        var devices = await admin.DevicesAsync();
        Assert.NotNull(devices.Single(item => item.Name == "First tablet").RevokedAt);
        Assert.Null(devices.Single(item => item.Name == "Second tablet").RevokedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpRevocationOrExpiryClosesAnExistingBrowserWebSocketAndActiveStream(bool expire)
    {
        using var server = new SignalRTestServer();
        server.Browser.PollState = BrowserPollState.Pending;
        using var device = new AccessClient(server, RemoteOrigin);
        using var admin = new AccessClient(server, token: RootToken);
        await PairBrowserAsync(device, admin);
        var id = Assert.Single(await admin.DevicesAsync()).Id;
        await using var hub = CreateBrowserHub(server, device);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var closed = ClosedSignal(hub);
        await hub.StartAsync(deadline.Token);
        var attempt = await hub.InvokeAsync<BrowserLoginReply>("StartBrowserLogin", deadline.Token);
        await using var stream = hub.StreamAsync<BrowserLoginReply>("WatchBrowserLogin", attempt.AttemptId,
            deadline.Token).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(BrowserLoginState.Waiting, stream.Current.State);
        if (expire)
            server.Clock.Advance(TimeSpan.FromDays(31));
        else
        {
            using var revoked = await admin.MutateAsync(HttpMethod.Delete, $"/api/access/devices/{id}");
            Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        }
        await closed.Task.WaitAsync(deadline.Token);
        Assert.Equal(HubConnectionState.Disconnected, hub.State);
        var receivedMore = false;
        try { receivedMore = await stream.MoveNextAsync(); }
        catch (Exception error) when (error is HubException or OperationCanceledException or IOException) { }
        Assert.False(receivedMore, "An invalidated device must not receive another provider stream update.");
        Assert.False(deadline.IsCancellationRequested,
            "The stream must stop because the device was invalidated, not because the test timed out.");
        Assert.False((await device.StatusAsync()).Authorized);
        Assert.Empty(server.Secrets.Values);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiryOrOutOfBandRevocationClosesAnIdleBrowserWebSocketWithinTenSeconds(bool revoke)
    {
        using var server = new SignalRTestServer();
        using var device = new AccessClient(server, RemoteOrigin);
        using var admin = new AccessClient(server, token: RootToken);
        await PairBrowserAsync(device, admin);
        var id = Assert.Single(await admin.DevicesAsync()).Id;
        await using var hub = CreateBrowserHub(server, device);
        var closed = ClosedSignal(hub);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await hub.StartAsync(deadline.Token);
        Assert.Equal(AuthState.SignedOut, (await hub.InvokeAsync<AuthStatusReply>("GetStatus", deadline.Token)).State);
        if (revoke)
            Assert.True(await server.Services.GetRequiredService<IDeviceAccessService>().RevokeDeviceAsync(id, deadline.Token));
        else
            server.Clock.Advance(TimeSpan.FromDays(31));
        // No further invocation: the connection guard must invalidate idle WebSockets too.
        await closed.Task.WaitAsync(deadline.Token);
        Assert.Equal(HubConnectionState.Disconnected, hub.State);
        Assert.False((await device.StatusAsync()).Authorized);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NextHubInvocationCannotMutateProviderStateAfterSessionExpiresOrIsRevoked(bool revoke)
    {
        using var server = new SignalRTestServer();
        using var device = new AccessClient(server, RemoteOrigin);
        using var admin = new AccessClient(server, token: RootToken);
        await PairBrowserAsync(device, admin);
        var id = Assert.Single(await admin.DevicesAsync()).Id;
        await using var hub = CreateBrowserHub(server, device);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await hub.StartAsync(deadline.Token);
        Assert.Equal(AuthState.SignedOut, (await hub.InvokeAsync<AuthStatusReply>("GetStatus", deadline.Token)).State);
        if (revoke)
            Assert.True(await server.Services.GetRequiredService<IDeviceAccessService>().RevokeDeviceAsync(id, deadline.Token));
        else
            server.Clock.Advance(TimeSpan.FromDays(31));
        var failure = await Record.ExceptionAsync(() => hub.InvokeAsync<AuthStatusReply>("Login", "must-not-be-stored", deadline.Token));
        Assert.NotNull(failure);
        Assert.False(deadline.IsCancellationRequested, "Revocation must be checked on the invocation itself.");
        Assert.Empty(server.Secrets.Values);
    }

    private static TaskCompletionSource ClosedSignal(HubConnection hub)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.Closed += _ => { signal.TrySetResult(); return Task.CompletedTask; };
        return signal;
    }

    private static HubConnection CreateBrowserHub(SignalRTestServer server, AccessClient browser) =>
        new HubConnectionBuilder().WithUrl(new Uri(browser.Address, ServerProtocol.AuthHubPath), options =>
        {
            options.Transports = HttpTransportType.WebSockets;
            options.SkipNegotiation = true;
            options.WebSocketFactory = async (context, cancellationToken) =>
            {
                var client = server.Server.CreateWebSocketClient();
                client.ConfigureRequest = request =>
                {
                    request.Headers.Origin = browser.Address.GetLeftPart(UriPartial.Authority);
                    request.Headers.Cookie = browser.Cookies.GetCookieHeader(browser.Address);
                };
                return await client.ConnectAsync(context.Uri, cancellationToken);
            };
        }).Build();

    private static async Task<string> PairBrowserAsync(AccessClient browser, AccessClient admin, string name = "Test browser")
    {
        var pairing = await browser.BeginPairingAsync(name);
        await admin.ApproveAsync(pairing);
        using var response = await browser.RedeemAsync(pairing);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return DeviceCookie(response);
    }

    private static string DeviceCookie(HttpResponseMessage response) => Assert.Single(response.Headers.GetValues("Set-Cookie"));

    private static string CookieValue(string cookie) => cookie[..cookie.IndexOf(';')].Split('=', 2)[1];

    // A small browser-like cookie jar makes cookie provenance explicit. Unlike direct service setup,
    // every pairing, approval and redemption above traverses the production HTTP/CSRF middleware.
    private sealed class AccessClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string? _token;
        private readonly bool _browser;
        public Uri Address => _http.BaseAddress!;
        public CookieContainer Cookies { get; }
        public List<string> Bodies { get; } = [];

        public AccessClient(SignalRTestServer server, string address = "http://localhost", string? token = null,
            bool browser = true, CookieContainer? cookies = null)
        {
            _http = server.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri(address), AllowAutoRedirect = false, HandleCookies = false
            });
            _token = token;
            _browser = browser;
            Cookies = cookies ?? new CookieContainer();
        }

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null,
            string? token = null, string? csrf = null, string? origin = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = JsonContent.Create(body);
            if ((token ?? _token) is { } credential)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            var cookies = Cookies.GetCookieHeader(Address);
            if (!string.IsNullOrEmpty(cookies)) request.Headers.TryAddWithoutValidation("Cookie", cookies);
            if (csrf is not null) request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf);
            if (origin is not null || _browser)
                request.Headers.TryAddWithoutValidation("Origin", origin ?? Address.GetLeftPart(UriPartial.Authority));
            var response = await _http.SendAsync(request);
            if (response.Headers.TryGetValues("Set-Cookie", out var values))
                foreach (var value in values) Cookies.SetCookies(Address, value);
            Bodies.Add(await response.Content.ReadAsStringAsync());
            return response;
        }

        public async Task<string> CsrfAsync()
        {
            using var response = await SendAsync(HttpMethod.Get, "/api/access/csrf");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.GetProperty("requestToken").GetString()!;
        }

        public async Task<HttpResponseMessage> MutateAsync(HttpMethod method, string path, object? body = null,
            string? token = null, string? origin = null) =>
            await SendAsync(method, path, body, token, await CsrfAsync(), origin);

        public async Task<AccessStatusReply> StatusAsync()
        {
            using var response = await SendAsync(HttpMethod.Get, "/api/access/status");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<AccessStatusReply>())!;
        }

        public async Task<PairingReply> BeginPairingAsync(string name)
        {
            using var response = await MutateAsync(HttpMethod.Post, "/api/access/pairings", new BeginPairingRequest(name));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<PairingReply>())!;
        }

        public async Task ApproveAsync(PairingReply pairing)
        {
            using var response = await MutateAsync(HttpMethod.Post, "/api/access/pairings/approve",
                new ApprovePairingRequest(pairing.ApprovalCode));
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        public Task<HttpResponseMessage> RedeemAsync(PairingReply pairing, bool native = false) =>
            MutateAsync(HttpMethod.Post, "/api/access/pairings/" + (native ? "redeem-native" : "redeem"),
                new RedeemPairingRequest(pairing.PairingId, pairing.PairingSecret));

        public async Task<DeviceReply[]> DevicesAsync()
        {
            using var response = await SendAsync(HttpMethod.Get, "/api/access/devices");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<DeviceReply[]>())!;
        }

        public void Dispose() => _http.Dispose();
    }
}
