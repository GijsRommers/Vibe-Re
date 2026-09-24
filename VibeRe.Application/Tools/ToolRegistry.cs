using System.Text.Json;
using System.Text.RegularExpressions;
using VibeRe.Domain.Tools;

namespace VibeRe.Application.Tools;

public sealed record RegisteredTool(ITool Tool, ToolApproval Approval);

/// <summary>
/// The single place the agent loop asks for tools: merges every <see cref="IToolSource"/>, applies the
/// user's <see cref="ToolPolicy"/>s and gates each call on them.
/// </summary>
public sealed partial class ToolRegistry(IEnumerable<IToolSource> sources, IToolPolicyStore policies)
{
    /// <summary>All tools with their effective approval, including denied ones (for settings UIs).</summary>
    public async Task<IReadOnlyList<RegisteredTool>> ListAsync(CancellationToken cancellationToken = default)
    {
        var approvals = await policies.GetAllAsync(cancellationToken);
        var tools = new Dictionary<string, RegisteredTool>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            foreach (var tool in await source.GetToolsAsync(cancellationToken))
            {
                if (!NamePattern().IsMatch(tool.Name))
                {
                    throw new InvalidOperationException($"Tool name '{tool.Name}' is not a valid model tool name.");
                }

                var approval = approvals.GetValueOrDefault(tool.Name, ToolApproval.Ask);
                if (!tools.TryAdd(tool.Name, new RegisteredTool(tool, approval)))
                {
                    throw new InvalidOperationException($"More than one tool is named '{tool.Name}'.");
                }
            }
        }

        return tools.Values.OrderBy(entry => entry.Tool.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>The tools to offer the model: everything that isn't denied.</summary>
    public async Task<IReadOnlyList<ITool>> GetAvailableAsync(CancellationToken cancellationToken = default) =>
        (await ListAsync(cancellationToken))
            .Where(entry => entry.Approval != ToolApproval.Deny)
            .Select(entry => entry.Tool)
            .ToList();

    /// <summary>
    /// Runs a model-requested tool call. Unknown, denied, declined and failing calls come back as error
    /// results for the model rather than exceptions; only cancellation propagates.
    /// </summary>
    public async Task<ToolResult> InvokeAsync(
        string name,
        JsonElement arguments,
        ToolContext context,
        IToolApprover approver,
        CancellationToken cancellationToken = default)
    {
        var entry = (await ListAsync(cancellationToken)).FirstOrDefault(candidate => candidate.Tool.Name == name);
        if (entry is null)
        {
            return ToolResult.Error($"Unknown tool '{name}'.");
        }

        switch (entry.Approval)
        {
            case ToolApproval.Deny:
                return ToolResult.Error($"The user has disabled '{name}'.");
            case ToolApproval.Ask when !await approver.ApproveAsync(entry.Tool, arguments, cancellationToken):
                return ToolResult.Error($"The user declined this call to '{name}'.");
        }

        try
        {
            return await entry.Tool.InvokeAsync(arguments, context, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ToolResult.Error($"'{name}' failed: {exception.Message}");
        }
    }

    [GeneratedRegex("^[a-zA-Z0-9_-]{1,64}$")]
    private static partial Regex NamePattern();
}
