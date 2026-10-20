using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VibeRe.Desktop.ViewModels;
using VibeRe.Updates;

namespace VibeRe.Desktop.Views;

public partial class MainWindow : Window
{
    public DesktopUpdateService? UpdateService { get; init; }
    public bool ShowUpdatesOnStartup { get; init; }
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow()
    {
        InitializeComponent();
        _refresh.Tick += async (_, _) =>
        {
            if (DataContext is WorkspaceViewModel model) await model.RefreshAsync();
        };
        Opened += async (_, _) =>
        {
            if (ShowUpdatesOnStartup && UpdateService is not null)
                await new UpdatesWindow(UpdateService).ShowDialog(this);
            if (DataContext is WorkspaceViewModel model)
            {
                model.Messages.CollectionChanged += (_, _) => FollowMessages(ThreadScroll);
                model.CockpitMessages.CollectionChanged += (_, _) => FollowMessages(CockpitScroll);
                await model.InitializeAsync();
                _refresh.Start();
            }
        };
        Closing += (_, args) => args.Cancel = UpdateService?.State.Stage == UpdateStage.Applying;
        Closed += async (_, _) =>
        {
            _refresh.Stop();
            if (DataContext is WorkspaceViewModel model) await model.DisposeAsync();
        };
    }
    private static void FollowMessages(ScrollViewer scroll)
    {
        if (scroll.Extent.Height - scroll.Offset.Y - scroll.Viewport.Height < 100)
            Dispatcher.UIThread.Post(scroll.ScrollToEnd, DispatcherPriority.Background);
    }
    private void ComposerKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || args.KeyModifiers.HasFlag(KeyModifiers.Shift) || DataContext is not WorkspaceViewModel model) return;
        args.Handled = true;
        if (sender == CockpitInput && model.CanSendCockpit) model.SendCockpitCommand.Execute(null);
        else if (sender == ThreadInput && model.CanSendThread) model.SendCommand.Execute(null);
    }
    private void DismissSettings(object? sender, PointerPressedEventArgs args)
    {
        if (DataContext is WorkspaceViewModel model) model.SettingsOpen = false;
        args.Handled = true;
    }
    private void KeepSettingsOpen(object? sender, PointerPressedEventArgs args) => args.Handled = true;
    private async void OpenUpdates(object? sender, RoutedEventArgs args)
    {
        if (UpdateService is not null) await new UpdatesWindow(UpdateService).ShowDialog(this);
    }
}
