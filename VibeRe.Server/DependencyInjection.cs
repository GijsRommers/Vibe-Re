using Latchkey.Extensions.DependencyInjection;
using VibeRe.Application.Secrets;
using VibeRe.Infrastructure.Secrets;

namespace VibeRe.Server;

internal static class DependencyInjection
{
    public static IServiceCollection AddVibeReServices(this IServiceCollection services)
    {
        services.AddLatchkey("dev.vibe-re");
        services.AddSingleton<ISecretStore, LatchkeySecretStore>();

        return services;
    }
}