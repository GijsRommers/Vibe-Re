using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace VibeRe.Runtime.Adapters;

public sealed class ServerReadinessProbe : IServerReadinessProbe
{
    public async Task<bool> IsReadyAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(1) };
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, "/.well-known/vibe-re/server"))
            { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return false;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.TryGetProperty("application", out var app) && app.GetString() == "vibe-re" &&
                document.RootElement.TryGetProperty("protocolVersion", out var protocol) &&
                protocol.ValueKind == JsonValueKind.Number && protocol.TryGetInt32(out var value) && value == 1;
        }
        catch (HttpRequestException) { return false; }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    public async Task<bool> IsListeningAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(500));
        using var socket = new TcpClient();
        try { await socket.ConnectAsync(uri.Host, uri.Port, timeout.Token); return true; }
        catch (SocketException) { return false; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }
}
