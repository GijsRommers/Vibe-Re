using VibeRe.Client.Application;
using VibeRe.Contracts;
using VibeRe.Web.Services;
using AuthState = VibeRe.Client.Application.AuthState;
using AuthStatus = VibeRe.Client.Application.AuthStatus;

namespace VibeRe.Web.Pages;

public partial class Home
{
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private Task? _runningTask;
    private ServerSession? _session;
    private AccessStatusReply? _access;
    private AuthStatus? _auth;
    private DeviceReply[] _devices = [];
    private PairingReply? _pairing;
    private bool _busy;
    private bool _error;
    private bool _disposed;
    private bool _showServer;
    private string _message = "Connecting to this server…";
    private string _browserUrl = "";
    private string _serverAddress = "";
    private string _deviceName = "My browser";
    private string _approvalCode = "";
    private string _adminToken = "";
    private bool IsSignedIn => _access?.Authorized == true &&
        _auth?.State is AuthState.Authenticated or AuthState.SubscriptionRequired;

    protected override Task OnInitializedAsync() => RefreshAsync();

    private Task RefreshAsync() => RunAsync(RefreshCoreAsync, "Checking this server…");

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        await CloseSessionAsync();
        _auth = null;
        _access = await Access.GetStatusAsync(cancellationToken);
        _devices = _access.CanManageDevices ? await Access.GetDevicesAsync(cancellationToken) : [];
        if (!_access.Authorized)
        {
            _message = "Pair this browser with your server to continue.";
            return;
        }
        var result = await Startup.StartAsync(cancellationToken: cancellationToken);
        _session = result switch
        {
            ServerSession session => session,
            ServerOptional => throw new BrowserAccessException("This browser requires a server connection."),
            _ => throw new InvalidOperationException("Unknown client startup result.")
        };
        _auth = _session.Status;
        _message = string.IsNullOrWhiteSpace(_auth?.Message) ? "Ready to sign in to Mistral." : _auth.Message;
    }

    private Task LoginAsync() => RunAsync(async cancellationToken =>
    {
        if (_session is null) await RefreshCoreAsync(cancellationToken);
        if (_session is null) throw new BrowserAccessException("Connect this browser to the server first.");
        try
        {
            var result = await _session.Client.LoginWithBrowserAsync(async attempt =>
            {
                _browserUrl = attempt.SignInUrl;
                _message = "Continue in Mistral to finish signing in.";
                if (!_disposed) await InvokeAsync(StateHasChanged);
            }, cancellationToken);
            _auth = result.AuthStatus ?? await _session.Client.GetStatusAsync(cancellationToken);
            _message = result.Message;
            _error = result.State is not VibeRe.Client.Application.BrowserLoginState.Completed;
        }
        finally { _browserUrl = ""; }
    }, "Starting Mistral sign-in…");

    private Task LogoutAsync() => RunAsync(async cancellationToken =>
    {
        if (_session is null) await RefreshCoreAsync(cancellationToken);
        if (_session is null) throw new BrowserAccessException("Refresh your server connection first.");
        _auth = await _session.Client.LogoutAsync(cancellationToken);
        _message = "This server is logged out of Mistral.";
    }, "Logging this server out of Mistral…");

    private Task PairAsync() => RunAsync(async cancellationToken =>
    {
        var name = _deviceName.Trim();
        if (name.Length is 0 or > 80) throw new BrowserAccessException("Enter a device name of 1–80 characters.");
        _pairing = await Access.BeginPairingAsync(name, cancellationToken);
        _message = "Waiting for approval from the server computer…";
        if (!_disposed) await InvokeAsync(StateHasChanged);
        var remaining = _pairing.ExpiresAt - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) throw new BrowserAccessException("This pairing code expired. Get a new code.");
        using var expiry = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        expiry.CancelAfter(remaining > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : remaining);
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), expiry.Token);
                if (await Access.RedeemPairingAsync(_pairing, expiry.Token)) break;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BrowserAccessException("This pairing code expired. Get a new code and try again.");
        }
        finally { _pairing = null; }
        await RefreshCoreAsync(cancellationToken);
    }, "Creating a pairing code…");

    private Task UnlockLocalAsync() => RunAsync(async cancellationToken =>
    {
        var token = _adminToken.Trim();
        _adminToken = "";
        if (token.Length == 0) throw new BrowserAccessException("Enter the server’s configured access token.");
        try { await Access.CreateLocalSessionAsync(token, cancellationToken); }
        finally { token = ""; }
        await RefreshCoreAsync(cancellationToken);
    }, "Unlocking this local server…");

    private Task ApproveAsync() => RunAsync(async cancellationToken =>
    {
        await Access.ApprovePairingAsync(_approvalCode, cancellationToken);
        _approvalCode = "";
        _devices = await Access.GetDevicesAsync(cancellationToken);
        _message = "Device approved. Its browser will connect automatically.";
    }, "Approving this device…");

    private Task RefreshDevicesAsync() => RunAsync(async cancellationToken =>
    {
        _devices = await Access.GetDevicesAsync(cancellationToken);
        _message = "Device list refreshed.";
    }, "Refreshing devices…");

    private Task RevokeAsync(Guid id) => RunAsync(async cancellationToken =>
    {
        await Access.RevokeDeviceAsync(id, cancellationToken);
        await RefreshCoreAsync(cancellationToken);
        _message = "Device access revoked.";
    }, "Revoking device access…");

    private Task DisconnectAsync() => RunAsync(async cancellationToken =>
    {
        await CloseSessionAsync();
        await Access.DisconnectAsync(cancellationToken);
        await RefreshCoreAsync(cancellationToken);
        _message = _access?.Authorized == true
            ? "Browser session removed. This server computer still has trusted local access."
            : "This browser is disconnected. The server’s Mistral sign-in is unchanged.";
    }, "Disconnecting this browser…");

    private void ToggleServer() => _showServer = !_showServer;

    private void OpenServer()
    {
        if (_busy) return;
        try { Navigation.NavigateTo(BrowserServer.NavigationTarget(_serverAddress), forceLoad: true); }
        catch (ArgumentException error)
        {
            _error = true;
            _message = error.Message;
        }
    }

    private void Cancel() => _operation?.Cancel();

    private async Task RunAsync(Func<CancellationToken, Task> action, string message)
    {
        if (_busy || _disposed) return;
        _busy = true;
        _error = false;
        _message = message;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = operation;
        try
        {
            _runningTask = action(operation.Token);
            await _runningTask;
        }
        catch (OperationCanceledException)
        {
            _message = operation.IsCancellationRequested ? "Canceled. You can try again." : "The request timed out. Try again.";
            _error = !operation.IsCancellationRequested;
        }
        catch (Exception error)
        {
            _message = error is BrowserAccessException or ServerConnectionException
                ? error.Message : "Could not reach this server. Check your connection and try again.";
            _error = true;
        }
        finally
        {
            _busy = false;
            _operation = null;
            _runningTask = null;
            _pairing = null;
            _adminToken = "";
            if (!_disposed) await InvokeAsync(StateHasChanged);
        }
    }

    private async Task CloseSessionAsync()
    {
        var previous = _session;
        _session = null;
        if (previous is not null) await previous.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        if (_runningTask is { } pending)
        {
            try { await pending; }
            catch { /* The owning operation reports failures; disposal only waits for cancellation. */ }
        }
        await CloseSessionAsync();
        _pairing = null;
        _adminToken = "";
        _lifetime.Dispose();
    }
}
