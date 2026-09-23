using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VibeRe.Domain.Conversations;
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
        return services;
    }
}
