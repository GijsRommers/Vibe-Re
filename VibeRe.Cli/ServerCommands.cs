using VibeRe.Client.Application;
using VibeRe.Client.Application.Configuration;

namespace VibeRe.Cli;

public sealed class ServerCommands(IServerConnectionManager connections, IDevicePairingClient? pairing = null,
    IServerConnector? connector = null)
{
    public async Task<int> ExecuteAsync(CliOptions options, CancellationToken cancellationToken)
    {
        if (options.Server is not null || options.Method is not null || options.ApiKeyFromStdin)
            throw new ArgumentException("Unsupported option for servers.");

        var args = options.Arguments.Skip(1).ToArray();
        var action = args.FirstOrDefault() ?? "list";
        if (options.TokenFromStdin && action != "token") throw new ArgumentException("--token-stdin requires servers token.");
        if (options.StartLocally && action != "add") throw new ArgumentException("--local requires servers add.");

        var config = args switch
        {
            [] or ["list"] => await connections.LoadAsync(cancellationToken),
            ["required", "on" or "off"] => await connections.SetServerRequiredAsync(args[1] == "on", cancellationToken),
            ["add", var name, var address] => await connections.AddAsync(name, address, cancellationToken, options.StartLocally),
            ["update", var name, var address] => await connections.UpdateAddressAsync(name, address, cancellationToken),
            ["use", var name] => await connections.SelectAsync(name, cancellationToken),
            ["remove", var name] => await connections.RemoveAsync(name, cancellationToken),
            ["local", var name, "on" or "off"] => await connections.SetLocalStartupAsync(name, args[2] == "on", cancellationToken),
            ["pair", var name] => await PairAsync(name, "CLI device", cancellationToken),
            ["pair", var name, var deviceName] => await PairAsync(name, deviceName, cancellationToken),
            ["token", var name] => await SaveTokenAsync(name, options.TokenFromStdin, cancellationToken),
            _ => throw new ArgumentException("Use servers list/add/update/use/remove/local/required/pair/token. See --help.")
        };
        Console.WriteLine($"Server required at startup: {config.ServerRequired}");
        foreach (var server in config.Servers)
            Console.WriteLine($"{(server.Name == config.ActiveServer ? "*" : " ")} {server.Name}\t{server.Address}\t{(server.HasAccessToken ? "token saved" : "no token")}\t{(server.StartLocally ? "auto-start local" : "connect only")}");
        if (config.Servers.Count == 0) Console.WriteLine("No servers configured. Use servers add NAME URL.");
        return 0;
    }

    private async Task<ClientConfiguration> SaveTokenAsync(string name, bool stdin, CancellationToken ct)
    {
        var server = (await connections.LoadAsync(ct)).Find(name);
        var token = stdin
            ? await Console.In.ReadLineAsync(ct) ?? ""
            : ConsoleInput.ReadSecret("Server access token (empty to clear): ", ct);
        return await connections.SetTokenAsync(server, token, ct);
    }

    private async Task<ClientConfiguration> PairAsync(string name, string deviceName, CancellationToken ct)
    {
        if (pairing is null) throw new InvalidOperationException("Device pairing is unavailable in this client.");
        var server = (await connections.LoadAsync(ct)).Find(name);
        var token = await pairing.PairAsync(server.Address, deviceName, challenge =>
        {
            ct.ThrowIfCancellationRequested();
            Console.WriteLine($"Approval code: {challenge.ApprovalCode}");
            Console.WriteLine($"On the computer running {server.Name}, open its web UI locally and approve this code in Manage devices.");
            Console.WriteLine($"Expires {challenge.ExpiresAt.ToLocalTime():g}. Press Ctrl+C to cancel.");
            return Task.CompletedTask;
        }, ct);
        ct.ThrowIfCancellationRequested();
        var configuration = await connections.SetTokenAsync(server, token, ct);
        Console.WriteLine($"Paired with {server.Name}. This device's server access token is saved in the OS credential store.");
        if (connector is not null)
        {
            var connection = await connector.ConnectAsync(server.Name, cancellationToken: ct);
            await using var client = connection.Client;
            ct.ThrowIfCancellationRequested();
            var status = await client.GetStatusAsync(ct);
            Console.WriteLine($"Mistral: {status.Message}");
            if (status.NeedsLogin) Console.WriteLine($"Use --server {server.Name} auth login to log in to Mistral.");
        }
        return configuration;
    }
}
