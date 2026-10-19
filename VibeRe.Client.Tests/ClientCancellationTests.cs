using VibeRe.Client.Infrastructure;
using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using Google.Protobuf;
using Grpc.Core;
using VibeRe.Client.Application;
using Protocol = VibeRe.Protocol;

namespace VibeRe.Client.Tests;

public sealed class ClientCancellationTests
{
    [Fact]
    public async Task CallerCancellationBoundsBrowserCleanupAndPreservesCallerCancellation()
    {
        using var handler = new LoginHandler();
        using var client = new VibeReClient("https://server.example", httpHandler: handler);
        using var cancellation = new CancellationTokenSource();
        var login = client.LoginWithBrowserAsync(_ => Task.CompletedTask, cancellation.Token);
        await handler.PollStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => login.WaitAsync(TimeSpan.FromSeconds(8)));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(StatusCode.Cancelled, Assert.IsType<RpcException>(error.InnerException).StatusCode);
        Assert.Equal(1, handler.CleanupCalls);
        await handler.CleanupCanceled.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task BrowserFailureSurvivesUnresponsiveCleanup()
    {
        using var handler = new LoginHandler();
        using var client = new VibeReClient("https://server.example", httpHandler: handler);
        var original = new InvalidOperationException("Browser callback failed.");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.LoginWithBrowserAsync(_ => Task.FromException(original)).WaitAsync(TimeSpan.FromSeconds(8)));

        Assert.Same(original, error);
        Assert.Equal(1, handler.CleanupCalls);
        Assert.False(handler.PollStarted.Task.IsCompleted);
        await handler.CleanupCanceled.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ExpiredAttemptReturnsExpiredAfterOneBoundedCleanup()
    {
        using var handler = new LoginHandler(expired: true);
        using var client = new VibeReClient("https://server.example", httpHandler: handler);

        var result = await client.LoginWithBrowserAsync(_ => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(8));

        Assert.Equal(BrowserLoginState.Expired, result.State);
        Assert.Equal(1, handler.CleanupCalls);
        Assert.False(handler.PollStarted.Task.IsCompleted);
        await handler.CleanupCanceled.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private sealed class LoginHandler(bool expired = false) : HttpMessageHandler
    {
        public TaskCompletionSource PollStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CleanupCalls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var method = request.RequestUri!.AbsolutePath.Split('/')[^1];
            if (method == "StartBrowserLogin")
                return Reply(new Protocol.BrowserLoginReply
                {
                    AttemptId = "attempt", SignInUrl = "https://console.mistral.ai/login",
                    ExpiresAtUnixSeconds = DateTimeOffset.UtcNow.AddMinutes(expired ? -1 : 5).ToUnixTimeSeconds(),
                    State = Protocol.BrowserLoginState.Waiting
                });

            if (method == "PollBrowserLogin") PollStarted.TrySetResult();
            else if (method == "CancelBrowserLogin") CleanupCalls++;
            else throw new InvalidOperationException($"Unexpected RPC: {method}");

            try { await Task.Delay(Timeout.Infinite, ct); }
            finally
            {
                if (method == "CancelBrowserLogin" && ct.IsCancellationRequested) CleanupCanceled.TrySetResult();
            }
            throw new InvalidOperationException("The unresponsive RPC should only end through cancellation.");
        }

        private static HttpResponseMessage Reply(IMessage message)
        {
            var payload = message.ToByteArray();
            var frame = new byte[payload.Length + 5];
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1, 4), payload.Length);
            payload.CopyTo(frame, 5);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Version = HttpVersion.Version20,
                Content = new ByteArrayContent(frame)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
            response.TrailingHeaders.Add("grpc-status", "0");
            return response;
        }
    }
}
