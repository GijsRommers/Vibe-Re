using System.Diagnostics;
using System.Text;
using VibeRe.Client;

namespace VibeRe.Cli;

internal sealed class ConsoleAuthInteraction : ISignInInteraction
{
    public void ReportStage(SignInStage stage)
    {
        var message = stage switch
        {
            SignInStage.WaitingForUser => "Waiting for you to finish signing in... (Ctrl+C to cancel)",
            SignInStage.Exchanging => "Finishing sign-in...",
            _ => null,
        };
        if (message is not null)
        {
            Console.WriteLine(message);
        }
    }

    public void OpenSignInUrl(Uri url, DateTimeOffset expiresAt)
    {
        var opened = TryOpenBrowser(url);
        Console.WriteLine(opened
            ? "Opened your browser to sign in. If it didn't open, visit:"
            : "Open this URL in a browser to sign in:");
        Console.WriteLine($"  {url}");
        Console.WriteLine($"The link expires at {expiresAt.ToLocalTime():HH:mm}.");
    }

    public async Task<string?> PromptSecretAsync(string prompt, CancellationToken cancellationToken)
    {
        Console.Write($"{prompt}: ");
        if (Console.IsInputRedirected)
        {
            return await Console.In.ReadLineAsync(cancellationToken);
        }

        var secret = new StringBuilder();
        while (true)
        {
            // Poll rather than block in ReadKey so Ctrl+C cancels immediately.
            while (!Console.KeyAvailable)
            {
                await Task.Delay(50, cancellationToken);
            }

            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (secret.Length > 0)
                {
                    secret.Length--;
                    Console.Write("\b \b");
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                secret.Append(key.KeyChar);
                Console.Write('*');
            }
        }

        Console.WriteLine();
        return secret.ToString();
    }

    private static bool TryOpenBrowser(Uri url)
    {
        try
        {
            var startInfo = OperatingSystem.IsMacOS() ? new ProcessStartInfo("open", [url.AbsoluteUri])
                : OperatingSystem.IsLinux() ? new ProcessStartInfo("xdg-open", [url.AbsoluteUri])
                : new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true };
            using var process = Process.Start(startInfo);
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
