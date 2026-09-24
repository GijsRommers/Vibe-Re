using System.Diagnostics;
using System.Runtime.Versioning;
using VibeRe.Application.Auth;

namespace VibeRe.Infrastructure.Credentials;

/// <summary>Stores credentials as generic passwords in the user's login keychain via <c>/usr/bin/security</c>.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacOsKeychainCredentialStore(string service = MacOsKeychainCredentialStore.DefaultService)
    : ICredentialStore
{
    public const string DefaultService = "vibe-re";

    private const string SecurityPath = "/usr/bin/security";
    private const int ItemNotFoundExitCode = 44; // errSecItemNotFound

    public async Task<Credential?> GetAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["find-generic-password", "-s", service, "-a", providerId, "-w"], null, cancellationToken);
        if (result.ExitCode == ItemNotFoundExitCode)
        {
            return null;
        }

        EnsureSuccess(result, "read from");
        var secret = result.StdOut.TrimEnd('\n');
        return secret.Length == 0 ? null : new Credential(providerId, secret);
    }

    public async Task SetAsync(Credential credential, CancellationToken cancellationToken = default)
    {
        // Pass the command on stdin (`security -i`) so the secret never shows up in the process list, and
        // hex-encode it (-X) because the interactive tokenizer has no reliable quoting for arbitrary text.
        var hexSecret = Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(credential.Secret));
        var command = $"add-generic-password -U -s {service} -a {credential.ProviderId} -X {hexSecret}\n";
        EnsureSuccess(await RunAsync(["-i"], command, cancellationToken), "write to");
    }

    public async Task<bool> DeleteAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["delete-generic-password", "-s", service, "-a", providerId], null, cancellationToken);
        if (result.ExitCode == ItemNotFoundExitCode)
        {
            return false;
        }

        EnsureSuccess(result, "delete from");
        return true;
    }

    private static void EnsureSuccess(ProcessResult result, string action)
    {
        if (result.ExitCode != 0)
        {
            throw new AuthException(
                $"Could not {action} the macOS keychain (security exited with {result.ExitCode}): {result.StdErr.Trim()}",
                AuthErrorCode.StoreFailed);
        }
    }

    private static async Task<ProcessResult> RunAsync(string[] arguments, string? stdin, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(SecurityPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
                            ?? throw new AuthException($"Could not start {SecurityPath}.", AuthErrorCode.StoreFailed);
        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken);
        }

        process.StandardInput.Close();
        var stdOut = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(process.ExitCode, await stdOut, await stdErr);
    }

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
}
