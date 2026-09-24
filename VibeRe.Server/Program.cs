using VibeRe.Application.Auth;
using VibeRe.Infrastructure;
using VibeRe.Infrastructure.Auth.Mistral;
using VibeRe.Infrastructure.Credentials;
using VibeRe.Server.Services;

var builder = WebApplication.CreateBuilder(args);

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
