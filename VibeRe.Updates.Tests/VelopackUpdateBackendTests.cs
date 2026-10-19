using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Velopack;
using Velopack.Exceptions;
using Velopack.Locators;
using Velopack.Sources;

namespace VibeRe.Updates.Tests;

/// <summary>Uses real Velopack feed/check/download code. Never call Apply with TestVelopackLocator: it starts real processes.</summary>
public sealed class VelopackUpdateBackendTests
{
    [Fact]
    public async Task Local_feed_checks_downloads_and_verifies_without_touching_user_data()
    {
        using var fixture = new LocalFeed();
        var asset = await fixture.PublishAsync();
        var backend = fixture.Backend();
        var candidate = Assert.IsType<UpdateCandidate>(await backend.CheckAsync(default));
        Assert.Equal("1.1.0", candidate.Version.ToString());
        Assert.Equal("linux-x64-stable", candidate.Channel);
        Assert.Equal("linux-x64", candidate.RuntimeIdentifier);
        Assert.False(File.Exists(Path.Combine(fixture.Cache, asset.FileName)));
        await backend.DownloadAsync(candidate, _ => { }, default);
        await backend.VerifyAsync(candidate, default);
        Assert.True(File.Exists(Path.Combine(fixture.Cache, asset.FileName)));
        Assert.Equal("keep database bytes", File.ReadAllText(fixture.Database));
        Assert.Equal("keep keyring bytes", File.ReadAllText(fixture.Keyring));
        Assert.Equal("installed 1.0.0", File.ReadAllText(fixture.InstalledBinary));
        Assert.Null(fixture.Locator.ExitCode);
    }

    [Theory]
    [InlineData("0.9.0")]
    [InlineData("1.0.0")]
    public async Task Local_feed_never_downgrades_or_reinstalls_current_version(string version)
    {
        using var fixture = new LocalFeed();
        await fixture.PublishAsync(version: version);
        Assert.Null(await fixture.Backend().CheckAsync(default));
    }

    [Fact]
    public async Task Empty_feed_reports_unpublished_release_instead_of_up_to_date()
    {
        using var fixture = new LocalFeed();
        File.WriteAllText(fixture.FeedFile, "{\"Assets\":[]}");
        var exception = await Assert.ThrowsAsync<UpdateSafetyException>(() => fixture.Backend().CheckAsync(default));
        Assert.Contains("No compatible stable release has been published", exception.Message);
    }

