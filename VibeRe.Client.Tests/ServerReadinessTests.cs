using System.Net;
using System.Net.Sockets;
using System.Text;
using VibeRe.Client.Infrastructure.Adapters;

namespace VibeRe.Client.Tests;

public class ServerReadinessTests
{
    [Theory]
    [InlineData("{\"application\":\"vibe-re\",\"protocolVersion\":2,\"transport\":\"signalr\"}", true)]
    [InlineData("{\"application\":\"vibe-re\",\"protocolVersion\":1}", false)]
    [InlineData("{\"application\":\"vibe-re\",\"protocolVersion\":2,\"transport\":\"other\"}", false)]
    [InlineData("{\"application\":\"vibe-re\",\"protocolVersion\":3,\"transport\":\"signalr\"}", false)]
    [InlineData("{\"application\":\"other\",\"protocolVersion\":2,\"transport\":\"signalr\"}", false)]
    [InlineData("not json", false)]
    public async Task PlainHttpReadinessAcceptsOnlyCurrentSignalRServer(string body, bool expected)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stop = timeout.Token.Register(listener.Stop);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // A dedicated responder avoids ThreadPool starvation consuming the product's one-second
        // readiness budget before this fixture writes the response body we intend to validate.
        var response = Task.Factory.StartNew(Respond, timeout.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await ready.Task.WaitAsync(timeout.Token);

        Assert.Equal(expected, await new ServerReadinessProbe().IsReadyAsync(new($"http://127.0.0.1:{port}"), timeout.Token));
        await response;

        void Respond()
        {
            ready.SetResult();
            using var socket = listener.AcceptTcpClient();
            socket.ReceiveTimeout = socket.SendTimeout = 5000;
            using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            Assert.Equal("GET /.well-known/vibe-re/server HTTP/1.1", reader.ReadLine());
            while (!string.IsNullOrEmpty(reader.ReadLine())) { }
            var payload = Encoding.UTF8.GetBytes(body);
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
            stream.Write([.. headers, .. payload]);
        }
    }
}
