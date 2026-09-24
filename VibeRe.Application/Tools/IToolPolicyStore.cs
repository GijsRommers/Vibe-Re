using VibeRe.Domain.Tools;

namespace VibeRe.Application.Tools;

public interface IToolPolicyStore
{
    Task<IReadOnlyDictionary<string, ToolApproval>> GetAllAsync(CancellationToken cancellationToken = default);

    Task SetAsync(string toolName, ToolApproval approval, CancellationToken cancellationToken = default);

    /// <summary>Resets the tool to <see cref="ToolApproval.Ask"/>. Returns <c>false</c> if it had no policy.</summary>
    Task<bool> DeleteAsync(string toolName, CancellationToken cancellationToken = default);
}
