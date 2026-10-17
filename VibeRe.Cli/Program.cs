using VibeRe.Runtime.Adapters;
using VibeRe.Cli;
using Microsoft.Extensions.DependencyInjection;
using VibeRe.Client;
using VibeRe.Runtime;
using VibeRe.Runtime.Configuration;

using var services = new ServiceCollection().AddVibeReRuntime(ClientConnectionDatabase.DesktopPath)
    .AddSingleton<AuthCommands>().AddSingleton<ServerCommands>().AddSingleton<CliApplication>()
    .BuildServiceProvider();
return await services.GetRequiredService<CliApplication>().RunAsync(args);