    [Fact]
    public async Task Malformed_feed_is_an_error_instead_of_no_update()
    {
        using var fixture = new LocalFeed();
        File.WriteAllText(fixture.FeedFile, "not json");
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Backend().CheckAsync(default));
    }

    [Fact]
    public async Task Missing_package_download_is_an_error()
    {
        using var fixture = new LocalFeed();
        var asset = await fixture.PublishAsync();
        var backend = fixture.Backend();
        var candidate = (await backend.CheckAsync(default))!;
        File.Delete(Path.Combine(fixture.Feed, asset.FileName));
        await Assert.ThrowsAnyAsync<Exception>(() => backend.DownloadAsync(candidate, _ => { }, default));
        Assert.Null(fixture.Locator.ExitCode);
    }

    [Fact]
    public async Task Real_download_rejects_a_checksum_mismatch()
    {
        using var fixture = new LocalFeed();
        var asset = await fixture.PublishAsync();
        fixture.WriteFeed(asset with { SHA256 = new string('0', 64) });
        var backend = fixture.Backend();
        var candidate = (await backend.CheckAsync(default))!;
        await Assert.ThrowsAsync<ChecksumFailedException>(() => backend.DownloadAsync(candidate, _ => { }, default));
        Assert.False(File.Exists(Path.Combine(fixture.Cache, asset.FileName)));
        Assert.Null(fixture.Locator.ExitCode);
    }

    [Fact]
    public async Task Cached_complete_package_is_rehashed_even_when_Velopack_skips_download()
    {
        using var fixture = new LocalFeed();
        var asset = await fixture.PublishAsync();
        var backend = fixture.Backend();
        var candidate = (await backend.CheckAsync(default))!;
        var corrupt = File.ReadAllBytes(Path.Combine(fixture.Feed, asset.FileName));
        corrupt[^1] ^= 0x01; // Same size, different hash.
        File.WriteAllBytes(Path.Combine(fixture.Cache, asset.FileName), corrupt);
        var error = await Assert.ThrowsAsync<UpdateSafetyException>(() => backend.DownloadAsync(candidate, _ => { }, default));
        Assert.Contains("integrity", error.Message);
        Assert.False(File.Exists(Path.Combine(fixture.Cache, asset.FileName)));
        await backend.DownloadAsync(candidate, _ => { }, default);
        await backend.VerifyAsync(candidate, default);
        Assert.Equal("keep database bytes", File.ReadAllText(fixture.Database));
        Assert.Equal("keep keyring bytes", File.ReadAllText(fixture.Keyring));
        Assert.Null(fixture.Locator.ExitCode);
    }

    [Fact]
    public async Task Symlink_in_cache_is_rejected_without_touching_its_target()
    {
        if (OperatingSystem.IsWindows()) return; // Symlink creation depends on Windows developer mode/elevation.
        using var fixture = new LocalFeed();
        var asset = await fixture.PublishAsync();
        var backend = fixture.Backend();
        var candidate = (await backend.CheckAsync(default))!;
        var cachePath = Path.Combine(fixture.Cache, asset.FileName);
        File.CreateSymbolicLink(cachePath, fixture.Database);
        await Assert.ThrowsAsync<UpdateSafetyException>(() => backend.VerifyAsync(candidate, default));
        Assert.Equal("keep database bytes", File.ReadAllText(fixture.Database));
        Assert.NotNull(new FileInfo(cachePath).LinkTarget);
    }

    [Fact]
    public async Task Deleted_cached_package_can_be_downloaded_again()
    {
        using var fixture = new LocalFeed();
        var asset = await fixture.PublishAsync();
        var backend = fixture.Backend();
        var candidate = (await backend.CheckAsync(default))!;
        await backend.DownloadAsync(candidate, _ => { }, default);
        File.Delete(Path.Combine(fixture.Cache, asset.FileName));
        await Assert.ThrowsAsync<UpdateSafetyException>(() => backend.VerifyAsync(candidate, default));
        await backend.DownloadAsync(candidate, _ => { }, default);
        await backend.VerifyAsync(candidate, default);
    }

    [Fact]
    public async Task Package_changed_after_download_is_rejected_before_apply()
    {
        using var fixture = new LocalFeed();
        var asset = await fixture.PublishAsync();
        var backend = fixture.Backend();
        var candidate = (await backend.CheckAsync(default))!;
        await backend.DownloadAsync(candidate, _ => { }, default);
        File.AppendAllText(Path.Combine(fixture.Cache, asset.FileName), "changed");
        await Assert.ThrowsAsync<UpdateSafetyException>(() => backend.VerifyAsync(candidate, default));
    }

    [Theory]
    [InlineData("Other.App", "1.1.0", "linux-x64-stable", "linux-x64")]
    [InlineData("GijsRommers.VibeRe", "1.2.0", "linux-x64-stable", "linux-x64")]
    [InlineData("GijsRommers.VibeRe", "1.1.0", "win-x64-stable", "linux-x64")]
    [InlineData("GijsRommers.VibeRe", "1.1.0", "linux-x64-stable", "osx-arm64")]
    public async Task Matching_checksum_does_not_bypass_package_identity_checks(string id, string version, string channel, string rid)
    {
        using var fixture = new LocalFeed();
        await fixture.PublishAsync(manifestId: id, manifestVersion: version, manifestChannel: channel, manifestRid: rid);
        var backend = fixture.Backend();
        var candidate = (await backend.CheckAsync(default))!;
        await Assert.ThrowsAsync<UpdateSafetyException>(() => backend.DownloadAsync(candidate, _ => { }, default));
        Assert.Null(fixture.Locator.ExitCode);
    }

    [Theory]
    [InlineData("../escape-full.nupkg")]
    [InlineData("..\\escape-full.nupkg")]
    [InlineData("C:escape-full.nupkg")]
    [InlineData("https://evil.example/package-full.nupkg")]
    public async Task Feed_cannot_choose_an_arbitrary_file_path(string name)
    {
        using var fixture = new LocalFeed();
        var asset = await fixture.PublishAsync();
        fixture.WriteFeed(asset with { FileName = name });
        await Assert.ThrowsAsync<UpdateSafetyException>(() => fixture.Backend().CheckAsync(default));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public async Task SHA256_is_required_even_if_feed_includes_SHA1(string hash)
    {
        using var fixture = new LocalFeed();
        var asset = await fixture.PublishAsync();
        fixture.WriteFeed(asset with { SHA256 = hash });
        await Assert.ThrowsAsync<UpdateSafetyException>(() => fixture.Backend().CheckAsync(default));
    }

    [Fact]
    public async Task Feed_package_identity_must_match_application()
    {
        using var fixture = new LocalFeed();
        var asset = await fixture.PublishAsync();
        fixture.WriteFeed(asset with { PackageId = "Other.App" });
        await Assert.ThrowsAsync<UpdateSafetyException>(() => fixture.Backend().CheckAsync(default));
    }

    [Fact]
    public async Task Prerelease_is_not_accepted_from_stable_feed()
    {
        using var fixture = new LocalFeed();
        await fixture.PublishAsync(version: "2.0.0-beta.1");
        await Assert.ThrowsAsync<UpdateSafetyException>(() => fixture.Backend().CheckAsync(default));
    }

    [Fact]
    public async Task Installed_channel_must_match_native_device_channel()
    {
        using var fixture = new LocalFeed();
        var backend = fixture.Backend(runtime: "win-x64");
        Assert.NotNull(backend.UnavailableReason);
        await Assert.ThrowsAsync<UpdateSafetyException>(() => backend.CheckAsync(default));
    }

    [Fact]
    public async Task Previously_selected_candidate_cannot_be_applied_after_a_new_check()
    {
        using var fixture = new LocalFeed();
        await fixture.PublishAsync();
        var backend = fixture.Backend();
        var previous = (await backend.CheckAsync(default))!;
        await backend.CheckAsync(default);
        await Assert.ThrowsAsync<UpdateSafetyException>(() => backend.DownloadAsync(previous, _ => { }, default));
    }

    [Fact]
    public async Task Official_Github_source_preserves_release_asset_identity_through_validation()
    {
        using var fixture = new LocalFeed();
        var asset = await fixture.PublishAsync();
        var downloader = new FakeGithubDownloader(fixture, asset);
        var source = new GithubSource(UpdatePlatform.RepositoryUrl, null, false, downloader);
        var backend = new VelopackUpdateBackend(source, "linux-x64", fixture.Locator);
        var candidate = Assert.IsType<UpdateCandidate>(await backend.CheckAsync(default));
        await backend.DownloadAsync(candidate, _ => { }, default);
        await backend.VerifyAsync(candidate, default);
        Assert.Contains(downloader.Requests, url => url.StartsWith("https://api.github.com/repos/GijsRommers/Vibe-Re/releases", StringComparison.Ordinal));
        Assert.Contains(downloader.Requests, url => url.EndsWith("releases.linux-x64-stable.json", StringComparison.Ordinal));
        Assert.Contains(downloader.Requests, url => url.EndsWith(asset.FileName, StringComparison.Ordinal));
        Assert.False(downloader.AuthorizationWasSent);
        Assert.Null(fixture.Locator.ExitCode);
    }

    [Fact]
    public async Task Official_Github_source_with_no_published_releases_is_not_up_to_date()
    {
        using var fixture = new LocalFeed();
        var asset = await fixture.PublishAsync();
        var downloader = new FakeGithubDownloader(fixture, asset) { NoReleases = true };
        var backend = new VelopackUpdateBackend(new GithubSource(UpdatePlatform.RepositoryUrl, null, false, downloader), "linux-x64", fixture.Locator);
        var exception = await Assert.ThrowsAsync<UpdateSafetyException>(() => backend.CheckAsync(default));
        Assert.Contains("published", exception.Message);
        Assert.Single(downloader.Requests);
    }

    [Fact]
    public void Production_factory_is_safe_for_an_unpacked_development_process()
    {
        var backend = VelopackUpdateBackend.CreateForCurrentPlatform();
        Assert.NotNull(backend);
        Assert.NotNull(backend.UnavailableReason);
    }

    [Theory]
    [InlineData("win", Architecture.X64, Architecture.X64, "win-x64")]
    [InlineData("linux", Architecture.X64, Architecture.X64, "linux-x64")]
    [InlineData("osx", Architecture.Arm64, Architecture.Arm64, "osx-arm64")]
    [InlineData("osx", Architecture.X64, Architecture.Arm64, null)]
    [InlineData("linux", Architecture.Arm64, Architecture.Arm64, null)]
    [InlineData("win", Architecture.X64, Architecture.Arm64, null)]
    [InlineData("win", Architecture.X86, Architecture.X64, null)]
    public void Channel_selection_never_silently_switches_architecture(string os, Architecture process, Architecture host, string? expected)
        => Assert.Equal(expected, UpdatePlatform.Resolve(os, process, host));

    private sealed class FakeGithubDownloader(LocalFeed fixture, VelopackAsset asset) : IFileDownloader
    {
        public List<string> Requests { get; } = [];
        public bool NoReleases { get; init; }
        public bool AuthorizationWasSent { get; private set; }
        private void Record(string url, IDictionary<string, string>? headers)
        {
            Requests.Add(url);
            AuthorizationWasSent |= headers?.Keys.Any(key => key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) == true;
        }

        public Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        {
            Record(url, headers);
            Assert.StartsWith("https://api.github.com/repos/GijsRommers/Vibe-Re/releases", url);
            if (NoReleases) return Task.FromResult("[]");
            var root = UpdatePlatform.RepositoryUrl + "/releases/download/v1.1.0/";
            return Task.FromResult(JsonSerializer.Serialize(new[]
            {
                new
                {
                    name = "v1.1.0", prerelease = false, published_at = "2026-01-01T00:00:00Z",
                    assets = new[]
                    {
                        new { name = Path.GetFileName(fixture.FeedFile), browser_download_url = root + Path.GetFileName(fixture.FeedFile) },
                        new { name = asset.FileName, browser_download_url = root + asset.FileName }
                    }
                }
            }));
        }

        public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        {
            Record(url, headers);
            Assert.EndsWith("releases.linux-x64-stable.json", url);
            return File.ReadAllBytesAsync(fixture.FeedFile);
        }

        public Task DownloadFile(string url, string targetFile, Action<int> progress,
            IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default)
        {
            Record(url, headers);
            Assert.EndsWith(asset.FileName, url);
            File.Copy(Path.Combine(fixture.Feed, asset.FileName), targetFile);
            progress(100);
            return Task.CompletedTask;
        }
    }

    private sealed class LocalFeed : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "vibere-update-tests-" + Guid.NewGuid().ToString("N"));
        public string Feed { get; }
        public string Cache { get; }
        public string FeedFile => Path.Combine(Feed, "releases.linux-x64-stable.json");
        public string Database { get; }
        public string Keyring { get; }
        public string InstalledBinary { get; }
        public TestVelopackLocator Locator { get; }

        public LocalFeed()
        {
            Feed = Directory.CreateDirectory(Path.Combine(root, "feed")).FullName;
            Cache = Directory.CreateDirectory(Path.Combine(root, "installation", "packages")).FullName;
            var app = Directory.CreateDirectory(Path.Combine(root, "installation", "current")).FullName;
            var data = Directory.CreateDirectory(Path.Combine(root, "user-data")).FullName;
            Database = Path.Combine(data, "vibere.db");
            Keyring = Path.Combine(data, "keyring");
            InstalledBinary = Path.Combine(app, "VibeRe.Desktop");
            File.WriteAllText(Database, "keep database bytes");
            File.WriteAllText(Keyring, "keep keyring bytes");
            File.WriteAllText(InstalledBinary, "installed 1.0.0");
            Locator = new TestVelopackLocator(UpdatePlatform.AppId, "1.0.0", Cache, app,
                Path.Combine(root, "installation"), Path.Combine(root, "installation", "Update.exe"), "linux-x64-stable");
        }

        public VelopackUpdateBackend Backend(string runtime = "linux-x64") => new(new SimpleFileSource(new DirectoryInfo(Feed)), runtime, Locator);

        public async Task<VelopackAsset> PublishAsync(string version = "1.1.0", string? manifestId = null,
            string? manifestVersion = null, string? manifestChannel = null, string? manifestRid = null)
        {
            var name = $"GijsRommers.VibeRe-{version}-linux-x64-stable-full.nupkg";
            var path = Path.Combine(Feed, name);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("GijsRommers.VibeRe.nuspec").Open(), Encoding.UTF8))
                    writer.Write($"""
                        <?xml version="1.0" encoding="utf-8"?>
                        <package xmlns="http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd"><metadata>
                          <id>{manifestId ?? UpdatePlatform.AppId}</id><version>{manifestVersion ?? version}</version>
                          <title>VibeRe</title><authors>VibeRe</authors><description>Test fixture</description>
                          <channel>{manifestChannel ?? "linux-x64-stable"}</channel><rid>{manifestRid ?? "linux-x64"}</rid>
                          <os>linux</os><machineArchitecture>x64</machineArchitecture><mainExe>VibeRe.Desktop</mainExe>
                        </metadata></package>
                        """);
                using var payload = new StreamWriter(zip.CreateEntry("lib/app/GijsRommers.VibeRe.AppImage").Open());
                payload.Write("Inert local-feed fixture; never execute or apply.");
            }
            var bytes = await File.ReadAllBytesAsync(path);
            var asset = new VelopackAsset
            {
                PackageId = UpdatePlatform.AppId, Version = SemanticVersion.Parse(version), Type = VelopackAssetType.Full,
                FileName = name, Size = bytes.Length, SHA256 = Convert.ToHexString(SHA256.HashData(bytes)), SHA1 = Convert.ToHexString(SHA1.HashData(bytes))
            };
            WriteFeed(asset);
            return asset;
        }

        public void WriteFeed(VelopackAsset asset) => File.WriteAllText(FeedFile, JsonSerializer.Serialize(new
        {
            Assets = new[] { new { asset.PackageId, Version = asset.Version.ToString(), Type = "Full", asset.FileName, asset.Size, asset.SHA256, asset.SHA1 } }
        }));

        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
