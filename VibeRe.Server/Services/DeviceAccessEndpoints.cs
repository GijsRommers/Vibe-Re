using Microsoft.AspNetCore.Antiforgery;
using VibeRe.Contracts;
using VibeRe.Domain.Access;
using VibeRe.Server.Application.Access;

namespace VibeRe.Server.Services;

public static class DeviceAccessEndpoints
{
    public static void MapDeviceAccess(this WebApplication app)
    {
        var group = app.MapGroup("/api/access").AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            http.Response.Headers.CacheControl = "no-store";
            if (!ClientAccessPolicy.IsSafeConnection(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!HttpMethods.IsGet(http.Request.Method))
            {
                try { await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http); }
                catch (AntiforgeryValidationException) { return Results.BadRequest(new { message = "Refresh this page and try again." }); }
            }
            try { return await next(context); }
            catch (DeviceAccessLimitException error) { return Results.Json(new { message = error.Message }, statusCode: 429); }
            catch (ArgumentException) { return Results.BadRequest(new { message = "Enter a valid device name or pairing code." }); }
        });

        group.MapGet("/csrf", (HttpContext http, IAntiforgery antiforgery) =>
            Results.Ok(new { requestToken = antiforgery.GetAndStoreTokens(http).RequestToken }));
        group.MapGet("/status", async (HttpContext http, ClientAccessPolicy access) =>
        {
            var client = await access.ResolveAsync(http);
            return Results.Ok(new AccessStatusReply(client.Authorized, ClientAccessPolicy.CanManage(http, client),
                ClientAccessPolicy.IsLocal(http), client.Device?.Name, client.Device?.ExpiresAt));
        });
        group.MapPost("/pairings", async (BeginPairingRequest request, HttpContext http, IDeviceAccessService devices) =>
        {
            var pairing = await devices.BeginPairingAsync(request.DeviceName, http.RequestAborted);
            return Results.Ok(new PairingReply(pairing.PairingId, pairing.PairingSecret, pairing.ApprovalCode, pairing.ExpiresAt));
        }).RequireRateLimiting("pair-begin");
        group.MapPost("/pairings/approve", async (ApprovePairingRequest request, HttpContext http,
            ClientAccessPolicy access, IDeviceAccessService devices) =>
        {
            if (!ClientAccessPolicy.CanManage(http, await access.ResolveAsync(http))) return Results.StatusCode(403);
            return await devices.ApprovePairingAsync(request.ApprovalCode, http.RequestAborted)
                ? Results.NoContent() : Results.NotFound(new { message = "Pairing code not found, already approved, or expired." });
        });
        group.MapPost("/pairings/redeem", (RedeemPairingRequest request, HttpContext http, IDeviceAccessService devices) =>
            RedeemAsync(request, http, devices, browser: true)).RequireRateLimiting("pair-redeem");
        group.MapPost("/pairings/redeem-native", (RedeemPairingRequest request, HttpContext http, IDeviceAccessService devices) =>
            RedeemAsync(request, http, devices, browser: false)).RequireRateLimiting("pair-redeem");
        group.MapPost("/local-session", async (HttpContext http, ClientAccessPolicy access, IDeviceSessionIssuer issuer) =>
        {
            if (!ClientAccessPolicy.CanManage(http, await access.ResolveAsync(http))) return Results.StatusCode(403);
            var grant = await issuer.IssueSessionAsync("Browser on this server", TimeSpan.FromDays(30),
                DevicePermissions.ProviderAccess | DevicePermissions.ManageDevices, http.RequestAborted);
            SetBrowserSession(http, grant);
            return Results.NoContent();
        });
        group.MapGet("/devices", async (HttpContext http, ClientAccessPolicy access, IDeviceAccessService devices) =>
        {
            if (!ClientAccessPolicy.CanManage(http, await access.ResolveAsync(http))) return Results.StatusCode(403);
            return Results.Ok((await devices.ListDevicesAsync(http.RequestAborted)).Select(Map));
        });
        group.MapDelete("/devices/{id:guid}", async (Guid id, HttpContext http, ClientAccessPolicy access,
            IDeviceAccessService devices, DeviceConnectionGuard connections) =>
        {
            if (!ClientAccessPolicy.CanManage(http, await access.ResolveAsync(http))) return Results.StatusCode(403);
            if (!await devices.RevokeDeviceAsync(id, http.RequestAborted)) return Results.NotFound();
            connections.Revoke(id);
            return Results.NoContent();
        });
        group.MapPost("/disconnect", async (HttpContext http, ClientAccessPolicy access,
            IDeviceAccessService devices, DeviceConnectionGuard connections) =>
        {
            var client = await access.ResolveAsync(http);
            if (client.Device is { } device)
            {
                await devices.RevokeDeviceAsync(device.Id, http.RequestAborted);
                connections.Revoke(device.Id);
            }
            http.Response.Cookies.Delete(ClientAccessPolicy.CookieName(http), CookieOptions(http));
            return Results.NoContent();
        });
    }

    private static async Task<IResult> RedeemAsync(RedeemPairingRequest request, HttpContext http,
        IDeviceAccessService devices, bool browser)
    {
        // The browser route never exposes the durable token to JavaScript.
        if (!browser && http.Request.Headers.ContainsKey("Origin")) return Results.StatusCode(403);
        var result = await devices.RedeemPairingAsync(request.PairingId, request.PairingSecret, http.RequestAborted);
        if (result.State == DevicePairingState.Pending) return Results.StatusCode(202);
        if (result.Grant is not { } grant) return Results.NotFound(new { message = "Pairing expired or was already used. Start again." });
        if (!browser) return Results.Ok(new DeviceTokenReply(grant.Token, Map(grant.Device)));
        SetBrowserSession(http, grant);
        return Results.NoContent();
    }

    private static CookieOptions CookieOptions(HttpContext http) => new()
    {
        HttpOnly = true, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Strict, Path = "/",
        IsEssential = true
    };
    public static void SetBrowserSession(HttpContext http, DeviceSessionGrant grant)
    {
        var options = CookieOptions(http);
        options.Expires = grant.Device.ExpiresAt;
        http.Response.Cookies.Append(ClientAccessPolicy.CookieName(http), grant.Token, options);
    }
    private static DeviceReply Map(DeviceInfo device) => new(device.Id, device.Name, device.CreatedAt,
        device.ExpiresAt, device.RevokedAt);
}
