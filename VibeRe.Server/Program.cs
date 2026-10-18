using VibeRe.Server;
using VibeRe.Contracts;
using VibeRe.Server.Services;

var builder = WebApplication.CreateBuilder(args);

if (string.IsNullOrEmpty(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://localhost:5067");
// Browser WebSocket/SSE clients send their access token in the query string. Never log request URLs.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Http.Connections", LogLevel.Warning);
// Hub invocation arguments include API keys; verbose protocol/frame logging is not safe here either.
builder.Logging.AddFilter("Microsoft.AspNetCore.SignalR", LogLevel.Warning);
builder.Services.AddSignalR(options => options.EnableDetailedErrors = false);
builder.Services.AddVibeReServices(builder.Configuration);
var app = builder.Build();

await app.ApplyMigrationsAsync();

// Install WebSocket detection before access checks; the checks also protect negotiation and long polling.
app.UseWebSockets();
app.UseMiddleware<ServerAccessMiddleware>();
app.MapHub<ProviderAuthHub>(ServerProtocol.AuthHubPath);
app.MapGet("/", () => "VibeRe server");
// Public readiness contains no credentials; clients can identify the runtime before provider login.
app.MapGet(ServerProtocol.ReadinessPath, () => new
{
    application = "vibe-re", protocolVersion = ServerProtocol.Version, transport = ServerProtocol.Transport
});

app.Run();

public partial class Program;
