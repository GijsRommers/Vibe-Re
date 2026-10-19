using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;

namespace VibeRe.Updates;

/// <summary>
/// Uses public GitHub Releases, an explicit architecture-specific channel, and full packages only.
/// SHA256 protects package integrity against the selected HTTPS feed; it is not publisher authentication.
/// </summary>
public sealed class VelopackUpdateBackend : IUpdateBackend
{
    private readonly CheckedUpdateManager manager;
    private readonly IVelopackLocator locator;
    private UpdateCandidate? selected;
    private UpdateInfo? selectedInfo;
    public SemanticVersion? CurrentVersion => manager.CurrentVersion;
    public string RuntimeIdentifier { get; }
    public string Channel { get; }
    public string? UnavailableReason => !manager.IsInstalled
        ? "In-app updates are available in the installed desktop app. Install VibeRe from GitHub Releases first."
        : locator.AppId != UpdatePlatform.AppId
            ? "This installation has an unexpected application identity. Install VibeRe from GitHub Releases."
            : locator.Channel != Channel
                ? "This installation doesn't match this device's stable update channel. Install the matching VibeRe package from GitHub Releases."
                : null;

    public static IUpdateBackend CreateForCurrentPlatform()
    {
        var runtime = UpdatePlatform.CurrentRuntimeIdentifier;
        if (runtime is null) return new UnsupportedBackend();
        try
        {
            var backend = new VelopackUpdateBackend(
                new GithubSource(UpdatePlatform.RepositoryUrl, accessToken: null, prerelease: false), runtime);
            // Probe installation metadata here, so an unpacked/development launch can never fail due to updater setup.
            return backend.UnavailableReason is { } reason
                ? new UnsupportedBackend(reason, backend.CurrentVersion)
                : backend;
        }
        catch (Exception)
        {
            return new UnsupportedBackend("In-app updates couldn't locate this installation. Install VibeRe from GitHub Releases to enable updates.");
        }
    }

    /// <summary>Custom sources and locators are test seams, not user-configurable production feed overrides.</summary>
    public VelopackUpdateBackend(IUpdateSource source, string runtimeIdentifier, IVelopackLocator? locator = null)
    {
        RuntimeIdentifier = runtimeIdentifier;
        Channel = UpdatePlatform.ChannelFor(runtimeIdentifier);
        this.locator = locator ?? VelopackLocator.Current;
        manager = new CheckedUpdateManager(new ValidatingSource(source, ValidateDownloadedMetadataAsync), new UpdateOptions
        {
            ExplicitChannel = Channel,
            AllowVersionDowngrade = false,
            MaximumDeltasBeforeFallback = -1
        }, this.locator);
    }

    public async Task<UpdateCandidate?> CheckAsync(CancellationToken cancellationToken)
    {
        selected = null;
        selectedInfo = null;
        EnsureAvailable();
        cancellationToken.ThrowIfCancellationRequested();
        // This SDK has no cancellation parameter for checks. Await completion before releasing the service lock.
        var info = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (info is null) return null;
        ValidateAsset(info.TargetFullRelease);
        if (info.IsDowngrade || info.TargetFullRelease.Version <= CurrentVersion || info.TargetFullRelease.Version.IsPrerelease)
            throw new UpdateSafetyException("The feed returned an older or prerelease version. No update was installed.");
        // Use a full package only: no extra unvalidated delta paths and deterministic recovery on every platform.
        selectedInfo = new UpdateInfo(info.TargetFullRelease, false);
        selected = new UpdateCandidate(info.TargetFullRelease.Version, Channel, RuntimeIdentifier, info.TargetFullRelease.NotesMarkdown);
        return selected;
    }

    public async Task DownloadAsync(UpdateCandidate candidate, Action<int> progress, CancellationToken cancellationToken)
    {
        var info = GetSelected(candidate);
        await manager.DownloadUpdatesAsync(info, progress, cancellationToken).ConfigureAwait(false);
        // Velopack skips checksum verification for an already cached complete package. Always rehash it ourselves.
        await VerifyAsync(candidate, cancellationToken).ConfigureAwait(false);
    }

