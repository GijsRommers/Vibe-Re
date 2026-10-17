using VibeRe.Cli;
using Microsoft.Extensions.DependencyInjection;
using VibeRe.Client;
using VibeRe.Client.Configuration;

using var services = new ServiceCollection().AddVibeReClient(ClientConnectionDatabase.DesktopPath)
    .AddSingleton<AuthCommands>().AddSingleton<ServerCommands>().AddSingleton<CliApplication>()
    .BuildServiceProvider();
return await services.GetRequiredService<CliApplication>().RunAsync(args);
