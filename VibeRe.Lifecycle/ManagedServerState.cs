using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VibeRe.Lifecycle;

internal sealed class ManagedServerState
{
    internal const string PipeEnvironment = "VIBE_RE_MANAGED_SERVER_PIPE";
    internal const string DirectoryEnvironment = "VIBE_RE_MANAGED_SERVER_DIRECTORY";
    internal const string IdentityEnvironment = "VIBE_RE_MANAGED_SERVER_IDENTITY";
    internal const int ProtocolVersion = 1;
    internal static readonly string BuildIdentity = typeof(ManagedServerState).Assembly.ManifestModule.ModuleVersionId.ToString("N");
    private static readonly JsonSerializerOptions JsonOptions = new() { RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, MaxDepth = 8 };
    internal static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal ManagedServerState(string installationDirectory, string? stateRoot = null, string? appImage = null, string? appDir = null)
    {
        InstallationDirectory = ResolveInstallationDirectory(installationDirectory);
        appImage ??= Environment.GetEnvironmentVariable("APPIMAGE");
        appDir ??= Environment.GetEnvironmentVariable("APPDIR");
        // APPIMAGE identifies the durable file; APPDIR is a different temporary mount on each launch.
        // Only use it when this executable actually belongs to that mount.
        var imageInstall = OperatingSystem.IsLinux() && !string.IsNullOrWhiteSpace(appImage) && Path.IsPathFullyQualified(appImage)
            && !string.IsNullOrWhiteSpace(appDir) && IsWithin(InstallationDirectory, Normalize(appDir));
        var identityPath = imageInstall ? Normalize(appImage!) : InstallationDirectory;
        if (OperatingSystem.IsWindows()) identityPath = identityPath.ToUpperInvariant();
        InstallationId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identityPath)));
        IsAppImage = imageInstall;
        AppImagePath = imageInstall ? Normalize(appImage!) : null;
        DirectoryPath = Path.Combine(stateRoot ?? DefaultStateRoot(), InstallationId);
        EnsurePrivateDirectory(DirectoryPath);
    }

    internal string InstallationDirectory { get; }
    internal string InstallationId { get; }
    internal bool IsAppImage { get; }
    internal string? AppImagePath { get; }
    internal string DirectoryPath { get; }
    internal string UpdatePath => Path.Combine(DirectoryPath, "update.json");
    internal string DescriptorPath(string pipe) => Path.Combine(DirectoryPath, ValidatePipe(pipe) + ".json");
    internal string PendingPath(string pipe) => Path.Combine(DirectoryPath, ValidatePipe(pipe) + ".pending");

    private static string DefaultStateRoot()
    {
        // On a fresh Unix account the configured app-data directory may not exist yet.
        // Resolve its location without requiring existence; EnsurePrivateDirectory creates our state.
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(appData) || !Path.IsPathFullyQualified(appData))
            throw new InvalidOperationException("The current user's application-data directory is unavailable. Local startup and updates cannot safely continue.");
        return Path.Combine(appData, "VibeRe", "managed-servers");
    }

    private static string ResolveInstallationDirectory(string directory)
    {
        var fullPath = Normalize(directory);
        var parent = Directory.GetParent(fullPath)?.FullName;
        // The desktop distribution contains a nested CLI, but they replace as one installation.
        if (Path.GetFileName(fullPath).Equals("cli", PathComparison) && parent is not null
            && File.Exists(Path.Combine(parent, "VibeRe.Desktop.dll")) && Directory.Exists(Path.Combine(parent, "server")))
            return Normalize(parent);
        return fullPath;
    }

    internal static string Normalize(string path)
    {
        // GetFullPath alone is lexical: on macOS /var and /private/var are aliases, and
        // launcher, process inspection, and updater can observe different spellings.
        // Resolve every existing ancestor, not just a symlink at the final path segment.
        var remainingLinks = 64;
        return Canonicalize(Path.GetFullPath(path), ref remainingLinks);
    }

    private static string Canonicalize(string path, ref int remainingLinks)
    {
        path = RemoveExtendedWindowsPrefix(Path.GetFullPath(path));
        var root = Path.GetPathRoot(path) ?? throw new InvalidOperationException("The application path has no filesystem root.");
        var parts = path[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index < parts.Length; index++)
        {
            current = Path.Combine(current, parts[index]);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // Launch reservations and tests can refer to a not-yet-created trailing path.
                // Its existing ancestors have already been canonicalized.
                for (index++; index < parts.Length; index++) current = Path.Combine(current, parts[index]);
                return Path.TrimEndingDirectorySeparator(current);
            }
            if ((attributes & FileAttributes.ReparsePoint) == 0) continue;
            if (--remainingLinks < 0) throw new InvalidOperationException("The application path contains too many symbolic links.");
            var target = (attributes & FileAttributes.Directory) != 0
                ? Directory.ResolveLinkTarget(current, returnFinalTarget: true)
                : File.ResolveLinkTarget(current, returnFinalTarget: true);
            if (target is null) throw new InvalidOperationException("An application path link could not be verified.");
            current = Canonicalize(target.FullName, ref remainingLinks);
        }
        return Path.TrimEndingDirectorySeparator(current);
    }

    private static string RemoveExtendedWindowsPrefix(string path)
    {
        if (!OperatingSystem.IsWindows()) return path;
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) && path.Length > 6 && path[5] == ':') return path[4..];
        return path;
    }
    internal static bool IsWithin(string path, string directory)
    {
        var canonicalPath = Normalize(path);
        var canonicalDirectory = Normalize(directory);
        var prefix = Path.EndsInDirectorySeparator(canonicalDirectory) ? canonicalDirectory : canonicalDirectory + Path.DirectorySeparatorChar;
        return canonicalPath.Equals(canonicalDirectory, PathComparison) || canonicalPath.StartsWith(prefix, PathComparison);
    }
    internal static string ValidatePipe(string pipe)
    {
        if (!pipe.StartsWith("vibere-", StringComparison.Ordinal) || pipe.Length != 39 || pipe[7..].Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidOperationException("The local server ownership record is invalid. Repair the application before updating.");
        return pipe;
    }

    internal FileStream AcquireGate()
    {
        try
        {
            return OpenPrivate(Path.Combine(DirectoryPath, "launch-update.lock"), FileMode.OpenOrCreate, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("A local server is starting or another update is in progress. Try again when it finishes.", exception);
        }
    }

    internal void ThrowIfUpdatePending()
    {
        if (File.Exists(UpdatePath)) throw new InvalidOperationException(
            "An application update is still pending. Open the updated app when it finishes. If it failed, finish reinstalling the app, then run the desktop app with --repair-update; your saved data is preserved.");
    }

    internal T Read<T>(string path)
    {
        AssertPrivateFile(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > 16 * 1024) throw new InvalidOperationException("The local server ownership record is invalid. Repair the application before updating.");
        try
        {
            return JsonSerializer.Deserialize<T>(stream, JsonOptions) ?? throw new InvalidOperationException("The local server ownership record is incomplete. Repair the application before updating.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The local server ownership record is invalid. Repair the application before updating.", exception);
        }
    }

    internal void Write<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = OpenPrivate(temporary, FileMode.CreateNew, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    internal static FileStream OpenPrivate(string path, FileMode mode, FileShare share)
    {
        if (File.Exists(path)) AssertPrivateFile(path);
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.ReadWrite, Share = share };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    private static void EnsurePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The local server state directory must not be a symbolic link.");
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new InvalidOperationException("The local server state directory is accessible to other users. Restrict its permissions before updating.");
    }

    private static void AssertPrivateFile(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("A local server ownership record must not be a symbolic link.");
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new InvalidOperationException("A local server ownership record is accessible to other users. Restrict its permissions before updating.");
    }
}

internal sealed record ManagedServerDescriptor(int Protocol, string InstallationId, string InstallationDirectory,
    string PipeName, int ProcessId, string StartIdentity, string ExecutablePath);
internal sealed record PendingServerLaunch(int Protocol, string InstallationId, string PipeName, string ExpectedExecutable,
    int LauncherProcessId, string LauncherStartIdentity, int? ProcessId = null, string? StartIdentity = null);
internal sealed record PendingApplicationUpdate(int Protocol, string InstallationId, string BuildIdentity, string LeaseId);

internal sealed record ManagedProcessIdentity(int ProcessId, string StartIdentity, string ExecutablePath);
internal interface IManagedProcessInspector
{
    ManagedProcessIdentity? Inspect(int processId);
    IReadOnlyList<ManagedProcessIdentity> FindProcessesUsingInstallation(string installationDirectory, string? appImagePath = null);
    Task WaitForExitAsync(ManagedProcessIdentity identity, CancellationToken cancellationToken);
    bool HasPotentialUpdater();
}
internal sealed class ManagedProcessInspector : IManagedProcessInspector
{
    public ManagedProcessIdentity? Inspect(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited) return null;
            var startIdentity = ManagedProcessStartIdentity.Read(process);
            var path = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("The local server executable could not be verified. Close it yourself before updating.");
            var executable = ManagedServerState.Normalize(path);
            if (startIdentity != ManagedProcessStartIdentity.Read(process))
                throw new InvalidOperationException("The local server process changed while its identity was being verified. Try the update again.");
            return new(process.Id, startIdentity, executable);
        }
        catch (ArgumentException) { return null; }
        catch (FileNotFoundException) { return null; } // Exited between process enumeration and /proc inspection.
        catch (DirectoryNotFoundException) { return null; }
        catch (InvalidOperationException) { throw; }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or NotSupportedException)
        { throw new InvalidOperationException("The local server process could not be verified. Close it yourself before updating.", exception); }
    }

    public IReadOnlyList<ManagedProcessIdentity> FindProcessesUsingInstallation(string installationDirectory, string? appImagePath = null)
    {
        var result = new List<ManagedProcessIdentity>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                string? name = null;
                try
                {
                    name = process.ProcessName;
                    var identity = Inspect(process.Id);
                    if (identity is null) continue;
                    if (ManagedServerState.IsWithin(identity.ExecutablePath, installationDirectory) || BelongsToAppImage(identity, appImagePath))
                    { result.Add(identity); continue; }
                    // Also catch hosts such as `dotnet /installation/server.dll` where the OS exposes
                    // mapped modules. Unknown users' unrelated processes are not ours to inspect or stop.
                    foreach (ProcessModule module in process.Modules)
                    {
                        if (!string.IsNullOrWhiteSpace(module.FileName)
                            && ManagedServerState.IsWithin(module.FileName, installationDirectory))
                        { result.Add(identity); break; }
                    }
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException)
                {
                    if (name is "vibe-re-server" or "VibeRe.Desktop" or "vibe-re")
                        throw new InvalidOperationException("Another VibeRe process could not be inspected. Close other VibeRe apps and user-managed local servers before updating.", exception);
                }
            }
        }
        return result;
    }

    public bool HasPotentialUpdater()
    {
        // Repair is intentionally conservative: an unrelated application's Velopack update may also
        // defer it. Updaters can execute outside current/ or from a temporary copy of an AppImage.
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                try
                {
                    if (process.ProcessName.Equals("Update", StringComparison.OrdinalIgnoreCase)
                        || process.ProcessName.Equals("UpdateNix", StringComparison.OrdinalIgnoreCase)
                        || process.ProcessName.Equals("UpdateMac", StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (InvalidOperationException) { } // Exited during enumeration.
                catch (System.ComponentModel.Win32Exception)
                { throw new InvalidOperationException("Running update processes could not be verified. Finish installers and updates before repairing VibeRe."); }
            }
        }
        return false;
    }

    private static bool BelongsToAppImage(ManagedProcessIdentity process, string? appImagePath)
    {
        if (!OperatingSystem.IsLinux() || appImagePath is null) return false;
        try
        {
            // Previous AppImage runs can have different temporary mounts. Inspect only the two launcher
            // identifiers, never log/store this environment, and require the executable to be inside APPDIR.
            var environment = File.ReadAllText($"/proc/{process.ProcessId}/environ").Split('\0');
            var image = environment.FirstOrDefault(item => item.StartsWith("APPIMAGE=", StringComparison.Ordinal))?[9..];
            var directory = environment.FirstOrDefault(item => item.StartsWith("APPDIR=", StringComparison.Ordinal))?[7..];
            return image is not null && directory is not null && Path.IsPathFullyQualified(image) && Path.IsPathFullyQualified(directory)
                && ManagedServerState.Normalize(image).Equals(appImagePath, ManagedServerState.PathComparison)
                && ManagedServerState.IsWithin(process.ExecutablePath, directory);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public async Task WaitForExitAsync(ManagedProcessIdentity identity, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            if (ManagedProcessStartIdentity.Read(process) != identity.StartIdentity) return; // Original process already exited.
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (ArgumentException) { } // Already exited.
        catch (FileNotFoundException) { } // /proc entry disappeared after obtaining the Process object.
        catch (DirectoryNotFoundException) { }
        catch (InvalidOperationException) when (Inspect(identity.ProcessId) is null) { }
        catch (IOException) when (Inspect(identity.ProcessId) is null) { }
    }
}
