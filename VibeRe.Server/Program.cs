using Microsoft.AspNetCore.Server.Kestrel.Core;
using VibeRe.Application.Auth;
using VibeRe.Infrastructure;
using VibeRe.Infrastructure.Auth.Mistral;
using VibeRe.Infrastructure.Credentials;
using VibeRe.Protocol;
using VibeRe.Server.Services;

var builder = WebApplication.CreateBuilder(args);

// Localhost only, cleartext HTTP/2 for gRPC. ASPNETCORE_URLS or --urls override the address.
if (string.IsNullOrEmpty(builder.Configuration[WebHostDefaults.ServerUrlsKey]))
{
    builder.WebHost.UseUrls($"http://localhost:{ServerDefaults.Port}");
}

builder.WebHost.ConfigureKestrel(kestrel =>
    kestrel.ConfigureEndpointDefaults(endpoint => endpoint.Protocols = HttpProtocols.Http2));

// Installed as a single file without appsettings.json, so keep per-request logs out of server.log here.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

builder.Services.AddGrpc(options => options.Interceptors.Add<AuthExceptionInterceptor>());

// Auth: macOS Keychain on macOS, an owner-only file elsewhere.
builder.Services.AddSingleton<ICredentialStore>(_ => OperatingSystem.IsMacOS()
    ? new MacOsKeychainCredentialStore()
    : new FileCredentialStore(Path.Combine(VibeRePaths.Home, "credentials.json")));
builder.Services.AddSingleton<IAuthProvider>(_ =>
{
    var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("VibeRe/0.1");
    return new MistralAuthProvider(http);
});
builder.Services.AddSingleton(services => new AuthService(
    services.GetServices<IAuthProvider>(),
    services.GetRequiredService<ICredentialStore>()));

var app = builder.Build();

app.MapGrpcService<AuthGrpcService>();

app.Run();
