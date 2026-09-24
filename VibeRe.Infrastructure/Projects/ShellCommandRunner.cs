using System.Diagnostics;
using System.Text;
using VibeRe.Application.Projects;

namespace VibeRe.Infrastructure.Projects;

/// <summary>Runs commands through <c>/bin/sh -c</c> (<c>cmd /c</c> on Windows) with stdin closed.</summary>
public sealed class ShellCommandRunner : ICommandRunner
{
    public async Task<CommandResult> RunAsync(
        string command,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/s", "/c", command } }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", command } };
        startInfo.WorkingDirectory = workingDirectory;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[name] = value;
        }

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        DataReceivedEventHandler append = (_, line) =>
        {
            if (line.Data is null)
            {
                return;
            }

            lock (output)
            {
                output.AppendLine(line.Data);
            }
        };
        process.OutputDataReceived += append;
        process.ErrorDataReceived += append;

        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        // The parameterless wait flushes the redirected streams, which the async wait doesn't guarantee.
        process.WaitForExit();
        lock (output)
        {
            return new CommandResult(process.ExitCode, output.ToString());
        }
    }
}
