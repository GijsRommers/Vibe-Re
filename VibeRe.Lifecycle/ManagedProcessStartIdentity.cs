using System.Diagnostics;
using System.Globalization;

namespace VibeRe.Lifecycle;

/// <summary>Cross-process-stable OS process creation identity; never a sampled wall-clock estimate.</summary>
internal static class ManagedProcessStartIdentity
{
    private static readonly Lazy<string> LinuxBootIdentity = new(() =>
    {
        try
        {
            var value = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
            if (!Guid.TryParseExact(value, "D", out var boot)) throw InvalidIdentity();
            return boot.ToString("D");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new InvalidOperationException("The operating-system boot identity could not be verified. Local startup and updates cannot safely continue.", exception); }
    });

    internal static string Read(Process process)
    {
        if (!OperatingSystem.IsLinux()) return "utc:" + process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
        // .NET's Linux StartTime adds a process-local estimate of boot UTC to these ticks. That
        // estimate can differ between the launcher and server, so compare the raw kernel token.
        return ReadLinuxProcessIdentity(process.Id, LinuxBootIdentity.Value, File.ReadAllText);
    }

    internal static string ReadLinuxProcessIdentity(int processId, string bootIdentity, Func<string, string> readFile)
    {
        try { return CreateLinuxIdentity(bootIdentity, ParseLinuxStat(readFile($"/proc/{processId}/stat"), processId)); }
        // A disappearing stat file means the process exited. Preserve those two signals for callers;
        // permission failures and other I/O errors must never be interpreted as an absent process.
        catch (Exception exception) when (exception is UnauthorizedAccessException
            || exception is IOException and not FileNotFoundException and not DirectoryNotFoundException)
        { throw new InvalidOperationException("The local server process start identity could not be read. Close it yourself before updating.", exception); }
    }

    internal static string CreateLinuxIdentity(string bootIdentity, long startTicks)
    {
        if (!Guid.TryParseExact(bootIdentity, "D", out var boot) || startTicks < 0) throw InvalidIdentity();
        return "linux:" + boot.ToString("D") + ":" + startTicks.ToString(CultureInfo.InvariantCulture);
    }

    internal static long ParseLinuxStat(string value, int expectedProcessId)
    {
        var firstSpace = value.IndexOf(' ');
        var commandEnd = value.LastIndexOf(')');
        if (firstSpace <= 0 || commandEnd <= firstSpace || value[firstSpace + 1] != '('
            || !int.TryParse(value.AsSpan(0, firstSpace), NumberStyles.None, CultureInfo.InvariantCulture, out var processId)
            || processId != expectedProcessId)
            throw InvalidIdentity();
        // comm (field 2) may contain spaces, parentheses, and even newlines. Only split after its
        // final closing parenthesis. The next token is field 3; starttime is field 22 (index 19).
        var fields = value[(commandEnd + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 20 || fields[0].Length != 1 || !long.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var identity) || identity < 0)
            throw InvalidIdentity();
        return identity;
    }

    private static InvalidOperationException InvalidIdentity()
        => new("The local server process start identity could not be verified. Close it yourself before updating.");
}
