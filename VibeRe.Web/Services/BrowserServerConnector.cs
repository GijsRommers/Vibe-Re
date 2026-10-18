using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using VibeRe.Client.Application;
using VibeRe.Client.Application.Configuration;
using VibeRe.Client.SignalR;
using VibeRe.Contracts;

namespace VibeRe.Web.Services;

public sealed class BrowserServerConnector(BrowserServer server) : IServerConnector
{
    internal static void ConfigureJsonProtocol(JsonHubProtocolOptions options) =>
        options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, BrowserJsonContext.Default);

    public Task<(ServerConnection Server, IVibeReClient Client)> ConnectAsync(string? name = null, string? tokenOverride = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (name is not null && name != "current" || tokenOverride is not null)
            throw new ArgumentException("The browser connects only to its current server using its device cookie.");
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.Origin, ServerProtocol.AuthHubPath), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                // Browser WebSockets send same-origin cookies automatically; no bearer token is exposed.
            })
            .AddJsonProtocol(ConfigureJsonProtocol)
            .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)])
            .Build();
        return Task.FromResult<(ServerConnection, IVibeReClient)>((server.Connection, new SignalRAuthClient(connection)));
    }
}
