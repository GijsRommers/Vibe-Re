using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using VibeRe.Domain.Tools;

namespace VibeRe.Infrastructure.Tools.Mcp;

/// <summary>
/// Keeps one live MCP client per configured server for the lifetime of the process. A server whose
/// configuration changed is reconnected; a failed connection is retried on the next request.
/// </summary>
public sealed class McpClientPool(ILoggerFactory? loggerFactory = null) : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Connection> _connections = [];

    public Task<McpClient> GetClientAsync(McpServer server, CancellationToken cancellationToken = default)
    {
        var fingerprint = JsonSerializer.Serialize(server);
        Connection? stale = null;
        Connection connection;
        lock (_lock)
        {
            if (!_connections.TryGetValue(server.Id, out connection!)
                || connection.Fingerprint != fingerprint
                || connection.Client.IsFaulted
                || connection.Client.IsCanceled)
            {
                stale = connection;
                connection = new Connection(fingerprint, ConnectAsync(server));
                _connections[server.Id] = connection;
            }
        }

        _ = stale?.DisposeAsync();
        return connection.Client.WaitAsync(cancellationToken);
    }

    /// <summary>Disconnects servers that were removed or disabled, in the background.</summary>
    public void RetainOnly(IEnumerable<Guid> serverIds)
    {
        foreach (var connection in Remove(serverIds.ToHashSet()))
        {
            _ = connection.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in Remove([]))
        {
            await connection.DisposeAsync();
        }
    }

    private List<Connection> Remove(HashSet<Guid> keep)
    {
        lock (_lock)
        {
            var removed = _connections.Where(pair => !keep.Contains(pair.Key)).ToList();
            foreach (var pair in removed)
            {
                _connections.Remove(pair.Key);
            }

            return removed.Select(pair => pair.Value).ToList();
        }
    }

    private async Task<McpClient> ConnectAsync(McpServer server)
    {
        IClientTransport transport = server.Transport switch
        {
            McpTransport.Stdio => new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = server.Name,
                Command = server.Command!,
                Arguments = server.Arguments,
                EnvironmentVariables = server.Environment.ToDictionary(pair => pair.Key, pair => (string?)pair.Value),
            }, loggerFactory),
            McpTransport.Http => new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = server.Name,
                Endpoint = new Uri(server.Url!),
                AdditionalHeaders = server.Headers,
            }, loggerFactory),
            _ => throw new NotSupportedException($"Unsupported MCP transport '{server.Transport}'."),
        };

        using var timeout = new CancellationTokenSource(ConnectTimeout);
        return await McpClient.CreateAsync(
            transport,
            new McpClientOptions { ClientInfo = new Implementation { Name = "vibe-re", Version = "0.1" } },
            loggerFactory,
            timeout.Token);
    }

    private sealed record Connection(string Fingerprint, Task<McpClient> Client) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await (await Client).DisposeAsync();
            }
            catch (Exception)
            {
                // Never connected, so there is nothing to shut down.
            }
        }
    }
}
