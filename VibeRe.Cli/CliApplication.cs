using Grpc.Core;
using VibeRe.Client.Configuration;

namespace VibeRe.Cli;

public sealed class CliApplication(AuthCommands auth, ServerCommands servers)
{
    public async Task<int> RunAsync(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            var options = CliOptions.Parse(args);
            if (options.ShowHelp) { PrintHelp(); return 0; }
            return options.IsServerCommand
                ? await servers.ExecuteAsync(options, cancellation.Token)
                : await auth.ExecuteAsync(options, cancellation.Token);
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Canceled."); return 130; }
        catch (RpcException e)
        {
            Console.Error.WriteLine(e.StatusCode == StatusCode.Unavailable
                ? "Could not connect to the server. Check the configured address." : e.Status.Detail);
            return 1;
        }
        catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 2; }
        catch (LocalServerStartupException e) { Console.Error.WriteLine(e.Message); return 1; }
        catch { Console.Error.WriteLine("Could not access saved server settings or credential storage."); return 1; }
        finally { Console.CancelKeyPress -= onCancel; }
    }

    private static void PrintHelp() => Console.WriteLine("""
        vibe-re [--server NAME]                 Connect and open Mistral login when signed out
        vibe-re [--server NAME] auth status
        vibe-re [--server NAME] auth login [--method browser|api-key] [--api-key-stdin]
        vibe-re [--server NAME] auth logout
        vibe-re servers list
        vibe-re servers add NAME URL [--local]
        vibe-re servers update NAME URL
        vibe-re servers use NAME
        vibe-re servers remove NAME
        vibe-re servers local NAME on|off
        vibe-re servers token NAME [--token-stdin]

        Browser login is the default. API-key login reads MISTRAL_API_KEY or prompts without echo.
        The default local server starts automatically when needed. Remote servers never start a local process.
        Server settings are shared with desktop. Tokens are saved in the OS credential store.
        VIBE_RE_CLIENT_DATABASE overrides the client database path; VIBE_RE_SERVER_TOKEN overrides the selected server token.
        """);
}
