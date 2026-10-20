using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VibeRe.Client.Application;
using VibeRe.Client.Application.Configuration;
using VibeRe.Client.Application.Workspace;
using VibeRe.Desktop.ViewModels;
using VibeRe.Desktop.Views;
using ThreadState = VibeRe.Client.Application.Workspace.ThreadState;

namespace VibeRe.Desktop.Tests;

public sealed class WorkspaceWindowTests
{
    [AvaloniaFact]
    public async Task CockpitStaysVisibleWhileSwitchingThreadsAndSettingsPreservesDrafts()
    {
        var fixture = new Fixture();
        var window = new MainWindow { DataContext = fixture.Model };
        window.Show();
        try
        {
            await fixture.Model.RefreshAsync(); Dispatcher.UIThread.RunJobs();
            fixture.Model.CockpitDraft = "Coordinate the next change";
            await fixture.Model.NewThreadCommand.ExecuteAsync(null);
            fixture.Model.Draft = "Implement the change";
            fixture.Model.ToggleSettingsCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(fixture.Model.SettingsOpen);
            Assert.True(window.GetVisualDescendants().OfType<ConnectionSettingsView>().Single().IsEffectivelyVisible);
            fixture.Model.ToggleSettingsCommand.Execute(null);
            Assert.Equal("Implement the change", fixture.Model.Draft);
            Assert.Equal("Coordinate the next change", fixture.Model.CockpitDraft);
            Assert.True(window.FindControl<TextBox>("CockpitInput")!.IsEffectivelyVisible);
            await fixture.Model.SendCockpitCommand.ExecuteAsync(null);
            Assert.Equal(CockpitThread.Id, fixture.Client.LastSentThread);
            Assert.Equal("Implement the change", fixture.Model.Draft);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task RenderWorkspaceAndSettingsAtDesktopAndMinimumSize()
    {
        var fixture = new Fixture();
        var window = new MainWindow { DataContext = fixture.Model };
        window.Show();
        try
        {
            await fixture.Model.RefreshAsync(); Dispatcher.UIThread.RunJobs();
            Assert.False(fixture.Model.SettingsOpen);
            Assert.True(window.FindControl<TextBox>("ThreadInput")!.IsEnabled);
            SaveFrame(window, Path.Combine(Path.GetTempPath(), "vibere-workspace.png"));
            fixture.Model.SettingsOpen = true;
            SaveFrame(window, Path.Combine(Path.GetTempPath(), "vibere-settings.png"));
            fixture.Model.SettingsOpen = false;
            window.Width = 1060; window.Height = 640;
            SaveFrame(window, Path.Combine(Path.GetTempPath(), "vibere-workspace-small.png"));
            Assert.True(window.FindControl<TextBox>("CockpitInput")!.Bounds.Width > 200);
            Assert.True(window.FindControl<TextBox>("ThreadInput")!.Bounds.Width > 300);
        }
        finally { window.Close(); }
    }
    private static void SaveFrame(Window window, string path)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
    private sealed class Fixture
    {
        public FakeClient Client { get; } = new();
        public WorkspaceViewModel Model { get; }
        public Fixture()
        {
            var store = new ConfigStore();
            var connector = new Connector(store, Client);
            var connection = new ConnectionSettingsViewModel(new ServerConnectionManager(store, new Tokens()), connector,
                new ClientStartup(store, connector), new Browser());
            Model = new(connection, connector);
        }
    }
    private sealed class ConfigStore : IClientConfigurationStore
    {
        public ClientConfiguration Current { get; set; } = ClientConfiguration.Default();
        public Task<ClientConfiguration> LoadAsync(CancellationToken ct = default) => Task.FromResult(Current);
        public async Task<ClientConfiguration> UpdateAsync(Func<ClientConfiguration, Task<ClientConfiguration>> update, CancellationToken ct = default) => Current = await update(Current);
    }
    private sealed class Connector(ConfigStore store, FakeClient client) : IServerConnector
    {
        public Task<(ServerConnection Server, IVibeReClient Client)> ConnectAsync(string? name = null, string? tokenOverride = null, CancellationToken cancellationToken = default) => Task.FromResult((store.Current.Find(name), (IVibeReClient)client));
    }
    private sealed class FakeClient : IVibeReClient, IWorkspaceClient
    {
        private readonly List<ThreadState> _threads = [new(CockpitThread.Id, null, "The Cockpit", "idle", null, [])];
        public Guid? LastSentThread;
        public Task<WorkspaceSnapshot> GetWorkspaceAsync(CancellationToken ct = default) => Task.FromResult(new WorkspaceSnapshot("/projects/vibe-re", _threads.ToArray()));
        public Task<ThreadState> ReadThreadAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_threads.Single(x => x.Id == id));
        public Task<ThreadState> CreateThreadAsync(Guid id, string title, string? prompt, CancellationToken ct = default)
        {
            var thread = new ThreadState(id, CockpitThread.Id, title, "idle", null, []); _threads.Add(thread); return Task.FromResult(thread);
        }
        public Task<ThreadState> SendThreadAsync(Guid id, Guid commandId, string text, CancellationToken ct = default)
        { LastSentThread = id; return ReadThreadAsync(id, ct); }
        public Task StopThreadAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<AuthStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(new AuthStatus { State = AuthState.Authenticated, Message = "Mistral connected" });
        public Task<AuthStatus> LoginAsync(string key, CancellationToken ct = default) => GetStatusAsync(ct);
        public Task<AuthStatus> LogoutAsync(CancellationToken ct = default) => GetStatusAsync(ct);
        public Task<BrowserLoginResult> LoginWithBrowserAsync(Func<BrowserLoginResult, Task> show, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Tokens : IServerTokenStore
    {
        public Task<string?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SetAsync(Guid id, string value, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class Browser : IBrowserLauncher { public Task OpenAsync(Uri uri) => Task.CompletedTask; }
}
