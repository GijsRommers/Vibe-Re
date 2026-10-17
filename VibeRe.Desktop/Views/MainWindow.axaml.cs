using Avalonia.Controls;
using VibeRe.Client;
using VibeRe.Runtime;

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
