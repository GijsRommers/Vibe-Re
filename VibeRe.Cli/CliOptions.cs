namespace VibeRe.Cli;

public sealed record CliOptions(string[] Arguments, string? Server, string? Method,
    bool ApiKeyFromStdin, bool TokenFromStdin, bool StartLocally, bool ShowHelp)
{
    public bool IsServerCommand => Arguments.FirstOrDefault() == "servers";
    public bool IsLaunch => Arguments.Length == 0;

    public static CliOptions Parse(string[] args)
    {
        var arguments = args.ToList();
        if (arguments.Contains("--help") || arguments.Contains("-h"))
            return new([], null, null, false, false, false, true);

        var server = TakeOption(arguments, "--server");
        var method = TakeOption(arguments, "--method");
        var stdin = arguments.Remove("--api-key-stdin");
        var tokenStdin = arguments.Remove("--token-stdin");
        var local = arguments.Remove("--local");
        return new(arguments.ToArray(), server, method, stdin, tokenStdin, local, false);
    }

    private static string? TakeOption(List<string> args, string option)
    {
        var index = args.IndexOf(option);
        if (index < 0) return null;
        if (index + 1 == args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"{option} requires a value.");
        var value = args[index + 1];
        args.RemoveRange(index, 2);
        return value;
    }
}
