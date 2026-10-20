using ThreadState = VibeRe.Client.Application.Workspace.ThreadState;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VibeRe.Client.Application;
using VibeRe.Client.Application.Configuration;
using VibeRe.Client.Application.Workspace;

namespace VibeRe.Desktop.ViewModels;

public partial class WorkspaceViewModel(ConnectionSettingsViewModel connection, IServerConnector connector) : ObservableObject, IAsyncDisposable
{
    public ConnectionSettingsViewModel Connection { get; } = connection;
    public ObservableCollection<ThreadState> Threads { get; } = [];
    public ObservableCollection<ThreadMessage> Messages { get; } = [];
    public ObservableCollection<ThreadMessage> CockpitMessages { get; } = [];
    private IVibeReClient? _client;
    private ServerConnection? _server;
    private bool _refreshing;
    private (Guid CommandId, Guid ThreadId, Guid ServerId, string Text)? _pendingSend;
    private (Guid CommandId, Guid ServerId, string Text)? _pendingCockpit;
    private bool _applying;
    private readonly CancellationTokenSource _lifetime = new();

    [ObservableProperty] private ThreadState? _selectedThread;
    [ObservableProperty] private string _draft = "";
    [ObservableProperty] private string _cockpitDraft = "";
    [ObservableProperty] private string _repositoryName = "Workspace";
    [ObservableProperty] private string _repositoryPath = "";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private bool _settingsOpen;
    [ObservableProperty] private bool _sending;
    [ObservableProperty] private bool _cockpitRunning;
    [ObservableProperty] private bool _workspaceReady;
    [ObservableProperty] private bool _threadRunning;
    [ObservableProperty] private string _threadTitle = "Workspace";
    [ObservableProperty] private string _worktreePath = "";

    public bool CanWork => WorkspaceReady && Connection.IsSignedIn && !Sending;
    public bool CanSendThread => CanWork && !ThreadRunning;
    public bool CanSendCockpit => CanWork && !CockpitRunning;
    partial void OnWorkspaceReadyChanged(bool value) => NotifyAvailability();
    partial void OnSendingChanged(bool value) => NotifyAvailability();
    partial void OnThreadRunningChanged(bool value) => NotifyAvailability();
    partial void OnCockpitRunningChanged(bool value) => NotifyAvailability();
    private void NotifyAvailability()
    {
        OnPropertyChanged(nameof(CanWork)); OnPropertyChanged(nameof(CanSendThread)); OnPropertyChanged(nameof(CanSendCockpit));
    }
    public bool HasThread => SelectedThread is not null;
    public bool IsEmpty => Messages.Count == 0;
    public bool CockpitEmpty => CockpitMessages.Count == 0;
    public bool HasError => Error.Length > 0;
    public string ThreadCount => Threads.Count == 1 ? "1 thread" : $"{Threads.Count} threads";
    public string ConnectionSummary => Connection.IsSignedIn ? "Mistral connected" : Connection.IsBusy ? "Connecting…" : Connection.NeedsLogin ? "Sign in required" : "Connection needs attention";
    public IEnumerable<ThreadState> FilteredThreads => Threads.Where(x => x.Title.Contains(Search, StringComparison.OrdinalIgnoreCase));

