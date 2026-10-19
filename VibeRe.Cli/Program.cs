using VibeRe.Client.Infrastructure;
using VibeRe.Client.Infrastructure.Adapters;
using VibeRe.Cli;
using Microsoft.Extensions.DependencyInjection;

try { VibeRe.Lifecycle.ManagedServerShutdownGuard.EnsureApplicationCanStart(AppContext.BaseDirectory); }
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

if (args.Length == 1 && args[0] == "update")
    return DesktopUpdateCommand.Run(AppContext.BaseDirectory);

using var services = new ServiceCollection().AddVibeReClient(ClientConnectionDatabase.DesktopPath)
    .AddSingleton<AuthCommands>().AddSingleton<ServerCommands>().AddSingleton<CliApplication>()
    .BuildServiceProvider();
return await services.GetRequiredService<CliApplication>().RunAsync(args);
