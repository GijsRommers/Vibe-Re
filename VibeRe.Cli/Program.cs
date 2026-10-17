using VibeRe.Client.Infrastructure;
using VibeRe.Client.Infrastructure.Adapters;
using VibeRe.Cli;
using Microsoft.Extensions.DependencyInjection;

using var services = new ServiceCollection().AddVibeReClient(ClientConnectionDatabase.DesktopPath)
    .AddSingleton<AuthCommands>().AddSingleton<ServerCommands>().AddSingleton<CliApplication>()
    .BuildServiceProvider();
return await services.GetRequiredService<CliApplication>().RunAsync(args);
