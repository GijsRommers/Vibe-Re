using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using VibeRe.Server.Application.Access;

namespace VibeRe.Server.Services;

// HTTP authentication alone cannot revoke a WebSocket that is already connected.
public sealed class DeviceConnectionGuard(IDeviceAccessService devices, TimeProvider clock,
    ILogger<DeviceConnectionGuard> logger)
{
    private readonly ConcurrentDictionary<string, (Guid DeviceId, HubCallerContext Context)> _connections = new();

    public void Register(HubCallerContext context, string token, DeviceInfo device)
    {
        _connections[context.ConnectionId] = (device.Id, context);
        _ = ObserveAsync(context, token, device.ExpiresAt);
    }

    public void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);
    public void Revoke(Guid deviceId)
    {
        foreach (var entry in _connections.Values)
            if (entry.DeviceId == deviceId) entry.Context.Abort();
    }

    private async Task ObserveAsync(HubCallerContext context, string token, DateTimeOffset expires)
    {
        try
        {
            while (!context.ConnectionAborted.IsCancellationRequested)
            {
                var remaining = expires - clock.GetUtcNow();
                if (remaining <= TimeSpan.Zero) break;
                await Task.Delay(remaining < TimeSpan.FromSeconds(5) ? remaining : TimeSpan.FromSeconds(5),
                    context.ConnectionAborted);
                if (await devices.ValidateSessionAsync(token, context.ConnectionAborted) is null) break;
            }
        }
        catch (OperationCanceledException) when (context.ConnectionAborted.IsCancellationRequested) { }
        catch (Exception error) { logger.LogWarning("Device session validation failed ({ExceptionType}).", error.GetType().Name); }
        finally
        {
            Remove(context.ConnectionId);
            context.Abort();
        }
    }
}

public sealed class DeviceAccessHubFilter(ClientAccessPolicy access, DeviceConnectionGuard connections) : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext context,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        await EnsureAuthorizedAsync(context.Context);
        return await next(context);
    }

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        var http = context.Context.GetHttpContext();
        if (http?.Items[ClientAccessPolicy.ItemKey] is AuthorizedClient handshake)
            context.Context.Items[ClientAccessPolicy.ItemKey] = handshake;
        var client = await EnsureAuthorizedAsync(context.Context);
        if (client.Device is { } device) connections.Register(context.Context, client.Token!, device);
        try { await next(context); }
        catch { connections.Remove(context.Context.ConnectionId); throw; }
    }

    public Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception,
        Func<HubLifetimeContext, Exception?, Task> next)
    {
        connections.Remove(context.Context.ConnectionId);
        return next(context, exception);
    }

    public async Task<AuthorizedClient> EnsureAuthorizedAsync(HubCallerContext context)
    {
        context.ConnectionAborted.ThrowIfCancellationRequested();
        var client = context.Items.TryGetValue(ClientAccessPolicy.ItemKey, out var identity) && identity is AuthorizedClient handshake
            ? await access.RevalidateAsync(handshake, context.ConnectionAborted) : new AuthorizedClient();
        if (client.Authorized) return client;
        context.Abort();
        throw new HubException("This device session expired or was revoked. Pair this device again.");
    }
}