    public async Task InitializeAsync()
    {
        Connection.PropertyChanged += ConnectionChanged;
        await Connection.InitializeAsync();
        await RefreshAsync();
    }
    private void ConnectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ConnectionSummary));
        NotifyAvailability();
        if (e.PropertyName == nameof(ConnectionSettingsViewModel.SelectedServer))
        {
            WorkspaceReady = false;
            Threads.Clear(); Messages.Clear(); CockpitMessages.Clear(); SelectedThread = null;
            OnPropertyChanged(nameof(CockpitEmpty)); OnPropertyChanged(nameof(FilteredThreads)); OnPropertyChanged(nameof(ThreadCount));
        }
    }
    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnSearchChanged(string value) => OnPropertyChanged(nameof(FilteredThreads));
    partial void OnSelectedThreadChanged(ThreadState? value)
    {
        ThreadTitle = value?.Title ?? "Workspace";
        ThreadRunning = value?.IsRunning ?? false;
        WorktreePath = value?.WorktreePath ?? "";
        OnPropertyChanged(nameof(HasThread));
        if (!_applying) Draft = "";
        if (!_applying) { Messages.Clear(); _ = LoadSelectedAsync(); }
    }
    private async Task LoadSelectedAsync()
    {
        var id = SelectedThread?.Id;
        if (id is null) { OnPropertyChanged(nameof(IsEmpty)); return; }
        try
        {
            var client = WorkspaceClient();
            var thread = await client.ReadThreadAsync(id.Value, _lifetime.Token);
            if (SelectedThread?.Id == id) ApplyThread(thread);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Error = error.Message; }
    }
    [RelayCommand]
    private void ToggleSettings() => SettingsOpen = !SettingsOpen;

    [RelayCommand]
    private async Task NewThreadAsync()
    {
        await RunUi(async () =>
        {
            var thread = await WorkspaceClient().CreateThreadAsync(Guid.NewGuid(), "New thread", null, _lifetime.Token);
            Threads.Insert(0, thread);
            SelectedThread = thread;
            OnPropertyChanged(nameof(FilteredThreads)); OnPropertyChanged(nameof(ThreadCount));
        });
    }
    [RelayCommand]
    private async Task SendAsync()
    {
        if (!CanSendThread || string.IsNullOrWhiteSpace(Draft)) return;
        var text = Draft.Trim();
        await RunUi(async () =>
        {
            var client = WorkspaceClient();
            var serverId = _server!.Id;
            var thread = SelectedThread;
            if (_pendingSend is not { } pending || pending.Text != text || pending.ServerId != serverId ||
                (thread is not null && pending.ThreadId != thread.Id))
                _pendingSend = (Guid.NewGuid(), thread?.Id ?? Guid.NewGuid(), serverId, text);
            var request = _pendingSend.Value;
            if (thread is null) thread = await client.CreateThreadAsync(request.ThreadId, text[..Math.Min(text.Length, 60)], null, _lifetime.Token);
            var sent = await client.SendThreadAsync(thread.Id, request.CommandId, text, _lifetime.Token);
            _pendingSend = null;
            if (Connection.SelectedServer?.Id != serverId) return;
            Draft = "";
            _applying = true;
            try { SelectedThread = sent; ApplyThread(sent); }
            finally { _applying = false; }
            await RefreshAsync();
        });
    }
    [RelayCommand]
    private async Task SendCockpitAsync()
    {
        if (!CanSendCockpit || string.IsNullOrWhiteSpace(CockpitDraft)) return;
        var text = CockpitDraft.Trim();
        await RunUi(async () =>
        {
            var client = WorkspaceClient();
            var serverId = _server!.Id;
            if (_pendingCockpit is not { } pending || pending.Text != text || pending.ServerId != serverId)
                _pendingCockpit = (Guid.NewGuid(), serverId, text);
            var thread = await client.SendThreadAsync(CockpitThread.Id, _pendingCockpit.Value.CommandId, text, _lifetime.Token);
            _pendingCockpit = null;
            if (Connection.SelectedServer?.Id != serverId) return;
            CockpitDraft = "";
            ReplaceMessages(CockpitMessages, thread.Messages);
            CockpitRunning = thread.IsRunning;
            OnPropertyChanged(nameof(CockpitEmpty));
        });
    }
    [RelayCommand]
    private Task StopAsync() => RunUi(async () =>
    {
        if (SelectedThread is { } thread) await WorkspaceClient().StopThreadAsync(thread.Id, _lifetime.Token);
    });
    [RelayCommand]
    private Task StopCockpitAsync() => RunUi(() => WorkspaceClient().StopThreadAsync(CockpitThread.Id, _lifetime.Token));
    [RelayCommand]
    private void OpenReport(Guid? id)
    {
        if (id is not null) SelectedThread = Threads.FirstOrDefault(x => x.Id == id);
    }
    [RelayCommand]
    private async Task RetryAsync()
    {
        await Connection.RefreshCommand.ExecuteAsync(null);
        await RefreshAsync();
    }
    public async Task RefreshAsync()
    {
        if (_refreshing || Connection.IsBusy || _lifetime.IsCancellationRequested) return;
        _refreshing = true;
        var selected = Connection.SelectedServer;
        try
        {
            if (selected is null) return;
            if (_server?.Id != selected.Id || _server.Address != selected.Address)
            {
                if (_client is not null) await _client.DisposeAsync();
                _client = null; _server = null;
                var connected = await connector.ConnectAsync(selected.Name, cancellationToken: _lifetime.Token);
                if (connected.Server.Id != selected.Id || connected.Server.Address != selected.Address)
                { await connected.Client.DisposeAsync(); return; }
                _client = connected.Client; _server = connected.Server;
            }
            var client = WorkspaceClient();
            var snapshot = await client.GetWorkspaceAsync(_lifetime.Token);
            if (Connection.SelectedServer?.Id != selected.Id || Connection.SelectedServer.Address != selected.Address) return;
            RepositoryPath = snapshot.Root;
            RepositoryName = new DirectoryInfo(snapshot.Root).Name;
            WorkspaceReady = true;
            Error = "";
            var previousId = SelectedThread?.Id;
            var children = snapshot.Threads.Where(x => x.Id != CockpitThread.Id).ToArray();
            if (!Threads.Select(x => (x.Id, x.Status, x.Title)).SequenceEqual(children.Select(x => (x.Id, x.Status, x.Title))))
            {
                _applying = true;
                try
                {
                    Threads.Clear(); foreach (var item in children) Threads.Add(item);
                    if (previousId is not null) SelectedThread = Threads.FirstOrDefault(x => x.Id == previousId);
                }
                finally { _applying = false; }
                OnPropertyChanged(nameof(FilteredThreads)); OnPropertyChanged(nameof(ThreadCount));
            }
            if (snapshot.Threads.FirstOrDefault(x => x.Id == CockpitThread.Id) is { } cockpit)
            {
                CockpitRunning = cockpit.IsRunning;
                // Summary responses omit messages. Read the permanent chat independently of the selected child.
                var detail = await client.ReadThreadAsync(CockpitThread.Id, _lifetime.Token);
                if (Connection.SelectedServer?.Id != selected.Id) return;
                ReplaceMessages(CockpitMessages, detail.Messages);
                OnPropertyChanged(nameof(CockpitEmpty));
            }
            if (previousId is { } threadId)
            {
                var detail = await client.ReadThreadAsync(threadId, _lifetime.Token);
                if (SelectedThread?.Id == threadId && Connection.SelectedServer?.Id == selected.Id) ApplyThread(detail);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Error = error.Message; WorkspaceReady = false; }
        finally { _refreshing = false; }
    }
    private IWorkspaceClient WorkspaceClient()
    {
        if (_server?.Id != Connection.SelectedServer?.Id || _server?.Address != Connection.SelectedServer?.Address)
            throw new ServerConnectionException("The connection changed. Wait for the workspace to reconnect.");
        return _client as IWorkspaceClient ?? throw new ServerConnectionException("Connect to a local server to open this workspace.");
    }
    private void ApplyThread(ThreadState thread)
    {
        ThreadTitle = thread.Title; ThreadRunning = thread.IsRunning; WorktreePath = thread.WorktreePath ?? "";
        ReplaceMessages(Messages, thread.Messages);
        OnPropertyChanged(nameof(IsEmpty));
    }
    private static void ReplaceMessages(ObservableCollection<ThreadMessage> target, IReadOnlyList<ThreadMessage> source)
    {
        if (target.Select(x => x.Id).SequenceEqual(source.Select(x => x.Id))) return;
        // Preserve the existing controls while live activity is appended.
        var prefix = target.Count <= source.Count && target.Select(x => x.Id).SequenceEqual(source.Take(target.Count).Select(x => x.Id));
        if (!prefix) target.Clear();
        foreach (var message in source.Skip(target.Count)) target.Add(message);
    }
    private async Task RunUi(Func<Task> action)
    {
        Sending = true; Error = "";
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Error = error.Message; }
        finally { Sending = false; }
    }
    public async ValueTask DisposeAsync()
    {
        Connection.PropertyChanged -= ConnectionChanged;
        Connection.CancelPending();
        await _lifetime.CancelAsync();
        if (_client is not null) await _client.DisposeAsync();
        _lifetime.Dispose();
    }
}
