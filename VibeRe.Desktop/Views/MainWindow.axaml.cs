using Avalonia.Controls;
using VibeRe.Client.Application;
using VibeRe.Updates;
using Avalonia.Interactivity;

namespace VibeRe.Desktop.Views;

public partial class MainWindow : Window
{
    public DesktopUpdateService? UpdateService { get; init; }
    public bool ShowUpdatesOnStartup { get; init; }

    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (ShowUpdatesOnStartup && UpdateService is not null)
                await new UpdatesWindow(UpdateService).ShowDialog(this);
            if (DataContext is ConnectionSettingsViewModel model)
                await model.InitializeAsync();
        };
        Closing += (_, args) => args.Cancel = UpdateService?.State.Stage == UpdateStage.Applying;
        Closed += (_, _) => (DataContext as ConnectionSettingsViewModel)?.CancelPending();
    }

    private async void OpenUpdates(object? sender, RoutedEventArgs args)
    {
        if (UpdateService is not null)
            await new UpdatesWindow(UpdateService).ShowDialog(this);
    }
}
