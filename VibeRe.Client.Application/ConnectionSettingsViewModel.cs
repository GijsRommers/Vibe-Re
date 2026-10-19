using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VibeRe.Client.Application.Configuration;

namespace VibeRe.Client.Application;

// Shared UI behavior for desktop and mobile; platform code only supplies storage and a browser opener.
public partial class ConnectionSettingsViewModel(
    IServerConnectionManager connections,
    IServerConnector connector,
    ClientStartup startup,
    IBrowserLauncher browser,
    IDevicePairingClient? pairing = null,
    ClientCapabilities? capabilities = null) : ObservableObject
{
    private readonly ClientCapabilities _capabilities = capabilities ?? new(true);
    private bool _applying;
    private CancellationTokenSource? _operation;
    public ObservableCollection<ServerConnection> Servers { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveServerCommand), nameof(SaveAddressCommand), nameof(SaveTokenCommand),
        nameof(RefreshCommand), nameof(LoginApiKeyCommand), nameof(LoginBrowserCommand), nameof(LogoutCommand), nameof(PairDeviceCommand))]
    [NotifyPropertyChangedFor(nameof(CanStartLocally))]
    private ServerConnection? _selectedServer;

    [ObservableProperty] private string _newServerName = "";
    [ObservableProperty] private string _newServerAddress = "https://";
    [ObservableProperty] private string _serverAddress = "";
    [ObservableProperty] private string _serverToken = "";
    [ObservableProperty] private string _pairingDeviceName = "My device";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPairingChallenge))]
    private string _pairingApprovalCode = "";
    [ObservableProperty] private string _pairingInstructions = "";
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
        nameof(SaveTokenCommand), nameof(RefreshCommand), nameof(LoginApiKeyCommand), nameof(LoginBrowserCommand), nameof(LogoutCommand),
        nameof(PairDeviceCommand))]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isBusy;

    public bool CanEdit => !IsBusy;
    public bool HasBrowserUrl => BrowserUrl.Length != 0;
    public bool HasPairingChallenge => PairingApprovalCode.Length != 0;
    public bool CanStartLocally => _capabilities.SupportsLocalServer &&
        SelectedServer is { } server && global::VibeRe.Client.Application.ServerAddress.IsLoopback(server.Address);
    private bool CanConnect() => !IsBusy && SelectedServer is not null;
    private bool CanPair() => CanConnect() && pairing is not null;

    public Task InitializeAsync() => UpdateAsync(connections.LoadAsync);

    public void CancelPending() => _operation?.Cancel();

    partial void OnSelectedServerChanged(ServerConnection? value)
    {
        ServerAddress = value?.Address ?? "";
        ServerToken = "";
        ApiKey = "";
        BrowserUrl = "";
        PairingApprovalCode = "";
        PairingInstructions = "";
        NeedsLogin = false;
        IsSignedIn = false;
        ShowApiKey = false;
        StartLocalServer = value?.StartLocally ?? false;
        if (_applying) return;
        if (value is null)
        {
            CancelPending();
            Status = "Select a server to connect.";
            return;
        }
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
        var server = SelectedServer!;
        var token = ServerToken;
        ServerToken = "";
        return UpdateAsync(ct => connections.SetTokenAsync(server, token, ct));
    }

    [RelayCommand(CanExecute = nameof(CanPair))]
    private Task PairDeviceAsync() => RunAsync(async ct =>
    {
        var server = SelectedServer!;
        var deviceName = PairingDeviceName;
        Status = $"Requesting device access to {server.Name}…";
        var token = await pairing!.PairAsync(server.Address, deviceName, challenge =>
        {
            ct.ThrowIfCancellationRequested();
            PairingApprovalCode = challenge.ApprovalCode;
            PairingInstructions = $"On the computer running {server.Name}, open its web UI locally and approve this code in Manage devices. " +
                $"Expires {challenge.ExpiresAt.ToLocalTime():t}.";
            Status = $"Waiting for {server.Name}'s owner to approve this device.";
            return Task.CompletedTask;
        }, ct);
        ct.ThrowIfCancellationRequested();
        if (SelectedServer?.Id != server.Id)
            throw new InvalidOperationException("The selected server changed. Pair this device again.");
        var configuration = await connections.SetTokenAsync(server, token, ct);
        ct.ThrowIfCancellationRequested();
        PairingApprovalCode = "";
        PairingInstructions = "";
        Apply(configuration);
        // Pairing grants server access only. Provider authentication remains a separate step.
        await WithClientAsync((client, cancellation) => client.GetStatusAsync(cancellation), ct);
    });

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
        var expectedServer = SelectedServer!;
        var connection = await connector.ConnectAsync(expectedServer.Name, cancellationToken: ct);
        await using var client = connection.Client;
        ct.ThrowIfCancellationRequested();
        EnsureSameServer(expectedServer, connection.Server);
        var result = await client.LoginWithBrowserAsync(async attempt =>
        {
            ct.ThrowIfCancellationRequested();
            BrowserUrl = attempt.SignInUrl;
            Status = $"Complete browser login to authorize {connection.Server.Name}. You can also open the URL below.";
            try
            {
                await browser.OpenAsync(new Uri(attempt.SignInUrl));
            }
            catch when (!ct.IsCancellationRequested)
            {
                // The visible URL allows manual browser login; continue polling.
            }
        }, ct);
        ct.ThrowIfCancellationRequested();
        BrowserUrl = "";
        Status = $"{connection.Server.Name}: {result.Message}";
        if (result.AuthStatus is { } status) ApplyStatus(connection.Server.Name, status);
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
        if (SelectedServer is null)
        {
            Status = "Add a server to connect.";
            return;
        }
        var expectedServer = SelectedServer;
        Status = $"Connecting to {expectedServer.Name}…";
        var result = await startup.StartAsync(expectedServer.Name, cancellationToken: ct);
        if (result is ServerOptional)
        {
            ct.ThrowIfCancellationRequested();
            NeedsLogin = true;
            Status = "Server is optional. Log in or refresh to connect.";
            return;
        }

        await using var session = (ServerSession)result;
        ct.ThrowIfCancellationRequested();
        EnsureSameServer(expectedServer, session.Server);
        ApplyStatus(session.Server.Name, session.Status);
    }

    private async Task WithClientAsync(Func<IVibeReClient, CancellationToken, Task<AuthStatus>> action, CancellationToken ct)
    {
        var expectedServer = SelectedServer!;
        NeedsLogin = false;
        IsSignedIn = false;
        var connection = await connector.ConnectAsync(expectedServer.Name, cancellationToken: ct);
        await using var client = connection.Client;
        ct.ThrowIfCancellationRequested();
        EnsureSameServer(expectedServer, connection.Server);
        var status = await action(client, ct);
        ct.ThrowIfCancellationRequested();
        ApplyStatus(connection.Server.Name, status);
    }

    private static void EnsureSameServer(ServerConnection expected, ServerConnection actual)
    {
        // A profile may be edited or recreated by another client while this UI still displays it.
        // Never send provider credentials or commands to the replacement merely because its name matches.
        if (actual.Id != expected.Id ||
            global::VibeRe.Client.Application.ServerAddress.Normalize(actual.Address) !=
            global::VibeRe.Client.Application.ServerAddress.Normalize(expected.Address))
            throw new ServerConnectionException("The server settings changed. Reopen the app before trying again.");
    }

    private void ApplyStatus(string serverName, AuthStatus status)
    {
        Status = $"{serverName}: {status.Message}";
        NeedsLogin = status.NeedsLogin;
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
        finally
        {
            _applying = false;
        }
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
        try
        {
            await action(operation.Token);
        }
        catch (Exception e)
        {
            if (_operation == operation)
            {
                Status = e switch
                {
                    OperationCanceledException => "Canceled.",
                    ServerConnectionException => e.Message,
                    ArgumentException or LocalServerStartupException or InvalidOperationException => e.Message,
                    _ => "Could not access saved server settings or credential storage."
                };
            }
        }
        finally
        {
            if (_operation == operation)
            {
                BrowserUrl = "";
                PairingApprovalCode = "";
                PairingInstructions = "";
                _operation = null;
                IsBusy = false;
            }
        }
    }
}
