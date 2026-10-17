using Avalonia.Controls;
using VibeRe.Client.Application;

namespace VibeRe.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (DataContext is ConnectionSettingsViewModel model)
                await model.InitializeAsync();
        };
        Closed += (_, _) => (DataContext as ConnectionSettingsViewModel)?.CancelPending();
    }
}
