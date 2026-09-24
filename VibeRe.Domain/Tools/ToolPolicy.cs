namespace VibeRe.Domain.Tools;

public enum ToolApproval
{
    /// <summary>Ask the user before every call. The default for tools without a policy.</summary>
    Ask,
    Allow,
    Deny,
}

/// <summary>The user's standing decision about calling one tool, keyed by its registry name.</summary>
public sealed class ToolPolicy
{
    private ToolPolicy() { }

    public ToolPolicy(string toolName, ToolApproval approval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ToolName = toolName;
        Approval = approval;
    }

    public string ToolName { get; private set; } = null!;

    public ToolApproval Approval { get; set; }
}
