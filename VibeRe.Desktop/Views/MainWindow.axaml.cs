using Avalonia.Controls;
using VibeRe.Desktop.ViewModels;

namespace VibeRe.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (DataContext is MainViewModel model)
            {
                await model.InitializeAsync();
                model.StartMonitoring();
            }
        };
        Closed += (_, _) => (DataContext as MainViewModel)?.StopMonitoring();
    }
}
