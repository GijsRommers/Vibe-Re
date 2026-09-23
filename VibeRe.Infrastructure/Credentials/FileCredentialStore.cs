using System.Text.Json;
using VibeRe.Core.Auth;

namespace VibeRe.Infrastructure.Credentials;

/// <summary>
/// Stores credentials in a JSON file readable only by the current user. Used where no OS keychain
/// integration exists yet.
/// </summary>
public sealed class FileCredentialStore(string path) : ICredentialStore
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string Path { get; } = path;

    public async Task<Credential?> GetAsync(string providerId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var secrets = await ReadAsync(cancellationToken);
            return secrets.TryGetValue(providerId, out var secret) ? new Credential(providerId, secret) : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetAsync(Credential credential, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var secrets = await ReadAsync(cancellationToken);
            secrets[credential.ProviderId] = credential.Secret;
            await WriteAsync(secrets, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteAsync(string providerId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var secrets = await ReadAsync(cancellationToken);
            if (!secrets.Remove(providerId))
            {
                return false;
            }

            await WriteAsync(secrets, cancellationToken);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<Dictionary<string, string>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(Path))
        {
            return new Dictionary<string, string>();
        }

        await using var stream = File.OpenRead(Path);
        try
        {
            return await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, cancellationToken: cancellationToken)
                   ?? new Dictionary<string, string>();
        }
        catch (JsonException ex)
        {
            throw new AuthException($"Credential file {Path} is corrupt.", AuthErrorCode.StoreFailed, ex);
        }
    }

    private async Task WriteAsync(Dictionary<string, string> secrets, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tempPath = $"{Path}.{Environment.ProcessId}.tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            // Create with owner-only permissions so the secret is never briefly world-readable.
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        await using (var stream = new FileStream(tempPath, options))
        {
            await JsonSerializer.SerializeAsync(stream, secrets, cancellationToken: cancellationToken);
        }

        File.Move(tempPath, Path, overwrite: true);
    }
}
