using VibeRe.Server;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using VibeRe.Server.Services;

var builder = WebApplication.CreateBuilder(args);

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
