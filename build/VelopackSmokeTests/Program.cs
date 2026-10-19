using System.Text.Json;
using Velopack;
using Velopack.Sources;

// Deliberately isolated from VibeRe: no provider, keyring, server or user database.
VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
if (args.Length == 0) return; // Installer's first-run launch.
var command = args[0];
var feed = args[1];
var report = args[2];
var manager = new UpdateManager(new SimpleFileSource(new DirectoryInfo(feed)));
if (!manager.IsInstalled) throw new InvalidOperationException("Native Velopack installation was not located.");
if (command == "inspect")
{
    File.WriteAllText(report + ".tmp", JsonSerializer.Serialize(new { Version = manager.CurrentVersion!.ToString(), AppId = manager.AppId }));
    File.Move(report + ".tmp", report, overwrite: true);
    return;
}
if (command != "update") throw new ArgumentException("Unknown fixture command.");
var update = await manager.CheckForUpdatesAsync() ?? throw new InvalidOperationException("No local fixture update found.");
await manager.DownloadUpdatesAsync(update);
// Only this disposable fixture invokes the real updater. The production UI's
// separate tests never apply an update against a synthetic locator.
manager.ApplyUpdatesAndRestart(update.TargetFullRelease, ["inspect", feed, report]);
