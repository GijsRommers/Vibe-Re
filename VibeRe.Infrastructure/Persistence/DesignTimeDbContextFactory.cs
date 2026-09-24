using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace VibeRe.Infrastructure.Persistence;

/// <summary>Lets <c>dotnet ef</c> build the context without a host: <c>dotnet ef migrations add Name -p VibeRe.Infrastructure</c>.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<VibeReDbContext>
{
    public VibeReDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<VibeReDbContext>()
            .UseSqlite($"Data Source={VibeRePaths.Database}")
            .Options);
}
