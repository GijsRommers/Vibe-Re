using VibeRe.Server;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using VibeRe.Server.Services;

// Installed clients can launch us from any working directory. Load the shipped
// settings beside the server, with an optional external configuration directory.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = Environment.GetEnvironmentVariable("ASPNETCORE_CONTENTROOT") ?? AppContext.BaseDirectory
});

if (string.IsNullOrEmpty(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://localhost:5067");
builder.WebHost.ConfigureKestrel(options =>
    options.ConfigureEndpointDefaults(endpoint => endpoint.Protocols = HttpProtocols.Http2));
builder.Services.AddGrpc(options => options.Interceptors.Add<ServerAccessInterceptor>());
builder.Services.AddVibeReServices(builder.Configuration);
var app = builder.Build();

await app.ApplyMigrationsAsync();


app.MapGrpcService<ProviderAuthGrpcService>();
app.MapGet("/", () => "VibeRe server");
// Public readiness contains no credentials; clients can identify the runtime before provider login.
app.MapGet("/.well-known/vibe-re/server", () => new { application = "vibe-re", protocolVersion = 1 });

app.Run();

public partial class Program;
