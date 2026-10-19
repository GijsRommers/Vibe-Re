using VibeRe.Contracts;

namespace VibeRe.Server.Services;

// Protect the complete transport. Pairing endpoints apply the same policy plus antiforgery checks.
public sealed class ServerAccessMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ClientAccessPolicy access)
    {
        var hub = context.Request.Path.StartsWithSegments(ServerProtocol.AuthHubPath, StringComparison.OrdinalIgnoreCase);
        var accessApi = context.Request.Path.StartsWithSegments("/api/access", StringComparison.OrdinalIgnoreCase);
        if (!hub && !accessApi)
        {
            await next(context);
            return;
        }
        if (!ClientAccessPolicy.IsSafeConnection(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        if (!hub) { await next(context); return; }
        var client = await access.ResolveAsync(context, context.RequestAborted);
        if (!client.Authorized)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        context.Items[ClientAccessPolicy.ItemKey] = client;
        await next(context);
    }
}
