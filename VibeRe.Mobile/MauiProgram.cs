using VibeRe.Client;
using VibeRe.Runtime;
using Microsoft.Extensions.Logging;
using VibeRe.Runtime.Configuration;

namespace VibeRe.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

#if DEBUG
        builder.Logging.AddDebug();
#endif

        builder.Services.AddSingleton<IServerTokenStore, MobileServerTokenStore>();
        builder.Services.AddSingleton<IBrowserLauncher, MobileBrowserLauncher>();
        builder.Services.AddVibeReRuntime(Path.Combine(FileSystem.AppDataDirectory, "client.db"));
        builder.Services.AddSingleton<MainPage>();
        builder.Services.AddSingleton<AppShell>();
        return builder.Build();
    }
}