    public async Task VerifyAsync(UpdateCandidate candidate, CancellationToken cancellationToken)
    {
        var asset = GetSelected(candidate).TargetFullRelease;
        ValidateAsset(asset);
        var packagesDir = locator.PackagesDir ?? throw new UpdateSafetyException("The update cache could not be located. Reinstall VibeRe from GitHub Releases.");
        var path = Path.Combine(packagesDir, asset.FileName);
        using var updateLock = await manager.AcquirePackageLockAsync().ConfigureAwait(false);
        try { await VerifyPackageAsync(path, asset, candidate, cancellationToken).ConfigureAwait(false); }
        catch (UpdateSafetyException)
        {
            // This is only the selected leaf file in Velopack's cache, never the installation or user data.
            // Wait until the verification stream is disposed, and never follow a symbolic link's target.
            if (new FileInfo(path).LinkTarget is null)
            {
                try { File.Delete(path); }
                catch (IOException) { /* Keep the original safety error; a fresh installer is still an option. */ }
                catch (UnauthorizedAccessException) { /* Same safe recovery as an unwritable cache. */ }
            }
            throw;
        }
    }

    private async Task VerifyPackageAsync(string path, VelopackAsset asset, UpdateCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(path).LinkTarget is not null)
            throw new UpdateSafetyException("The cached update is a symbolic link and cannot be installed. Download a fresh installer from GitHub Releases.");
        if (!File.Exists(path)) throw new UpdateSafetyException("The downloaded update is missing. Download it again before restarting.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        if (stream.Length != asset.Size)
            throw IntegrityFailure();
        var actualHash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, Convert.FromHexString(asset.SHA256)))
            throw IntegrityFailure();
        stream.Position = 0;
        VerifyMetadata(stream, candidate);
    }

    private Task ValidateDownloadedMetadataAsync(VelopackAsset asset, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = File.OpenRead(path);
        VerifyMetadata(stream, new UpdateCandidate(asset.Version, Channel, RuntimeIdentifier));
        return Task.CompletedTask;
    }

    private void VerifyMetadata(Stream stream, UpdateCandidate candidate)
    {
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var manifests = zip.Entries.Where(entry => entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)
                && !entry.FullName.Contains('/') && !entry.FullName.Contains('\\')).ToArray();
            if (manifests.Length != 1 || manifests[0].Length > 1024 * 1024)
                throw new UpdateSafetyException("The update package has missing or invalid metadata and was not installed.");
            using var manifestStream = manifests[0].Open();
            using var reader = XmlReader.Create(manifestStream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 1024 * 1024
            });
            var document = XDocument.Load(reader);
            var metadata = document.Root?.Elements().SingleOrDefault(element => element.Name.LocalName == "metadata");
            string? Value(string name) => metadata?.Elements().SingleOrDefault(element => element.Name.LocalName == name)?.Value;
            var runtimeParts = RuntimeIdentifier.Split('-');
            if (Value("id") != UpdatePlatform.AppId || Value("channel") != Channel || Value("rid") != RuntimeIdentifier
                || Value("os") != runtimeParts[0] || Value("machineArchitecture") != runtimeParts[1]
                || !SemanticVersion.TryParse(Value("version"), out var version) || version != candidate.Version)
                throw new UpdateSafetyException("The update package is for a different app, version, device, or channel. It was not installed.");
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or InvalidOperationException)
        {
            throw new UpdateSafetyException("The update package is damaged or has invalid metadata. Download a fresh installer from GitHub Releases.");
        }
    }

    public void ApplyAndRestart(UpdateCandidate candidate)
    {
        var info = GetSelected(candidate);
        // The service re-verifies the package and holds the managed-server update lease before calling this.
        manager.ApplyUpdatesAndRestart(info.TargetFullRelease);
    }

    private UpdateInfo GetSelected(UpdateCandidate candidate)
    {
        EnsureAvailable();
        if (!ReferenceEquals(candidate, selected) || selectedInfo is null)
            throw new UpdateSafetyException("The selected update has changed. Check for updates again.");
        if (candidate.Version <= CurrentVersion || candidate.Version.IsPrerelease)
            throw new UpdateSafetyException("Only a newer stable version can be installed.");
        return selectedInfo;
    }

    private void EnsureAvailable()
    {
        if (UnavailableReason is { } reason) throw new UpdateSafetyException(reason);
    }

    private static UpdateSafetyException IntegrityFailure() => new(
        "The downloaded update failed its integrity check and was not installed. Check again and retry the download, or get a fresh installer from GitHub Releases.");

    private static void ValidateAsset(VelopackAsset asset)
    {
        if (asset.PackageId != UpdatePlatform.AppId || asset.Version is null || asset.Type != VelopackAssetType.Full
            || string.IsNullOrWhiteSpace(asset.FileName) || asset.FileName != Path.GetFileName(asset.FileName)
            || asset.FileName.IndexOfAny(['/', '\\', ':']) >= 0 || !asset.FileName.EndsWith("-full.nupkg", StringComparison.OrdinalIgnoreCase)
            || asset.FileName.Any(char.IsControl)
            || asset.Size <= 0 || asset.SHA256?.Length != 64 || !asset.SHA256.All(Uri.IsHexDigit))
            throw new UpdateSafetyException("The update feed contains invalid package information. No update was installed.");
    }

    private sealed class CheckedUpdateManager(IUpdateSource source, UpdateOptions options, IVelopackLocator locator)
        : UpdateManager(source, options, locator)
    {
        public Task<IDisposable> AcquirePackageLockAsync() => AcquireUpdateLock();
    }

    private sealed class ValidatingSource(IUpdateSource source,
        Func<VelopackAsset, string, CancellationToken, Task> verifyMetadata) : IUpdateSource
    {
        public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
            Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
        {
            var feed = await source.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease).ConfigureAwait(false);
            var full = feed.Assets.Where(asset => asset.Type == VelopackAssetType.Full).ToArray();
            if (full.Length == 0)
                throw new UpdateSafetyException("No compatible stable release has been published for this device yet. Open GitHub Releases or check again later.");
            foreach (var asset in full) ValidateAsset(asset);
            return new VelopackAssetFeed { Assets = full };
        }

        public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile,
            Action<int> progress, CancellationToken cancelToken)
        {
            ValidateAsset(releaseEntry);
            await source.DownloadReleaseEntry(logger, releaseEntry, localFile, progress, cancelToken).ConfigureAwait(false);
            // On Windows the SDK refreshes Update.exe after a download. Reject a mismatched package before that step.
            await verifyMetadata(releaseEntry, localFile, cancelToken).ConfigureAwait(false);
        }
    }

    private sealed class UnsupportedBackend(string? reason = null, SemanticVersion? currentVersion = null) : IUpdateBackend
    {
        public SemanticVersion? CurrentVersion => currentVersion;
        public string RuntimeIdentifier => "unsupported";
        public string Channel => "unsupported";
        public string? UnavailableReason => reason ?? "In-app updates support native Windows x64, Linux x64, and Apple Silicon macOS. Download the matching installer from GitHub Releases.";
        public Task<UpdateCandidate?> CheckAsync(CancellationToken cancellationToken) => throw new NotSupportedException(UnavailableReason);
        public Task DownloadAsync(UpdateCandidate candidate, Action<int> progress, CancellationToken cancellationToken) => throw new NotSupportedException(UnavailableReason);
        public Task VerifyAsync(UpdateCandidate candidate, CancellationToken cancellationToken) => throw new NotSupportedException(UnavailableReason);
        public void ApplyAndRestart(UpdateCandidate candidate) => throw new NotSupportedException(UnavailableReason);
    }
}
