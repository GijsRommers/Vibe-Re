using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using VibeRe.Client.Configuration;

namespace VibeRe.Client;

// Shared UI behavior for desktop and mobile; platform code only supplies storage and a browser opener.
public partial class ConnectionSettingsViewModel(ServerConnectionManager connections, Func<Uri, Task> openBrowser) : ObservableObject
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
    [ObservableProperty] private string _browserUrl = "";
    [ObservableProperty] private bool _needsLogin;
    [ObservableProperty] private bool _startLocalServer;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddServerCommand), nameof(RemoveServerCommand), nameof(SaveAddressCommand),
        nameof(SaveTokenCommand), nameof(RefreshCommand), nameof(LoginApiKeyCommand), nameof(LoginBrowserCommand), nameof(LogoutCommand))]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isBusy;

    public bool CanEdit => !IsBusy;
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
        _ = MonitorAsync(_monitor.Token);
    }

    public void StopMonitoring()
    {
        _monitor?.Cancel();
        _monitor?.Dispose();
        _monitor = null;
        _operation?.Cancel();
    }

    private async Task MonitorAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (IsBusy || SelectedServer is not { StartLocally: true } server) continue;
                if (await LocalServerLauncher.IsReadyAsync(new Uri(server.Address), token)) continue;
                await RunAsync(async ct =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, token);
                    await RefreshCoreAsync(linked.Token);
                });
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    partial void OnSelectedServerChanged(ServerConnection? value)
    {
        ServerAddress = value?.Address ?? "";
        ServerToken = "";
        ApiKey = "";
        BrowserUrl = "";
        NeedsLogin = false;
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
        using var client = await connections.ConnectAsync(SelectedServer!.Name, cancellationToken: ct);
        var result = await client.LoginAsync(key, ct);
        Status = $"{client.Server.Name}: {result.Message}";
        NeedsLogin = VibeReClient.NeedsLogin(result);
    });

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task LoginBrowserAsync() => RunAsync(async ct =>
    {
        using var client = await connections.ConnectAsync(SelectedServer!.Name, cancellationToken: ct);
        var result = await client.LoginWithBrowserAsync(async attempt =>
        {
            BrowserUrl = attempt.SignInUrl;
            Status = $"Log in to Mistral in your browser to authorize {client.Server.Name}. You can also open the URL below.";
            try { await openBrowser(new Uri(attempt.SignInUrl)); }
            catch { /* The visible URL allows manual browser login; continue polling. */ }
        }, ct);
        BrowserUrl = "";
        Status = $"{client.Server.Name}: {result.Message}";
        if (result.AuthStatus is { } status) NeedsLogin = VibeReClient.NeedsLogin(status);
    });

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task LogoutAsync() => RunAsync(async ct =>
    {
        using var client = await connections.ConnectAsync(SelectedServer!.Name, cancellationToken: ct);
        var result = await client.LogoutAsync(ct);
        Status = $"{client.Server.Name}: {result.Message}";
        NeedsLogin = true;
    });

    [RelayCommand]
    private void Cancel() => _operation?.Cancel();

    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        NeedsLogin = false;
        if (SelectedServer is null) { Status = "Add a server to connect."; return; }
        Status = $"Connecting to {SelectedServer.Name}…";
        using var client = await connections.ConnectAsync(SelectedServer.Name, cancellationToken: ct);
        var result = await client.GetStatusAsync(ct);
        Status = $"{client.Server.Name}: {result.Message}";
        NeedsLogin = VibeReClient.NeedsLogin(result);
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
