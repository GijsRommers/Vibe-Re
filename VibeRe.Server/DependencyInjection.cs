using Latchkey.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using VibeRe.Application.Secrets;
using VibeRe.Infrastructure.Persistence;
using VibeRe.Infrastructure.Persistence.Interceptors;
using VibeRe.Infrastructure.Secrets;

namespace VibeRe.Server;

internal static class DependencyInjection
{
    public static IServiceCollection AddVibeReServices(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<TimestampedEntityInterceptor>();

        services.AddDbContext<VibeReDbContext>((sp, options) =>
        {
            options.UseSqlite(sp.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection"));
            options.AddInterceptors(sp.GetRequiredService<TimestampedEntityInterceptor>());
        });

        services.AddLatchkey("dev.vibe-re");
        services.AddSingleton<ISecretStore, LatchkeySecretStore>();

        return services;
    }
}
