using System.Text;

namespace VibeRe.Cli;

internal static class ConsoleInput
{
    public static string ReadSecret(string prompt, CancellationToken ct)
    {
        if (Console.IsInputRedirected) throw new ArgumentException("Use the stdin option for non-interactive secret input.");
        Console.Write(prompt);
        var value = new StringBuilder();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return value.ToString(); }
            if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; }
            else if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
        }
    }

}
