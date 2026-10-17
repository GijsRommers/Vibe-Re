using VibeRe.Runtime.Configuration;

namespace VibeRe.Cli;

public sealed class ServerCommands(IServerConnectionManager connections)
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
            ["token", var name] => await connections.SetTokenAsync(name, options.TokenFromStdin
                ? await Console.In.ReadLineAsync(cancellationToken) ?? ""
                : ConsoleInput.ReadSecret("Server access token (empty to clear): ", cancellationToken), cancellationToken),
            _ => throw new ArgumentException("Use servers list/add/update/use/remove/local/required/token. See --help.")
        };
        Console.WriteLine($"Server required at startup: {config.ServerRequired}");
        foreach (var server in config.Servers)
            Console.WriteLine($"{(server.Name == config.ActiveServer ? "*" : " ")} {server.Name}\t{server.Address}\t{(server.HasAccessToken ? "token saved" : "no token")}\t{(server.StartLocally ? "auto-start local" : "connect only")}");
        if (config.Servers.Count == 0) Console.WriteLine("No servers configured. Use servers add NAME URL.");
        return 0;
    }
}
