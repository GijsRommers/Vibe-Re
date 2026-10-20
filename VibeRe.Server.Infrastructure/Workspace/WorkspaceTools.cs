using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VibeRe.Server.Application.Workspace;

namespace VibeRe.Server.Infrastructure.Workspace;

public sealed class WorkspaceTools : IWorkspaceTools
{
    public async Task PrepareAsync(string root, string worktree, CancellationToken ct)
    {
        if (!Directory.Exists(worktree))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(worktree)!);
            var prepared = await ProcessAsync("git", ["worktree", "add", "-b", "vibere/" + Path.GetFileName(worktree), worktree, "HEAD"], root, ct);
            if (prepared.ExitCode != 0) throw new WorkspaceException("Could not create a git worktree. Check the repository and worktree state.");
        }
        var verified = await ProcessAsync("git", ["rev-parse", "--show-toplevel"], worktree, ct);
        if (verified.ExitCode != 0 || !string.Equals(PhysicalPath(verified.Output.Trim()), PhysicalPath(worktree), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new WorkspaceException("The coding thread's worktree could not be verified.");
    }

    public async Task<string> ExecuteAsync(string worktree, string name, string arguments, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(arguments);
        var input = json.RootElement;
        switch (name)
        {
            case "list_files":
                return string.Join("\n", Directory.EnumerateFileSystemEntries(Resolve(worktree, input.GetProperty("path").GetString()!))
                    .Take(500).Select(path => Path.GetFileName(path) + (Directory.Exists(path) ? "/" : "")));
            case "read_file":
                var path = Resolve(worktree, input.GetProperty("path").GetString()!);
                if (new FileInfo(path).Length > 32000) throw new WorkspaceException("File exceeds 32 KB. Use a targeted shell command to read the relevant part.");
                return await File.ReadAllTextAsync(path, ct);
            case "write_file":
                var target = Resolve(worktree, input.GetProperty("path").GetString()!);
                var content = input.GetProperty("content").GetString() ?? "";
                if (content.Length > 128000) throw new WorkspaceException("Write is too large. Split the change into smaller files.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllTextAsync(target, content, ct);
                return "Saved " + Path.GetRelativePath(worktree, target);
            case "run_command":
                var command = input.GetProperty("command").GetString() ?? "";
                if (command.Length is 0 or > 16000) throw new WorkspaceException("Enter a command of up to 16,000 characters.");
                var windows = OperatingSystem.IsWindows();
                var result = await ProcessAsync(windows ? "cmd.exe" : "/bin/sh", [windows ? "/c" : "-c", command], worktree, ct);
                return $"Exit code: {result.ExitCode}\n{result.Output}";
            default: throw new WorkspaceException("Unknown coding tool.");
        }
    }

    private static string PhysicalPath(string path)
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var segment in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = new DirectoryInfo(Path.Combine(current, segment));
            current = directory.LinkTarget is null ? directory.FullName : directory.ResolveLinkTarget(true)!.FullName;
        }
        return current;
    }

    internal static string Resolve(string root, string relative)
    {
        if (Path.IsPathRooted(relative)) throw new WorkspaceException("Use a relative path inside the worktree.");
        root = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(root, relative));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (path != root && !path.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new WorkspaceException("The file path must stay inside the worktree.");
        // Reject symlinks in every existing path component, including the destination itself.
        for (var current = path; current != root && current is not null; current = Path.GetDirectoryName(current))
            if (new FileInfo(current).LinkTarget is not null)
                throw new WorkspaceException("File tools do not follow symbolic links. Use a regular worktree path.");
        if (Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar).Contains(".git", StringComparer.OrdinalIgnoreCase))
            throw new WorkspaceException("Use git commands to inspect repository metadata.");
        return path;
    }

    private static readonly HashSet<string> CommandEnvironment = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "HOME", "USER", "TMPDIR", "LANG", "SystemRoot", "DOTNET_ROOT",
        "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "TEMP", "TMP", "COMSPEC", "LOCALAPPDATA", "APPDATA"
    };

    private static async Task<(int ExitCode, string Output)> ProcessAsync(string executable, string[] arguments, string directory, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var name in start.Environment.Keys.ToArray())
            if (!CommandEnvironment.Contains(name)) start.Environment.Remove(name);
        using var process = Process.Start(start) ?? throw new WorkspaceException("Could not start the command.");
        process.StandardInput.Close();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromMinutes(2));
        using var stop = limit.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var stdout = ReadBounded(process.StandardOutput, limit.Token);
        var stderr = ReadBounded(process.StandardError, limit.Token);
        try
        {
            await process.WaitForExitAsync(limit.Token);
            return (process.ExitCode, (await stdout) + (await stderr));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new WorkspaceException("Command timed out after two minutes. Its process tree was stopped."); }
        finally
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
        }
    }
    private static async Task<string> ReadBounded(StreamReader reader, CancellationToken ct)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        var truncated = false;
        while (await reader.ReadAsync(buffer, ct) is var count && count > 0)
        {
            var accepted = Math.Min(count, 32000 - text.Length);
            if (accepted > 0) text.Append(buffer, 0, accepted);
            if (accepted < count) truncated = true;
        }
        if (truncated) text.Append("\n[Output truncated]");
        return text.ToString();
    }
}
