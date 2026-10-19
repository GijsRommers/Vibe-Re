using Latchkey.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VibeRe.Server.Application.Secrets;
using VibeRe.Server.Infrastructure.Persistence;
using VibeRe.Server.Infrastructure.Persistence.Interceptors;
using VibeRe.Server.Infrastructure.Secrets;

namespace VibeRe.Server;

internal static class DependencyInjection
{
    public static IServiceCollection AddVibeReServices(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<TimestampedEntityInterceptor>();

        services.AddDbContext<VibeReDbContext>((sp, options) =>
        {
            options.UseSqlite(DatabaseConnectionString());
            options.AddInterceptors(sp.GetRequiredService<TimestampedEntityInterceptor>());
        });

        services.AddLatchkey("dev.vibe-re");
        services.AddSingleton<ISecretStore, LatchkeySecretStore>();

        return services;
    }

    // The database always lives in ~/.vibe-re; SQLite won't create the folder itself.
    private static string DatabaseConnectionString()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vibe-re");
        Directory.CreateDirectory(directory);

        return new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "vibe-re.db") }.ToString();
    }
}