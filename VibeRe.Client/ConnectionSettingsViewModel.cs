using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using VibeRe.Client.Configuration;
using VibeRe.Protocol;

namespace VibeRe.Client;

// Shared UI behavior for desktop and mobile; platform code only supplies storage and a browser opener.
public partial class ConnectionSettingsViewModel(
    IServerConnectionManager connections,
    IServerConnector connector,
    LocalServerMonitor monitor,
    IBrowserLauncher browser) : ObservableObject
{
    private bool _applying;
    private bool _syncingLaunchOption;
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _monitor;
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoginTitle))]
    private bool _isSignedIn;
    [ObservableProperty] private bool _showApiKey;
    [ObservableProperty] private bool _showServerSettings;
    [ObservableProperty] private bool _startLocalServer;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddServerCommand), nameof(RemoveServerCommand), nameof(SaveAddressCommand),
        nameof(SaveTokenCommand), nameof(RefreshCommand), nameof(LoginApiKeyCommand), nameof(LoginBrowserCommand), nameof(LogoutCommand))]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isBusy;

    public bool CanEdit => !IsBusy;
    public string LoginTitle => IsSignedIn ? "You're connected" : "Connect to Mistral";
    public bool HasBrowserUrl => BrowserUrl.Length != 0;
    public bool CanStartLocally => SelectedServer is { } server && new Uri(server.Address).IsLoopback &&
        !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS();
    private bool CanConnect() => !IsBusy && SelectedServer is not null;
    private bool CanAdd() => !IsBusy;

    public Task InitializeAsync() => RunAsync(async ct =>
    {
        Apply(await connections.LoadAsync(ct));
        await RefreshCoreAsync(ct);
    });

    public void StartMonitoring()
    {
        if (_monitor is not null) return;
        _monitor = new();
        _ = monitor.RunAsync(() => IsBusy ? null : SelectedServer,
            token => RunAsync(async ct =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, token);
                await RefreshCoreAsync(linked.Token);
            }), _monitor.Token);
    }

    public void StopMonitoring()
    {
        _monitor?.Cancel();
        _monitor?.Dispose();
        _monitor = null;
        _operation?.Cancel();
    }

    partial void OnSelectedServerChanged(ServerConnection? value)
    {
        ServerAddress = value?.Address ?? "";
        ServerToken = "";
        ApiKey = "";
        BrowserUrl = "";
        NeedsLogin = false;
        IsSignedIn = false;
        ShowApiKey = false;
        _syncingLaunchOption = true;
        StartLocalServer = value?.StartLocally ?? false;
        _syncingLaunchOption = false;
        if (!_applying && value is not null)
            _ = RunAsync(async ct =>
            {
                await connections.SelectAsync(value.Name, ct);
                await RefreshCoreAsync(ct);
            });
    }

    partial void OnStartLocalServerChanged(bool value)
    {
        if (_syncingLaunchOption || _applying || SelectedServer is null) return;
        _ = RunAsync(async ct =>
        {
            Apply(await connections.SetLocalStartupAsync(SelectedServer.Name, value, ct));
            await RefreshCoreAsync(ct);
        });
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private Task AddServerAsync() => RunAsync(async ct =>
    {
        await connections.AddAsync(NewServerName, NewServerAddress, ct);
        Apply(await connections.SelectAsync(NewServerName.Trim(), ct));
        NewServerName = "";
        NewServerAddress = "https://";
        await RefreshCoreAsync(ct);
    });

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task RemoveServerAsync() => RunAsync(async ct =>
    {
        Apply(await connections.RemoveAsync(SelectedServer!.Name, ct));
        await RefreshCoreAsync(ct);
    });

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task SaveAddressAsync() => RunAsync(async ct =>
    {
        Apply(await connections.UpdateAddressAsync(SelectedServer!.Name, ServerAddress, ct));
        await RefreshCoreAsync(ct);
    });

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task SaveTokenAsync() => RunAsync(async ct =>
    {
        var token = ServerToken;
        ServerToken = "";
        Apply(await connections.SetTokenAsync(SelectedServer!.Name, token, ct));
        await RefreshCoreAsync(ct);
    });

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task RefreshAsync() => RunAsync(RefreshCoreAsync);

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task LoginApiKeyAsync() => RunAsync(async ct =>
    {
        var key = ApiKey;
        ApiKey = "";
        using var client = await connector.ConnectAsync(SelectedServer!.Name, cancellationToken: ct);
        var result = await client.LoginAsync(key, ct);
        ApplyStatus(client.Server.Name, result);
    });

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task LoginBrowserAsync() => RunAsync(async ct =>
    {
        using var client = await connector.ConnectAsync(SelectedServer!.Name, cancellationToken: ct);
        var result = await client.LoginWithBrowserAsync(async attempt =>
        {
            BrowserUrl = attempt.SignInUrl;
            Status = $"Log in to Mistral in your browser to authorize {client.Server.Name}. You can also open the URL below.";
            try { await browser.OpenAsync(new Uri(attempt.SignInUrl)); }
            catch { /* The visible URL allows manual browser login; continue polling. */ }
        }, ct);
        BrowserUrl = "";
        Status = $"{client.Server.Name}: {result.Message}";
        if (result.AuthStatus is { } status) ApplyStatus(client.Server.Name, status);
    });

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task LogoutAsync() => RunAsync(async ct =>
    {
        using var client = await connector.ConnectAsync(SelectedServer!.Name, cancellationToken: ct);
        var result = await client.LogoutAsync(ct);
        ApplyStatus(client.Server.Name, result);
    });

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
        using var client = await connector.ConnectAsync(SelectedServer.Name, cancellationToken: ct);
        var result = await client.GetStatusAsync(ct);
        ApplyStatus(client.Server.Name, result);
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
            Servers.Clear();
            foreach (var server in config.Servers) Servers.Add(server);
            SelectedServer = config.ActiveServer is null ? null : config.Find();
        }
        finally { _applying = false; }
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        IsBusy = true;
        try { await action(operation.Token); }
        catch (OperationCanceledException) { Status = "Canceled."; }
        catch (RpcException e) { Status = e.StatusCode == StatusCode.Unavailable
            ? "Could not connect to this server. Check its address and whether it is running."
            : e.Status.Detail; }
        catch (ArgumentException e) { Status = e.Message; }
        catch (LocalServerStartupException e) { Status = e.Message; }
        catch { Status = "Could not access saved server settings or credential storage."; }
        finally { BrowserUrl = ""; _operation = null; IsBusy = false; }
    }
}
