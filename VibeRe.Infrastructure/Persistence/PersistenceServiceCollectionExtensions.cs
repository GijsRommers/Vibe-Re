using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VibeRe.Application.Conversations;
using VibeRe.Application.Tools;
using VibeRe.Infrastructure.Persistence.Repositories;

namespace VibeRe.Infrastructure.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddVibeRePersistence(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<VibeReDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped<IConversationRepository, ConversationRepository>();
        services.AddScoped<IConversationEventStore, ConversationEventStore>();
        services.AddScoped<IContextStore, ContextStore>();
        services.AddScoped<IMcpServerRepository, McpServerRepository>();
        services.AddScoped<IToolPolicyStore, ToolPolicyStore>();
        return services;
    }

    /// <summary>Creates or upgrades the database to the latest migration.</summary>
    public static async Task MigrateVibeReDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<VibeReDbContext>().Database.MigrateAsync(cancellationToken);
    }
}
