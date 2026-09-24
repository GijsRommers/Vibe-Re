using Microsoft.EntityFrameworkCore;
using VibeRe.Application.Tools;
using VibeRe.Domain.Tools;

namespace VibeRe.Infrastructure.Persistence.Repositories;

public sealed class ToolPolicyStore(VibeReDbContext db) : IToolPolicyStore
{
    public async Task<IReadOnlyDictionary<string, ToolApproval>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await db.ToolPolicies.AsNoTracking()
            .ToDictionaryAsync(policy => policy.ToolName, policy => policy.Approval, StringComparer.Ordinal, cancellationToken);

    public async Task SetAsync(string toolName, ToolApproval approval, CancellationToken cancellationToken = default)
    {
        var policy = await db.ToolPolicies.FindAsync([toolName], cancellationToken);
        if (policy is null)
        {
            db.ToolPolicies.Add(new ToolPolicy(toolName, approval));
        }
        else
        {
            policy.Approval = approval;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(string toolName, CancellationToken cancellationToken = default) =>
        await db.ToolPolicies.Where(policy => policy.ToolName == toolName).ExecuteDeleteAsync(cancellationToken) > 0;
}
