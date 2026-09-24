using VibeRe.Application.Auth;
using VibeRe.Infrastructure.Credentials;

namespace VibeRe.Infrastructure.Tests.Credentials;

public sealed class FileCredentialStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("vibe-re-tests").FullName;
    private readonly FileCredentialStore _store;

    public FileCredentialStoreTests()
    {
        _store = new FileCredentialStore(Path.Combine(_dir, "nested", "credentials.json"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Round_trips_credentials_per_provider()
    {
        Assert.Null(await _store.GetAsync("a"));

        await _store.SetAsync(new Credential("a", "sk-a"));
        await _store.SetAsync(new Credential("b", "sk-b"));
        await _store.SetAsync(new Credential("a", "sk-a2"));

        Assert.Equal("sk-a2", (await _store.GetAsync("a"))?.Secret);
        Assert.True(await _store.DeleteAsync("a"));
        Assert.False(await _store.DeleteAsync("a"));
        Assert.Null(await _store.GetAsync("a"));
        Assert.Equal("sk-b", (await _store.GetAsync("b"))?.Secret);
    }

    [Fact]
    public async Task File_is_readable_only_by_owner()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix file modes only.
        }

        await _store.SetAsync(new Credential("a", "sk-a"));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_store.Path));
    }

    [Fact]
    public async Task Corrupt_file_is_reported_as_store_failure()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_store.Path)!);
        await File.WriteAllTextAsync(_store.Path, "{not json");

        var error = await Assert.ThrowsAsync<AuthException>(() => _store.GetAsync("a"));

        Assert.Equal(AuthErrorCode.StoreFailed, error.Code);
    }
}
