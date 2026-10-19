using Avalonia;

namespace VibeRe.Desktop;

internal sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Run before Avalonia or server startup. Updates are applied only after explicit consent.
        try
        {
            Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false)
                .OnFirstRun(_ => VibeRe.Lifecycle.ManagedServerShutdownGuard.CompleteInstallation(AppContext.BaseDirectory))
                .OnRestarted(_ => VibeRe.Lifecycle.ManagedServerShutdownGuard.CompletePendingUpdate(AppContext.BaseDirectory))
                .Run();
            if (args.Contains("--repair-update", StringComparer.Ordinal))
                VibeRe.Lifecycle.ManagedServerShutdownGuard.RecoverInterruptedUpdate(AppContext.BaseDirectory);
            VibeRe.Lifecycle.ManagedServerShutdownGuard.EnsureApplicationCanStart(AppContext.BaseDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Do not keep an error window (and its installed assemblies) alive while the updater replaces files.
            Console.Error.WriteLine(exception.Message);
            Environment.ExitCode = 1;
            return;
        }
        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
