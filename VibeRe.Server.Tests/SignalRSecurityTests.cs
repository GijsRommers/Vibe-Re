using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using VibeRe.Server.Services;
using Microsoft.AspNetCore.SignalR.Client;
using VibeRe.Client.Infrastructure;
using VibeRe.Contracts;
using VibeRe.Server.Application.Providers.Auth;

namespace VibeRe.Server.Tests;

public class SignalRSecurityTests
{
    [Theory]
    [InlineData("POST", "/negotiate?negotiateVersion=1", null)]
    [InlineData("POST", "/negotiate?negotiateVersion=1", "wrong-token")]
    [InlineData("GET", "?id=connection", null)]
    [InlineData("GET", "?id=connection", "wrong-token")]
    [InlineData("POST", "?id=connection", null)]
    [InlineData("DELETE", "?id=connection", null)]
    public async Task EveryHttpTransportStageRequiresConfiguredToken(string method, string suffix, string? token)
    {
        using var server = new SignalRTestServer();
        using var http = server.CreateClient();
        using var request = Request(method, suffix, token);
        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(server.Secrets.Values);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-token")]
    public async Task ActualWebSocketUpgradeRequiresConfiguredToken(string? token)
    {
        using var server = new SignalRTestServer();
        var client = server.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            if (token is not null) request.Headers.Authorization = "Bearer " + token;
        };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ConnectAsync(new Uri("ws://localhost" + ServerProtocol.AuthHubPath), CancellationToken.None));
        Assert.Contains("401", error.Message);
        Assert.Empty(server.Secrets.Values);
    }

    [Theory]
    [InlineData("127.0.0.1", "localhost", true)]
    [InlineData("::1", "localhost", true)]
    [InlineData("127.0.0.1", "127.0.0.1", true)]
    [InlineData("127.0.0.1", "attacker.example", false)]
    [InlineData("203.0.113.19", "localhost", false)]
    [InlineData("203.0.113.19", "127.0.0.1", false)]
    [InlineData(null, "localhost", false)]
    public async Task TokenlessAccessRequiresBothLoopbackSocketAndLoopbackHost(string? peer, string host, bool allowed)
    {
        using var server = new SignalRTestServer(token: null, remoteIp: peer);
        using var http = server.CreateClient();
        using var request = Request("POST", "/negotiate?negotiateVersion=1");
        request.Headers.Host = host;
        var response = await http.SendAsync(request);
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("server-token")]
    public async Task ForeignOriginIsDeniedEvenOnLoopback(string? token)
    {
        using var server = new SignalRTestServer(token: token);
        using var http = server.CreateClient();
        using var request = Request("POST", "/negotiate?negotiateVersion=1", token);
        request.Headers.Add("Origin", "https://attacker.example");
        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Theory]
    [InlineData("http://localhost", HttpStatusCode.OK)]
    [InlineData("http://localhost:80", HttpStatusCode.OK)]
    [InlineData("http://localhost:9999", HttpStatusCode.Forbidden)]
    [InlineData("https://localhost", HttpStatusCode.Forbidden)]
    [InlineData("null", HttpStatusCode.Forbidden)]
    public async Task BrowserOriginMustMatchExactServerOrigin(string origin, HttpStatusCode expected)
    {
        using var server = new SignalRTestServer();
        using var http = server.CreateClient();
        using var request = Request("POST", "/negotiate?negotiateVersion=1", "server-token");
        request.Headers.Add("Origin", origin);
        Assert.Equal(expected, (await http.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task CrossOriginPreflightDoesNotGrantCorsAccess()
    {
        using var server = new SignalRTestServer();
        using var http = server.CreateClient();
        using var request = Request("OPTIONS", "/negotiate?negotiateVersion=1");
        request.Headers.Add("Origin", "https://attacker.example");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization");
        var response = await http.SendAsync(request);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Headers"));
    }

    [Theory]
    [InlineData("POST", "/negotiate?negotiateVersion=1&access_token=server-token")]
    [InlineData("GET", "?id=connection&access_token=server-token")]
    [InlineData("POST", "?id=connection&access_token=server-token")]
    [InlineData("DELETE", "?id=connection&access_token=server-token")]
    public async Task QueryTokenDoesNotAuthorizeNegotiateOrLongPolling(string method, string suffix)
    {
        using var server = new SignalRTestServer();
        using var http = server.CreateClient();
        using var request = Request(method, suffix);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task QueryTokenAuthorizesOnlyBrowserEventStreamAtHubEndpoint()
    {
        using var server = new SignalRTestServer();
        using var http = server.CreateClient();
        // An unknown connection ID passes auth but is rejected by the SignalR dispatcher.
        using var stream = Request("GET", "?id=unknown&access_token=server-token");
        stream.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(stream)).StatusCode);
        using var negotiate = Request("POST", "/negotiate?negotiateVersion=1&access_token=server-token");
        negotiate.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(negotiate)).StatusCode);
        using var invalidHeader = Request("GET", "?id=unknown&access_token=server-token", "wrong-token");
        invalidHeader.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(invalidHeader)).StatusCode);
    }

    [Theory]
    [InlineData("text/event-stream;q=0")]
    [InlineData("text/event-stream;charset=utf-8")]
    public async Task ParameterizedAcceptDoesNotAuthorizeLongPollingWithQueryToken(string accept)
    {
        using var server = new SignalRTestServer();
        using var http = server.CreateClient();
        using var request = Request("GET", "?id=unknown&access_token=server-token");
        request.Headers.TryAddWithoutValidation("Accept", accept);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(request)).StatusCode);
    }

    [Theory]
    [InlineData(null, "http", HttpStatusCode.Forbidden)]
    [InlineData(null, "https", HttpStatusCode.Forbidden)]
    [InlineData("server-token", "http", HttpStatusCode.Forbidden)]
    [InlineData("server-token", "https", HttpStatusCode.OK)]
    public async Task RemoteConnectionsRequireBothConfiguredTokenAndHttps(string? token, string scheme, HttpStatusCode expected)
    {
        using var server = new SignalRTestServer(token: token, remoteIp: "203.0.113.19");
        using var http = server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{scheme}://server.example{ServerProtocol.AuthHubPath}/negotiate?negotiateVersion=1");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(expected, (await http.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task NativeClientAndRawSignalRRoundTripOverRealKestrelWebSockets()
    {
        using var server = new SignalRTestServer();
        server.UseKestrel(0);
        using var http = server.CreateClient();
        var address = http.BaseAddress!.GetLeftPart(UriPartial.Authority);
        using var cli = new VibeReClient(address, "server-token");
        Assert.Equal(VibeRe.Client.Application.AuthState.Authenticated, (await cli.LoginAsync("websocket-key")).State);
        await using var desktop = new HubConnectionBuilder().WithUrl(address + ServerProtocol.AuthHubPath, options =>
        {
            options.Transports = HttpTransportType.WebSockets;
            options.SkipNegotiation = true;
            options.AccessTokenProvider = () => Task.FromResult<string?>("server-token");
        }).Build();
        await desktop.StartAsync();
        Assert.Equal(AuthState.Authenticated, (await desktop.InvokeAsync<AuthStatusReply>("GetStatus")).State);
        await desktop.InvokeAsync<AuthStatusReply>("Logout");
        Assert.Equal(VibeRe.Client.Application.AuthState.SignedOut, (await cli.GetStatusAsync()).State);
    }

    [Theory]
    [InlineData("CONNECT", true, 204)]
    [InlineData("CONNECT", false, 401)]
    [InlineData("GET", true, 204)]
    [InlineData("GET", false, 401)]
    public async Task QueryTokenSupportsRecognizedHttp2WebSocketsButNotOrdinaryConnect(string method, bool websocket, int expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost");
        context.Request.Path = ServerProtocol.AuthHubPath;
        context.Request.QueryString = new QueryString("?access_token=server-token");
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Features.Set<IHttpWebSocketFeature>(new WebSocketFeature(websocket));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Server:AccessToken"] = "server-token"
        }).Build();
        var middleware = new ServerAccessMiddleware(http =>
        {
            http.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        }, configuration);
        await middleware.InvokeAsync(context);
        Assert.Equal(expected, context.Response.StatusCode);
    }

    private sealed class WebSocketFeature(bool isWebSocket) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => isWebSocket;
        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => throw new NotSupportedException();
    }

    [Fact]
    public async Task NativeBrowserLoginResumesAfterServerSilenceTimeoutWithoutStartingAgain()
    {
        using var server = new SignalRTestServer { KeepAliveInterval = TimeSpan.FromMinutes(2) };
        server.Browser.ExchangeResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        server.UseKestrel(0);
        using var http = server.CreateClient();
        using var client = new VibeReClient(http.BaseAddress!.GetLeftPart(UriPartial.Authority), "server-token");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        var opened = 0;
        var login = client.LoginWithBrowserAsync(_ =>
        {
            Interlocked.Increment(ref opened);
            return Task.CompletedTask;
        }, deadline.Token);
        await server.Browser.ExchangeStarted.Task.WaitAsync(deadline.Token);
        // Leave the production client's 30-second timeout unchanged and withhold server pings.
        await Task.Delay(TimeSpan.FromSeconds(35), deadline.Token);
        server.Browser.ExchangeResult.SetResult("browser-key");
        var result = await login;
        Assert.Equal(VibeRe.Client.Application.BrowserLoginState.Completed, result.State);
        Assert.True(server.HubRequests.Count >= 2, "The client must actually reconnect after server silence.");
        Assert.Equal(1, opened);
        Assert.Equal(1, server.Browser.StartCount);
        Assert.Contains("browser-key", server.Secrets.Values.Values);
    }

    [Fact]
    public async Task NativeClientNeverFollowsAWebSocketRedirectToAnotherServer()
    {
        using var destination = new SignalRTestServer();
        destination.UseKestrel(0);
        using var destinationHttp = destination.CreateClient();
        using var source = new SignalRTestServer();
        source.RedirectHubTo = destinationHttp.BaseAddress!.GetLeftPart(UriPartial.Authority) + ServerProtocol.AuthHubPath;
        source.UseKestrel(0);
        using var sourceHttp = source.CreateClient();
        using var client = new VibeReClient(sourceHttp.BaseAddress!.GetLeftPart(UriPartial.Authority), "server-token");
        await Assert.ThrowsAsync<VibeRe.Client.Application.ServerConnectionException>(() => client.LoginAsync("redirect-secret-key"));
        Assert.NotEmpty(source.HubRequests);
        Assert.Empty(destination.HubRequests);
        Assert.Empty(destination.Secrets.Values);
        Assert.Empty(source.Secrets.Values);
    }

    [Fact]
    public async Task NativeBrowserLoginResumesSameAttemptAfterAbruptWebSocketDisconnect()
    {
        using var server = new SignalRTestServer();
        server.Browser.PollState = BrowserPollState.Pending;
        server.UseKestrel(0);
        using var http = server.CreateClient();
        using var client = new VibeReClient(http.BaseAddress!.GetLeftPart(UriPartial.Authority), "server-token");
        var opened = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var login = client.LoginWithBrowserAsync(_ =>
        {
            Interlocked.Increment(ref opened);
            return Task.CompletedTask;
        }, deadline.Token);
        await server.Browser.Polled.Task.WaitAsync(deadline.Token);
        // Abort the Kestrel socket itself, rather than sending a graceful hub Close message.
        Assert.Single(server.WebSocketRequests).Value.Abort();
        server.Browser.PollState = BrowserPollState.Completed;
        server.Clock.Advance(TimeSpan.FromSeconds(4));
        var result = await login;
        Assert.Equal(VibeRe.Client.Application.BrowserLoginState.Completed, result.State);
        Assert.Equal(1, opened);
        Assert.Equal(1, server.Browser.StartCount);
        Assert.Contains("browser-key", server.Secrets.Values.Values);
    }

    [Fact]
    public async Task BrowserStyleQueryTokenWebSocketRoundTripUsesSamePublicHub()
    {
        using var server = new SignalRTestServer();
        server.UseKestrel(0);
        using var http = server.CreateClient();
        var uri = new UriBuilder(http.BaseAddress!)
        {
            Scheme = "ws", Path = ServerProtocol.AuthHubPath, Query = "access_token=server-token"
        }.Uri;
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", http.BaseAddress!.GetLeftPart(UriPartial.Authority));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await socket.ConnectAsync(uri, deadline.Token);
        await Send(socket, "{\"protocol\":\"json\",\"version\":1}\u001e", deadline.Token);
        Assert.Equal("{}\u001e", await Receive(socket, deadline.Token));
        await Send(socket, "{\"type\":1,\"invocationId\":\"1\",\"target\":\"GetStatus\",\"arguments\":[]}\u001e", deadline.Token);
        var payload = await Receive(socket, deadline.Token);
        using var reply = JsonDocument.Parse(payload.TrimEnd('\u001e'));
        Assert.Equal(3, reply.RootElement.GetProperty("type").GetInt32());
        Assert.Equal(0, reply.RootElement.GetProperty("result").GetProperty("state").GetInt32());
        Assert.DoesNotContain("server-token", payload);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Test complete", deadline.Token);
    }

    private static HttpRequestMessage Request(string method, string suffix, string? token = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), ServerProtocol.AuthHubPath + suffix);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static Task Send(ClientWebSocket socket, string text, CancellationToken cancellationToken) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cancellationToken);

    private static async Task<string> Receive(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(buffer, cancellationToken);
            stream.Write(buffer, 0, received.Count);
        } while (!received.EndOfMessage);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
