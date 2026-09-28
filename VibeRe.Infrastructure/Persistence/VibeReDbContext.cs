using Microsoft.EntityFrameworkCore;

namespace VibeRe.Infrastructure.Persistence;

public sealed class VibeReDbContext(DbContextOptions<VibeReDbContext> options) : DbContext(options)
{
}