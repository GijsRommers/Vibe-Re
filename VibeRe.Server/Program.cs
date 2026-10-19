using VibeRe.Server;
using VibeRe.Lifecycle;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.RateLimiting;
using System.Globalization;
using System.Threading.RateLimiting;
using VibeRe.Contracts;
using VibeRe.Server.Services;

// A manually started or remote server has no managed registration or shutdown IPC.
// Register before migrations/startup so an update can account for a server still starting.
using var managedServer = ManagedServerRegistration.TryCreateFromEnvironment();
// Managed launches already hold the installation gate until registration. Direct launches
// must check it themselves so a manually started bundled server cannot race replacement.
if (managedServer is null)
    ManagedServerShutdownGuard.EnsureUnmanagedServerCanStart(AppContext.BaseDirectory);

// Managed clients can launch from any directory. Keep shipped settings and assets beside the host,
// while still allowing a separate configuration directory (the packaging host uses the same rule).
var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = Environment.GetEnvironmentVariable("ASPNETCORE_CONTENTROOT") ?? AppContext.BaseDirectory,
    WebRootPath = Directory.Exists(webRoot) ? webRoot : null
});
// Source startup deliberately skips launch profiles; its referenced WASM assets must work in Production too.
builder.WebHost.UseStaticWebAssets();

if (string.IsNullOrEmpty(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://localhost:5067");
// Keep credentials and ephemeral transport identifiers out of request logs.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Http.Connections", LogLevel.Warning);
// Hub invocation arguments include API keys; verbose protocol/frame logging is not safe here either.
builder.Logging.AddFilter("Microsoft.AspNetCore.SignalR", LogLevel.Warning);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = false;
    options.AddFilter<DeviceAccessHubFilter>();
});
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, _) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        return ValueTask.CompletedTask;
    };
    options.AddFixedWindowLimiter("pair-begin", policy =>
    { policy.PermitLimit = 10; policy.Window = TimeSpan.FromMinutes(1); policy.QueueLimit = 0; });
    options.AddFixedWindowLimiter("pair-redeem", policy =>
    { // Accommodate every pending pairing (32) polling every two seconds, with headroom.
      policy.PermitLimit = 2048; policy.Window = TimeSpan.FromMinutes(1); policy.QueueLimit = 0; });
});
builder.Services.AddVibeReServices(builder.Configuration);
if (managedServer is not null)
{
    builder.Services.AddSingleton(managedServer);
    builder.Services.AddHostedService<ManagedServerLifetime>();
}
var app = builder.Build();

await app.ApplyMigrationsAsync();

// Install WebSocket detection before access checks; the checks also protect negotiation and long polling.
app.UseMiddleware<TailscaleServeMiddleware>();
app.UseWebSockets();
app.UseMiddleware<ServerAccessMiddleware>();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'none'; base-uri 'self'; object-src 'none'";
    await next(context);
});
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.MapDeviceAccess();
app.MapHub<ProviderAuthHub>(ServerProtocol.AuthHubPath);

// Public readiness contains no credentials; clients can identify the runtime before provider login.
app.MapGet(ServerProtocol.ReadinessPath, () => new
{
    application = "vibe-re", protocolVersion = ServerProtocol.Version, transport = ServerProtocol.Transport
});

// Unknown API/hub paths must remain 404, not the SPA HTML document.
app.Map("/api/{**path}", () => Results.NotFound());
app.Map("/hubs/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
