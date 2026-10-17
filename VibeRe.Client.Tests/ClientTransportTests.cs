using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using Google.Protobuf;
using Grpc.Core;
using VibeRe.Client.Application;
using VibeRe.Client.Infrastructure;
using Protocol = VibeRe.Protocol;

namespace VibeRe.Client.Tests;

public sealed class ClientTransportTests
{
    [Theory]
    [InlineData(Protocol.AuthState.SignedOut, AuthState.SignedOut, true)]
    [InlineData(Protocol.AuthState.Authenticated, AuthState.Authenticated, false)]
    [InlineData(Protocol.AuthState.InvalidCredential, AuthState.InvalidCredential, true)]
    [InlineData(Protocol.AuthState.SubscriptionRequired, AuthState.SubscriptionRequired, false)]
    [InlineData(Protocol.AuthState.Unavailable, AuthState.Unavailable, false)]
    public async Task AuthenticationRepliesBecomeApplicationStatus(Protocol.AuthState wireState, AuthState state, bool needsLogin)
    {
        using var handler = new ReplyHandler((_, _) => Task.FromResult(Reply(new Protocol.AuthStatusReply
        {
            State = wireState, Message = "Provider status."
        })));
        using IVibeReClient client = new VibeReClient("https://server.example", httpHandler: handler);

        foreach (var result in new[] { await client.LoginAsync("api-key"), await client.GetStatusAsync(), await client.LogoutAsync() })
        {
            Assert.Equal(state, result.State);
            Assert.Equal("Provider status.", result.Message);
            Assert.Equal(needsLogin, result.NeedsLogin);
        }
    }

    [Theory]
    [InlineData(Protocol.BrowserLoginState.Completed, BrowserLoginState.Completed)]
    [InlineData(Protocol.BrowserLoginState.Failed, BrowserLoginState.Failed)]
    [InlineData(Protocol.BrowserLoginState.Expired, BrowserLoginState.Expired)]
    [InlineData(Protocol.BrowserLoginState.Canceled, BrowserLoginState.Canceled)]
    public async Task BrowserRepliesBecomeApplicationResults(Protocol.BrowserLoginState wireState, BrowserLoginState state)
    {
        using var handler = new ReplyHandler((request, _) => Task.FromResult(Reply(Method(request) == "StartBrowserLogin"
            ? StartedAttempt()
            : new Protocol.BrowserLoginReply
            {
                State = wireState, Message = "Browser result.",
                AuthStatus = new() { State = Protocol.AuthState.Authenticated, Message = "Verified." }
            })));
        using IVibeReClient client = new VibeReClient("https://server.example", httpHandler: handler);
        BrowserLoginResult? shown = null;

        var result = await client.LoginWithBrowserAsync(attempt =>
        {
            shown = attempt;
            return Task.CompletedTask;
        });

        Assert.NotNull(shown);
        Assert.Equal(BrowserLoginState.Waiting, shown.State);
        Assert.Equal("https://console.mistral.ai/login", shown.SignInUrl);
        Assert.Equal("Open the browser.", shown.Message);
        Assert.Null(shown.AuthStatus);
        Assert.Equal(state, result.State);
        Assert.Equal("Browser result.", result.Message);
        Assert.Equal(new AuthStatus { State = AuthState.Authenticated, Message = "Verified." }, result.AuthStatus);
    }

    public static IEnumerable<object[]> RpcFailures()
    {
        var statuses = new[] { StatusCode.Cancelled, StatusCode.Unavailable, StatusCode.Unauthenticated,
            StatusCode.InvalidArgument, StatusCode.Internal, StatusCode.DeadlineExceeded };
        var methods = new[] { "Login", "GetStatus", "Logout", "StartBrowserLogin", "PollBrowserLogin" };
        return from method in methods from status in statuses select new object[] { method, status };
    }

    [Theory]
    [MemberData(nameof(RpcFailures))]
    public async Task RpcFailuresBecomeApplicationErrorsEvenWhenServerCancels(string failingMethod, StatusCode status)
    {
        var cleanupCalls = 0;
        using var handler = new ReplyHandler((request, _) =>
        {
            var method = Method(request);
            if (method == failingMethod) return Task.FromResult(Error(status, "Server error detail."));
            if (method == "CancelBrowserLogin") cleanupCalls++;
            return Task.FromResult(Reply(StartedAttempt()));
        });
        using IVibeReClient client = new VibeReClient("https://server.example", httpHandler: handler);
        using var caller = new CancellationTokenSource();

        var error = await Assert.ThrowsAsync<ServerConnectionException>(() => InvokeAsync(client, failingMethod, caller.Token));

        Assert.False(caller.IsCancellationRequested);
        Assert.Equal(status, Assert.IsType<RpcException>(error.InnerException).StatusCode);
        Assert.Equal(status == StatusCode.Unavailable
            ? "Could not connect to this server. Check its address and whether it is running."
            : "Server error detail.", error.Message);
        Assert.Equal(failingMethod == "PollBrowserLogin" ? 1 : 0, cleanupCalls);
    }

