namespace VibeRe.Infrastructure;

public static class VibeRePaths
{
    /// <summary>Per-user data directory: <c>$VIBE_RE_HOME</c>, or <c>~/.vibe-re</c>.</summary>
    public static string Home =>
        Environment.GetEnvironmentVariable("VIBE_RE_HOME") is { Length: > 0 } home
            ? Path.GetFullPath(home)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vibe-re");

    /// <summary>The SQLite database holding conversations and their context trees.</summary>
    public static string Database => Path.Combine(Home, "vibe-re.db");
}
