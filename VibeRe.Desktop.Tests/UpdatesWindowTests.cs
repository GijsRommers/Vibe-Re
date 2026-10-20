using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using VibeRe.Desktop.Views;
using VibeRe.Lifecycle;
using VibeRe.Updates;
using Velopack;

[assembly: AvaloniaTestApplication(typeof(VibeRe.Desktop.Tests.TestApplication))]

namespace VibeRe.Desktop.Tests;

public sealed class TestApplication : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApplication>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class UpdatesWindowTests
{
    [AvaloniaFact]
    public async Task RestartRequiresSeparateExplicitConsent()
    {
        var backend = new FakeBackend();
        var guard = new FakeGuard();
        var service = new DesktopUpdateService(backend, guard);
        var window = new UpdatesWindow(service);
        window.Show();
        try
        {
            Assert.False(window.FindControl<Button>("ApplyButton")!.IsVisible);
            await service.CheckAsync();
            await service.DownloadAsync();
            Dispatcher.UIThread.RunJobs();
            var apply = window.FindControl<Button>("ApplyButton")!;
            Assert.True(apply.IsVisible);
            Assert.False(apply.IsEnabled);
            Assert.Equal(0, backend.ApplyCount);
            window.FindControl<CheckBox>("Consent")!.IsChecked = true;
            Assert.True(apply.IsEnabled);
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, backend.ApplyCount);
            Assert.Equal(1, guard.Calls);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task CheckCannotSilentlyDownloadOrApply()
    {
        var backend = new FakeBackend();
        var service = new DesktopUpdateService(backend, new FakeGuard());
        var window = new UpdatesWindow(service);
        window.Show();
        try
        {
            window.FindControl<Button>("CheckButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(UpdateStage.Available, service.State.Stage);
            Assert.True(window.FindControl<Button>("DownloadButton")!.IsVisible);
            Assert.Equal(0, backend.DownloadCount);
            Assert.Equal(0, backend.ApplyCount);
            await service.ApplyAndRestartAsync(false);
            Assert.Equal(0, backend.ApplyCount);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task UnpackagedBuildShowsReasonAndLeavesRestartHidden()
    {
        var backend = new FakeBackend { UnavailableReason = "Install the VibeRe desktop package first." };
        var service = new DesktopUpdateService(backend, new FakeGuard());
        var window = new UpdatesWindow(service);
        window.Show();
        try
        {
            await service.CheckAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Install", window.FindControl<TextBlock>("StatusLabel")!.Text);
            Assert.False(window.FindControl<Button>("ApplyButton")!.IsVisible);
            Assert.True(window.FindControl<Button>("CheckButton")!.IsEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task WindowsCannotCloseWhileServerShutdownIsInProgress()
    {
        var guard = new WaitingGuard();
        var service = new DesktopUpdateService(new FakeBackend(), guard);
        var main = new MainWindow { UpdateService = service };
        var window = new UpdatesWindow(service);
        main.Show();
        window.Show(main);
        await service.CheckAsync();
        await service.DownloadAsync();
        var applying = service.ApplyAndRestartAsync(true);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(UpdateStage.Applying, service.State.Stage);
        window.Close();
        main.Close();
        Assert.True(window.IsVisible);
        Assert.True(main.IsVisible);
        guard.Release.SetException(new InvalidOperationException("Canceled test shutdown"));
        await applying;
        Dispatcher.UIThread.RunJobs();
        window.Close();
        main.Close();
        Assert.False(window.IsVisible);
        Assert.False(main.IsVisible);
    }

    private sealed class WaitingGuard : IServerShutdownGuard
    {
        public TaskCompletionSource<IAsyncDisposable> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IAsyncDisposable> AcquireUpdateLeaseAsync(CancellationToken cancellationToken) => Release.Task;
    }

    private sealed class FakeBackend : IUpdateBackend
    {
        public SemanticVersion? CurrentVersion => SemanticVersion.Parse("1.0.0");
        public string RuntimeIdentifier => "linux-x64";
        public string Channel => "linux-x64-stable";
        public string? UnavailableReason { get; init; }
        public int DownloadCount { get; private set; }
        public int ApplyCount { get; private set; }
        public Task<UpdateCandidate?> CheckAsync(CancellationToken cancellationToken) =>
            Task.FromResult<UpdateCandidate?>(new(SemanticVersion.Parse("1.0.1"), Channel, RuntimeIdentifier));
        public Task DownloadAsync(UpdateCandidate candidate, Action<int> progress, CancellationToken cancellationToken)
        { DownloadCount++; progress(100); return Task.CompletedTask; }
        public Task VerifyAsync(UpdateCandidate candidate, CancellationToken cancellationToken) => Task.CompletedTask;
        public void ApplyAndRestart(UpdateCandidate candidate)
        { ApplyCount++; throw new InvalidOperationException("Simulated process exit"); }
    }
    private sealed class FakeGuard : IServerShutdownGuard
    {
        public int Calls { get; private set; }
        public Task<IAsyncDisposable> AcquireUpdateLeaseAsync(CancellationToken cancellationToken)
        { Calls++; return Task.FromResult<IAsyncDisposable>(new Lease()); }
        private sealed class Lease : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
}
