using VibeRe.Runtime.Adapters;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using VibeRe.Client;
using VibeRe.Runtime;
using VibeRe.Runtime.Configuration;
using VibeRe.Desktop.Views;

namespace VibeRe.Desktop;

public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new ServiceCollection().AddVibeReRuntime(ClientConnectionDatabase.DesktopPath)
                .BuildServiceProvider();
            desktop.Exit += (_, _) => services.Dispose();
            desktop.MainWindow = new MainWindow
            {
                DataContext = services.GetRequiredService<ConnectionSettingsViewModel>()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
