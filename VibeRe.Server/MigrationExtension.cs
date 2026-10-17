using Microsoft.EntityFrameworkCore;
using VibeRe.Server.Infrastructure.Persistence;

namespace VibeRe.Server;

public static class MigrationExtensions
{
    public static async Task ApplyMigrationsAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();

        var dbContext = scope.ServiceProvider
            .GetRequiredService<VibeReDbContext>();

        await dbContext.Database.MigrateAsync();
    }
}