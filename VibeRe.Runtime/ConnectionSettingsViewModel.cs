using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using VibeRe.Runtime.Configuration;
using VibeRe.Protocol;

namespace VibeRe.Runtime;

// Shared UI behavior for desktop and mobile; platform code only supplies storage and a browser opener.
public partial class ConnectionSettingsViewModel(
    IServerConnectionManager connections,
    IServerConnector connector,
    ClientStartup startup,
    IBrowserLauncher browser) : ObservableObject
{
    private bool _applying;
    private CancellationTokenSource? _operation;
    public ObservableCollection<ServerConnection> Servers { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveServerCommand), nameof(SaveAddressCommand), nameof(SaveTokenCommand),
        nameof(RefreshCommand), nameof(LoginApiKeyCommand), nameof(LoginBrowserCommand), nameof(LogoutCommand))]
    [NotifyPropertyChangedFor(nameof(CanStartLocally))]
    private ServerConnection? _selectedServer;

    [ObservableProperty] private string _newServerName = "";
    [ObservableProperty] private string _newServerAddress = "https://";
    [ObservableProperty] private string _serverAddress = "";
    [ObservableProperty] private string _serverToken = "";
    [ObservableProperty] private string _apiKey = "";
    [ObservableProperty] private string _status = "Select a server to connect.";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBrowserUrl))]
    private string _browserUrl = "";
    [ObservableProperty] private bool _needsLogin;
    [ObservableProperty] private bool _isSignedIn;
    [ObservableProperty] private bool _showApiKey;
    [ObservableProperty] private bool _showServerSettings;
    [ObservableProperty] private bool _startLocalServer;
    [ObservableProperty] private bool _serverRequired = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddServerCommand), nameof(RemoveServerCommand), nameof(SaveAddressCommand),
        nameof(SaveTokenCommand), nameof(RefreshCommand), nameof(LoginApiKeyCommand), nameof(LoginBrowserCommand), nameof(LogoutCommand))]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isBusy;

    public bool CanEdit => !IsBusy;
    public bool HasBrowserUrl => BrowserUrl.Length != 0;
    public bool CanStartLocally => SelectedServer is { } server && new Uri(server.Address).IsLoopback &&
        !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS();
    private bool CanConnect() => !IsBusy && SelectedServer is not null;

    public Task InitializeAsync() => UpdateAsync(connections.LoadAsync);

    public void CancelPending() => _operation?.Cancel();

    partial void OnSelectedServerChanged(ServerConnection? value)
    {
        ServerAddress = value?.Address ?? "";
        ServerToken = "";
        ApiKey = "";
        BrowserUrl = "";
        NeedsLogin = false;
        IsSignedIn = false;
        ShowApiKey = false;
        StartLocalServer = value?.StartLocally ?? false;
        if (_applying) return;
        if (value is null) { CancelPending(); Status = "Select a server to connect."; return; }
        _ = RunAsync(async ct =>
        {
            await connections.SelectAsync(value.Name, ct);
            await RefreshCoreAsync(ct);
        });
    }

    partial void OnStartLocalServerChanged(bool value)
    {
        if (_applying || SelectedServer is null || SelectedServer.StartLocally == value) return;
        _ = UpdateAsync(ct => connections.SetLocalStartupAsync(SelectedServer.Name, value, ct));
    }

    partial void OnServerRequiredChanged(bool value)
    {
        if (_applying) return;
        _ = UpdateAsync(ct => connections.SetServerRequiredAsync(value, ct));
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task AddServerAsync() => UpdateAsync(async ct =>
    {
        var name = NewServerName.Trim();
        await connections.AddAsync(name, NewServerAddress, ct);
        var configuration = await connections.SelectAsync(name, ct);
        ct.ThrowIfCancellationRequested();
        NewServerName = "";
        NewServerAddress = "https://";
        return configuration;
    });

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task RemoveServerAsync() => UpdateAsync(ct => connections.RemoveAsync(SelectedServer!.Name, ct));

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task SaveAddressAsync() => UpdateAsync(ct => connections.UpdateAddressAsync(SelectedServer!.Name, ServerAddress, ct));

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task SaveTokenAsync()
    {
        var token = ServerToken;
        ServerToken = "";
        return UpdateAsync(ct => connections.SetTokenAsync(SelectedServer!.Name, token, ct));
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task RefreshAsync() => RunAsync(ct => WithClientAsync((client, token) => client.GetStatusAsync(token), ct));

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task LoginApiKeyAsync() => RunAsync(async ct =>
    {
        var key = ApiKey;
        ApiKey = "";
        await WithClientAsync((client, token) => client.LoginAsync(key, token), ct);
    });

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task LoginBrowserAsync() => RunAsync(async ct =>
    {
        using var session = await connector.ConnectAsync(SelectedServer!.Name, cancellationToken: ct);
        var result = await session.Client.LoginWithBrowserAsync(async attempt =>
        {
            ct.ThrowIfCancellationRequested();
            BrowserUrl = attempt.SignInUrl;
            Status = $"Log in to Mistral in your browser to authorize {session.Server.Name}. You can also open the URL below.";
            try { await browser.OpenAsync(new Uri(attempt.SignInUrl)); }
            catch { /* The visible URL allows manual browser login; continue polling. */ }
        }, ct);
        ct.ThrowIfCancellationRequested();
        BrowserUrl = "";
        Status = $"{session.Server.Name}: {result.Message}";
        if (result.AuthStatus is { } status) ApplyStatus(session.Server.Name, status);
    });

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task LogoutAsync() => RunAsync(ct => WithClientAsync((client, token) => client.LogoutAsync(token), ct));

    [RelayCommand]
    private void Cancel() => _operation?.Cancel();

    [RelayCommand]
    private void ToggleApiKey() => ShowApiKey = !ShowApiKey;

    [RelayCommand]
    private void ToggleServerSettings() => ShowServerSettings = !ShowServerSettings;

    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        NeedsLogin = false;
        IsSignedIn = false;
        if (SelectedServer is null) { Status = "Add a server to connect."; return; }
        Status = $"Connecting to {SelectedServer.Name}…";
        using var session = await startup.StartAsync(SelectedServer.Name, cancellationToken: ct);
        ct.ThrowIfCancellationRequested();
        if (session is null)
        {
            NeedsLogin = true;
            Status = "Server is optional. Log in or refresh to connect.";
            return;
        }
        ApplyStatus(session.Server.Name, session.Status!);
    }

    private async Task WithClientAsync(Func<IVibeReClient, CancellationToken, Task<AuthStatusReply>> action, CancellationToken ct)
    {
        NeedsLogin = false;
        IsSignedIn = false;
        using var session = await connector.ConnectAsync(SelectedServer!.Name, cancellationToken: ct);
        var status = await action(session.Client, ct);
        ct.ThrowIfCancellationRequested();
        ApplyStatus(session.Server.Name, status);
    }

    private void ApplyStatus(string serverName, AuthStatusReply status)
    {
        Status = $"{serverName}: {status.Message}";
        NeedsLogin = VibeReClient.NeedsLogin(status);
        IsSignedIn = status.State is AuthState.Authenticated or AuthState.SubscriptionRequired;
        if (IsSignedIn) ShowApiKey = false;
    }

    private void Apply(ClientConfiguration config)
    {
        _applying = true;
        try
        {
            ServerRequired = config.ServerRequired;
            Servers.Clear();
            foreach (var server in config.Servers) Servers.Add(server);
            SelectedServer = config.ActiveServer is null ? null : config.Find();
        }
        finally { _applying = false; }
    }

    private Task UpdateAsync(Func<CancellationToken, Task<ClientConfiguration>> update) => RunAsync(async ct =>
    {
        var configuration = await update(ct);
        ct.ThrowIfCancellationRequested();
        Apply(configuration);
        await RefreshCoreAsync(ct);
    });

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        _operation?.Cancel();
        using var operation = new CancellationTokenSource();
        _operation = operation;
        IsBusy = true;
        try { await action(operation.Token); }
        catch (Exception e)
        {
            if (_operation == operation) Status = e switch
            {
                OperationCanceledException => "Canceled.",
                RpcException { StatusCode: StatusCode.Cancelled } when operation.IsCancellationRequested => "Canceled.",
                RpcException { StatusCode: StatusCode.Unavailable } => "Could not connect to this server. Check its address and whether it is running.",
                RpcException rpc => rpc.Status.Detail,
                ArgumentException or LocalServerStartupException or InvalidOperationException => e.Message,
                _ => "Could not access saved server settings or credential storage."
            };
        }
        finally
        {
            if (_operation == operation) { BrowserUrl = ""; _operation = null; IsBusy = false; }
        }
    }
}