    [Theory]
    [InlineData("Login")]
    [InlineData("GetStatus")]
    [InlineData("Logout")]
    [InlineData("StartBrowserLogin")]
    public async Task CallerCancellationBecomesOperationCanceledException(string method)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new ReplyHandler(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("The request should end through cancellation.");
        });
        using IVibeReClient client = new VibeReClient("https://server.example", httpHandler: handler);
        using var caller = new CancellationTokenSource();
        var operation = InvokeAsync(client, method, caller.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        caller.Cancel();

        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Equal(StatusCode.Cancelled, Assert.IsType<RpcException>(error.InnerException).StatusCode);
    }

    [Theory]
    [InlineData("http://console.mistral.ai/login")]
    [InlineData("https://console.mistral.ai.attacker.example/login")]
    [InlineData("https://console.mistral.ai:444/login")]
    [InlineData("https://user@console.mistral.ai/login")]
    [InlineData("/login")]
    public async Task UntrustedBrowserUrlIsRejectedAndAttemptIsCanceled(string url)
    {
        var calls = new List<string>();
        using var handler = new ReplyHandler((request, _) =>
        {
            calls.Add(Method(request));
            var reply = StartedAttempt();
            reply.SignInUrl = url;
            return Task.FromResult(Reply(reply));
        });
        using IVibeReClient client = new VibeReClient("https://server.example", httpHandler: handler);
        var opened = false;

        var error = await Assert.ThrowsAsync<ServerConnectionException>(() => client.LoginWithBrowserAsync(_ =>
        {
            opened = true;
            return Task.CompletedTask;
        }));

        Assert.Contains("untrusted", error.Message);
        Assert.False(opened);
        Assert.Equal(new[] { "StartBrowserLogin", "CancelBrowserLogin" }, calls);
    }

    [Fact]
    public async Task UnknownAuthenticationStatusIsRejected()
    {
        using var handler = new ReplyHandler((_, _) => Task.FromResult(Reply(new Protocol.AuthStatusReply
        {
            State = (Protocol.AuthState)999
        })));
        using IVibeReClient client = new VibeReClient("https://server.example", httpHandler: handler);

        var error = await Assert.ThrowsAsync<ServerConnectionException>(() => client.GetStatusAsync());

        Assert.Contains("unsupported authentication status", error.Message);
    }

    [Fact]
    public async Task UnknownBrowserStatusIsRejectedAndCleanedUp()
    {
        var cleanupCalls = 0;
        using var handler = new ReplyHandler((request, _) =>
        {
            var reply = StartedAttempt();
            if (Method(request) == "PollBrowserLogin") reply.State = (Protocol.BrowserLoginState)999;
            if (Method(request) == "CancelBrowserLogin") cleanupCalls++;
            return Task.FromResult(Reply(reply));
        });
        using IVibeReClient client = new VibeReClient("https://server.example", httpHandler: handler);

        var error = await Assert.ThrowsAsync<ServerConnectionException>(() => client.LoginWithBrowserAsync(_ => Task.CompletedTask));

        Assert.Contains("unsupported browser login status", error.Message);
        Assert.Equal(1, cleanupCalls);
    }

    private static Task InvokeAsync(IVibeReClient client, string method, CancellationToken cancellationToken) => method switch
    {
        "Login" => client.LoginAsync("api-key", cancellationToken),
        "GetStatus" => client.GetStatusAsync(cancellationToken),
        "Logout" => client.LogoutAsync(cancellationToken),
        "StartBrowserLogin" or "PollBrowserLogin" => client.LoginWithBrowserAsync(_ => Task.CompletedTask, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(method))
    };

    private static string Method(HttpRequestMessage request) => request.RequestUri!.AbsolutePath.Split('/')[^1];

    private static Protocol.BrowserLoginReply StartedAttempt() => new()
    {
        AttemptId = "attempt", SignInUrl = "https://console.mistral.ai/login",
        ExpiresAtUnixSeconds = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
        State = Protocol.BrowserLoginState.Waiting, Message = "Open the browser."
    };

    private static HttpResponseMessage Reply(IMessage message)
    {
        var payload = message.ToByteArray();
        var frame = new byte[payload.Length + 5];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1, 4), payload.Length);
        payload.CopyTo(frame, 5);
        return Response(frame, StatusCode.OK);
    }

    private static HttpResponseMessage Error(StatusCode status, string message) => Response([], status, message);

    private static HttpResponseMessage Response(byte[] content, StatusCode status, string message = "")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Version = HttpVersion.Version20,
            Content = new ByteArrayContent(content)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        response.TrailingHeaders.Add("grpc-status", ((int)status).ToString());
        if (message.Length > 0) response.TrailingHeaders.Add("grpc-message", Uri.EscapeDataString(message));
        return response;
    }

    private sealed class ReplyHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
