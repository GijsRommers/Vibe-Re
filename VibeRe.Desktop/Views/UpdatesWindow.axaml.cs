using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VibeRe.Updates;

namespace VibeRe.Desktop.Views;

public partial class UpdatesWindow : Window
{
    private readonly DesktopUpdateService _updates;

    public UpdatesWindow() : this(new DesktopUpdateService(VelopackUpdateBackend.CreateForCurrentPlatform(),
        new VibeRe.Lifecycle.ManagedServerShutdownGuard(AppContext.BaseDirectory))) { }

    public UpdatesWindow(DesktopUpdateService updates)
    {
        _updates = updates;
        InitializeComponent();
        _updates.StateChanged += OnStatusChanged;
        Closing += (_, args) => args.Cancel = _updates.State.Stage == UpdateStage.Applying;
        Closed += (_, _) => _updates.StateChanged -= OnStatusChanged;
        Render(_updates.State);
    }

    private void OnStatusChanged(UpdateStatus status) => Dispatcher.UIThread.Post(() => Render(status));

    private void Render(UpdateStatus status)
    {
        VersionLabel.Text = "Installed version: " + (status.CurrentVersion ?? "development / portable build") +
            (status.AvailableVersion is null ? "" : "\nAvailable version: " + status.AvailableVersion);
        StatusLabel.Text = status.Message;
        var busy = status.Stage is UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Applying;
        CheckButton.IsEnabled = !busy;
        DownloadButton.IsVisible = status.Stage == UpdateStage.Available;
        var ready = status.Stage == UpdateStage.ReadyToRestart;
        ConsentText.IsVisible = Consent.IsVisible = ApplyButton.IsVisible = ready;
        if (!ready) Consent.IsChecked = false;
        ApplyButton.IsEnabled = ready && Consent.IsChecked == true;
        Progress.IsVisible = busy;
        Progress.IsIndeterminate = status.DownloadPercent is null;
        Progress.Value = status.DownloadPercent ?? 0;
    }

    private async void CheckForUpdates(object? sender, RoutedEventArgs args) => await _updates.CheckAsync();
    private async void DownloadUpdate(object? sender, RoutedEventArgs args) => await _updates.DownloadAsync();
    private async void ApplyUpdate(object? sender, RoutedEventArgs args)
    {
        if (Consent.IsChecked == true) await _updates.ApplyAndRestartAsync(userConfirmed: true);
    }
    private void ConsentChanged(object? sender, RoutedEventArgs args) =>
        ApplyButton.IsEnabled = _updates.State.Stage == UpdateStage.ReadyToRestart && Consent.IsChecked == true;
}
