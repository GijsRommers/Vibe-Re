using VibeRe.Application.Tools;

namespace VibeRe.Tools;

/// <summary>Resolves model-supplied paths against the conversation's working directory.</summary>
internal static class Workspace
{
    /// <summary>
    /// Returns the absolute path for <paramref name="path"/> (absolute, or relative to the working directory).
    /// Paths outside the working directory are refused, so tools can't touch the rest of the machine.
    /// </summary>
    public static string Resolve(ToolContext context, string? path)
    {
        var root = Path.GetFullPath(context.WorkingDirectory);
        var full = Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? "." : path, root);
        var relative = Path.GetRelativePath(root, full);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new ToolException($"'{path}' is outside the working directory {root}.");
        }

        return full;
    }

    /// <summary>The path as shown back to the model: relative to the working directory.</summary>
    public static string Display(ToolContext context, string fullPath) =>
        Path.GetRelativePath(Path.GetFullPath(context.WorkingDirectory), fullPath);
}
