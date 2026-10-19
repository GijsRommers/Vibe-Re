using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using VibeRe.Client.Application;
using VibeRe.Client.Application.Configuration;
using VibeRe.Web;
using VibeRe.Web.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");
builder.Services.AddScoped(services => new BrowserServer(
    services.GetRequiredService<NavigationManager>().Uri));
builder.Services.AddScoped(services => new HttpClient
{
    BaseAddress = services.GetRequiredService<BrowserServer>().Origin,
    Timeout = TimeSpan.FromSeconds(30)
});
builder.Services.AddScoped<BrowserAccessClient>();
builder.Services.AddScoped<IClientConfigurationStore, CurrentOriginConfiguration>();
builder.Services.AddScoped<IServerConnector, BrowserServerConnector>();
builder.Services.AddScoped<ClientStartup>();
await builder.Build().RunAsync();
