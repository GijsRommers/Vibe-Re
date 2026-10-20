using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using VibeRe.Client.SignalR;
using Contracts = VibeRe.Contracts;

namespace VibeRe.Client.Infrastructure;

public sealed class VibeReClient : IVibeReClient, VibeRe.Client.Application.Workspace.IWorkspaceClient, IDisposable
{
    private readonly SignalRAuthClient _client;
    private readonly HttpMessageInvoker _http;

    public VibeReClient(string address, string? accessToken = null, HttpMessageHandler? httpHandler = null)
    {
        var endpoint = new Uri(new Uri(ServerAddress.Normalize(address)), Contracts.ServerProtocol.AuthHubPath);
        _http = new HttpMessageInvoker(httpHandler ?? new SocketsHttpHandler { AllowAutoRedirect = false });
        var connection = new HubConnectionBuilder().WithUrl(endpoint, options =>
        {
            options.AccessTokenProvider = () => Task.FromResult(accessToken);
            // Avoid both SignalR negotiation redirects and HTTP redirects during the WebSocket upgrade.
            options.Transports = httpHandler is null ? HttpTransportType.WebSockets : HttpTransportType.LongPolling;
            options.SkipNegotiation = httpHandler is null;
            if (httpHandler is not null) options.HttpMessageHandlerFactory = _ => new SharedHandler(_http, endpoint);
            else options.WebSocketFactory = async (context, cancellationToken) =>
            {
                var socket = new ClientWebSocket();
                socket.Options.CollectHttpResponseDetails = true;
                if (!string.IsNullOrWhiteSpace(accessToken))
                    socket.Options.SetRequestHeader("Authorization", "Bearer " + accessToken);
                try
                {
                    await socket.ConnectAsync(context.Uri, _http, cancellationToken);
                    return socket;
                }
                catch (Exception error)
                {
                    var status = socket.HttpStatusCode;
                    socket.Dispose();
                    if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                        throw new HttpRequestException("Server access denied.", error, status);
                    throw;
                }
            };
        }).WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)]).Build();
        _client = new SignalRAuthClient(connection);
    }

    public Task<AuthStatus> LoginAsync(string apiKey, CancellationToken cancellationToken = default) =>
        _client.LoginAsync(apiKey, cancellationToken);

    public Task<AuthStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        _client.GetStatusAsync(cancellationToken);

    public Task<AuthStatus> LogoutAsync(CancellationToken cancellationToken = default) =>
        _client.LogoutAsync(cancellationToken);

    public Task<BrowserLoginResult> LoginWithBrowserAsync(Func<BrowserLoginResult, Task> showBrowser,
        CancellationToken cancellationToken = default) => _client.LoginWithBrowserAsync(showBrowser, cancellationToken);

    public Task<VibeRe.Client.Application.Workspace.WorkspaceSnapshot> GetWorkspaceAsync(CancellationToken ct = default) => _client.GetWorkspaceAsync(ct);
    public Task<VibeRe.Client.Application.Workspace.ThreadState> ReadThreadAsync(Guid id, CancellationToken ct = default) => _client.ReadThreadAsync(id, ct);
    public Task<VibeRe.Client.Application.Workspace.ThreadState> CreateThreadAsync(Guid id, string title, string? prompt, CancellationToken ct = default) => _client.CreateThreadAsync(id, title, prompt, ct);
    public Task<VibeRe.Client.Application.Workspace.ThreadState> SendThreadAsync(Guid id, Guid commandId, string text, CancellationToken ct = default) => _client.SendThreadAsync(id, commandId, text, ct);
    public Task StopThreadAsync(Guid id, CancellationToken ct = default) => _client.StopThreadAsync(id, ct);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _http.Dispose();
        }
    }

    // Retain synchronous disposal for existing native callers.
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    // Reconnect creates a new HTTP pipeline without prematurely disposing an injected handler.
    private sealed class SharedHandler(HttpMessageInvoker http, Uri endpoint) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.GetLeftPart(UriPartial.Authority) != endpoint.GetLeftPart(UriPartial.Authority))
                throw new HttpRequestException("A server connection cannot redirect to another destination.");
            return http.SendAsync(request, cancellationToken);
        }
    }
}
