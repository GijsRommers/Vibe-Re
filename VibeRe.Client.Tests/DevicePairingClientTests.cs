using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VibeRe.Client.Application;
using VibeRe.Client.Infrastructure.Adapters;
using VibeRe.Contracts;

namespace VibeRe.Client.Tests;

public sealed class DevicePairingClientTests
{
    [Fact]
    public async Task NativePairingUsesCookieAndCsrfPostsAndReturnsOnlyTheIssuedToken()
    {
        await using var server = await PairingServer.StartAsync(pendingPolls: 1);
        DevicePairingChallenge? shown = null;
        var elapsed = Stopwatch.StartNew();
        var token = await new DevicePairingClient().PairAsync(server.Address, "  My tablet  ", challenge =>
        {
            shown = challenge;
            return Task.CompletedTask;
        });

        Assert.Equal("device-access-token", token);
        Assert.Equal("My tablet", server.DeviceName);
        Assert.Equal(new DevicePairingChallenge("ABCD1234", server.ExpiresAt), shown);
        Assert.Equal(2, server.Redemptions);
        Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(2));
        Assert.All(server.Mutations, headers =>
        {
            Assert.Equal("fixture-csrf", headers.Csrf);
            Assert.Contains("pairing-csrf=fixture-cookie", headers.Cookie);
            Assert.Empty(headers.Origin);
            Assert.Empty(headers.Authorization);
        });
        Assert.All(server.Urls, url => Assert.DoesNotContain("?", url));
        Assert.DoesNotContain(server.Urls, url => url.Contains("pending-secret") || url.Contains("device-access-token"));
        Assert.Equal(new RedeemPairingRequest("pending-id", "pending-secret"), server.Redemption);
    }

    [Theory]
    [InlineData("/api/access/csrf")]
    [InlineData("/api/access/pairings")]
    [InlineData("/api/access/pairings/redeem-native")]
    public async Task NoStageFollowsRedirects(string redirectPath)
    {
        await using var server = await PairingServer.StartAsync(redirectPath: redirectPath);
        var error = await Assert.ThrowsAsync<ServerConnectionException>(() =>
            new DevicePairingClient().PairAsync(server.Address, "Device", _ => Task.CompletedTask));
        Assert.Equal(0, server.RedirectsFollowed);
        Assert.DoesNotContain("pending-secret", error.ToString());
        Assert.DoesNotContain("device-access-token", error.ToString());
    }

    [Fact]
    public async Task PendingAttemptExpiresWithoutAnotherPoll()
    {
        await using var server = await PairingServer.StartAsync(pendingPolls: int.MaxValue,
            lifetime: TimeSpan.FromMilliseconds(500));
        var elapsed = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<ServerConnectionException>(() =>
            new DevicePairingClient().PairAsync(server.Address, "Device", _ => Task.CompletedTask));
        Assert.Contains("expired", error.Message);
        Assert.Equal(1, server.Redemptions);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ExpiredChallengeIsNeverShownOrRedeemed()
    {
        await using var server = await PairingServer.StartAsync(lifetime: TimeSpan.FromMinutes(-1));
        var shown = false;
        await Assert.ThrowsAsync<ServerConnectionException>(() => new DevicePairingClient().PairAsync(server.Address,
            "Device", _ => { shown = true; return Task.CompletedTask; }));
        Assert.False(shown);
        Assert.Equal(0, server.Redemptions);
    }

    [Fact]
    public async Task CancelingPendingAttemptStopsPromptlyAndAllowsFreshRetry()
    {
        await using var server = await PairingServer.StartAsync(pendingPolls: int.MaxValue);
        using var cancellation = new CancellationTokenSource();
        var client = new DevicePairingClient();
        var pending = client.PairAsync(server.Address, "Device", _ => Task.CompletedTask, cancellation.Token);
        await server.FirstRedemption.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, server.Redemptions);
        server.PendingPolls = 0;
        Assert.Equal("device-access-token", await client.PairAsync(server.Address, "Device", _ => Task.CompletedTask));
        Assert.Equal(2, server.Beginnings);
    }

    [Fact]
    public async Task RateLimitedRedemptionHonorsRetryAfterThenCompletes()
    {
        await using var server = await PairingServer.StartAsync(rateLimitedPolls: 1, retryAfter: "1");
        var elapsed = Stopwatch.StartNew();
        Assert.Equal("device-access-token", await new DevicePairingClient().PairAsync(server.Address,
            "Device", _ => Task.CompletedTask));
        Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(1));
        Assert.Equal(2, server.Redemptions);
        Assert.Equal(1, server.Beginnings);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("3600")]
    public async Task RateLimitDelayCannotExtendPairingExpiry(string? retryAfter)
    {
        await using var server = await PairingServer.StartAsync(rateLimitedPolls: int.MaxValue,
            retryAfter: retryAfter, lifetime: TimeSpan.FromMilliseconds(500));
        var elapsed = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<ServerConnectionException>(() =>
            new DevicePairingClient().PairAsync(server.Address, "Device", _ => Task.CompletedTask));
        Assert.Contains("expired", error.Message);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3));
        Assert.Equal(1, server.Redemptions);
    }

    [Theory]
    [InlineData("http://remote.example")]
    [InlineData("http://192.168.1.12:5067")]
    [InlineData("https://user:secret@remote.example")]
    [InlineData("https://remote.example/path")]
    [InlineData("https://remote.example?token=secret")]
    public async Task PairingRejectsUnsafeOriginsBeforeNetworkAccess(string address) =>
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new DevicePairingClient().PairAsync(address, "Device", _ => Task.CompletedTask));

    [Theory]
    [InlineData(404)]
    [InlineData(410)]
    [InlineData(403)]
    [InlineData(500)]
    public async Task FailedPairingIsNotRetriedAndDoesNotExposeServerResponseSecrets(int status)
    {
        await using var server = await PairingServer.StartAsync(redemptionStatus: status);
        var error = await Assert.ThrowsAsync<ServerConnectionException>(() =>
            new DevicePairingClient().PairAsync(server.Address, "Device", _ => Task.CompletedTask));
        Assert.Equal(1, server.Redemptions);
        Assert.DoesNotContain("server-secret", error.ToString());
    }

    private sealed class PairingServer(WebApplication app) : IAsyncDisposable
    {
        public string Address { get; private set; } = "";
        public DateTimeOffset ExpiresAt { get; private set; }
        public int PendingPolls { get; set; }
        public int Beginnings { get; private set; }
        public int Redemptions { get; private set; }
        public int RedirectsFollowed { get; private set; }
        public string? DeviceName { get; private set; }
        public RedeemPairingRequest? Redemption { get; private set; }
        public TaskCompletionSource FirstRedemption { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(string Csrf, string Cookie, string Origin, string Authorization)> Mutations { get; } = [];
        public List<string> Urls { get; } = [];

        public static async Task<PairingServer> StartAsync(int pendingPolls = 0, string? redirectPath = null,
            TimeSpan? lifetime = null, int redemptionStatus = 200, int rateLimitedPolls = 0, string? retryAfter = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var server = new PairingServer(app) { PendingPolls = pendingPolls };
            app.Use(async (context, next) =>
            {
                server.Urls.Add(context.Request.Path + context.Request.QueryString);
                if (context.Request.Path == redirectPath)
                {
                    context.Response.Redirect("/unexpected", permanent: false, preserveMethod: true);
                    return;
                }
                if (context.Request.Method == "POST")
                    server.Mutations.Add((context.Request.Headers["X-CSRF-TOKEN"].ToString(),
                        context.Request.Headers.Cookie.ToString(), context.Request.Headers.Origin.ToString(),
                        context.Request.Headers.Authorization.ToString()));
                await next(context);
            });
            app.MapMethods("/unexpected", ["GET", "POST"], () => { server.RedirectsFollowed++; return Results.Ok(); });
            app.MapGet("/api/access/csrf", (HttpContext context) =>
            {
                context.Response.Cookies.Append("pairing-csrf", "fixture-cookie", new CookieOptions { HttpOnly = true, Path = "/" });
                return Results.Json(new { requestToken = "fixture-csrf" });
            });
            app.MapPost("/api/access/pairings", (BeginPairingRequest request) =>
            {
                server.Beginnings++;
                server.DeviceName = request.DeviceName;
                server.ExpiresAt = DateTimeOffset.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(5));
                return Results.Json(new PairingReply("pending-id", "pending-secret", "ABCD1234", server.ExpiresAt));
            });
            app.MapPost("/api/access/pairings/redeem-native", (RedeemPairingRequest request, HttpContext context) =>
            {
                server.Redemption = request;
                server.Redemptions++;
                server.FirstRedemption.TrySetResult();
                if (server.Redemptions <= rateLimitedPolls)
                {
                    if (retryAfter is not null) context.Response.Headers.RetryAfter = retryAfter;
                    return Results.Text("server-secret", statusCode: 429);
                }
                if (server.Redemptions <= server.PendingPolls) return Results.StatusCode(202);
                if (redemptionStatus != 200) return Results.Text("server-secret", statusCode: redemptionStatus);
                return Results.Json(new DeviceTokenReply("device-access-token",
                    new DeviceReply(Guid.NewGuid(), "Device", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30))));
            });
            await app.StartAsync();
            server.Address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return server;
        }

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }
}
