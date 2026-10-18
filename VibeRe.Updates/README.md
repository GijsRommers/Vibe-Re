# Desktop update integration

The production factory uses the public `https://github.com/GijsRommers/Vibe-Re` GitHub Releases feed without an access token. A private repository, unpublished releases, network failures, and malformed feeds can make checks fail. A failed request is never reported as “up to date.” No recurring check, silent download, or silent apply is scheduled by this library.

The desktop entry point must call `VelopackApp.Build().SetAutoApplyOnStartup(false).Run()` before ordinary startup, with the lifecycle installation/restart callbacks registered by `VibeRe.Desktop`. Disabling auto-apply is essential: Velopack otherwise applies downloaded packages on the next launch.

`DesktopUpdateService` is the application-facing API:

1. `CheckAsync()` selects a newer stable release for the installed native device channel.
2. `DownloadAsync()` downloads a full package and verifies size, SHA256, and its root `.nuspec` application ID, version, channel, RID, OS, and CPU architecture.
3. `ApplyAndRestartAsync(userConfirmed: true)` requires the explicit Restart and update action. It verifies the cached bytes again, acquires the installation's managed-server shutdown lease, verifies once more, and hands off to Velopack. The production apply call exits the current process. Only tests use a fake apply implementation.

`StateChanged` may fire off the UI thread. Subscribers must marshal UI updates. Concurrent commands are ignored instead of queued; a double click cannot queue a later restart. The service catches expected failures and supplies recoverable status text. Failed cache verification evicts only the selected regular cache file under Velopack's package lock; database, settings, keyring, log, and installed payload directories are never modified by this library.

Channels are `win-x64-stable`, `linux-x64-stable`, and `osx-arm64-stable`. Architecture-emulated processes, unrecognized application IDs/channels, prereleases, downgrades, and same-version reinstalls are refused. Updating does not migrate from older portable/Inno/development distributions; install the matching new desktop package first.

## Trust and recovery boundaries

SHA256 checks detect damaged or mismatched content relative to the selected HTTPS feed. They are not an independent publisher signature. GitHub repository/release permissions and any separately configured OS code signing remain important. Do not promise a full rollback, database rollback, or guaranteed macOS same-team verification. Application data must remain outside the installed package.

The shutdown lease is implemented in `VibeRe.Lifecycle`. It is held through apply and leaves a persistent replacement marker after process exit. The successful Velopack restart/installation hook completes that marker. The updater never kills servers by process name or assumes that a saved remote/local address proves process ownership.

## Tests

`dotnet test VibeRe.Updates.Tests/VibeRe.Updates.Tests.csproj`

Unit tests cover explicit consent/sequencing, concurrency, failures, cancellation, lifecycle guard failures, and repeated verification. Local-feed integration tests run the real pinned Velopack check/download paths using `SimpleFileSource` and `TestVelopackLocator`, including corrupted/cached packages, channels, version constraints, and user-data preservation. They deliberately never invoke an apply method with `TestVelopackLocator`; its `StartProcess` method launches real processes. Native installer/application replacement is tested separately by the packaging workflow's isolated fixture.

Pinned source references:

- [SDK integration and startup auto-apply](https://docs.velopack.io/integrating/overview)
- [Velopack 1.2.161 package metadata generation](https://github.com/velopack/velopack/blob/1.2.161/src/vpk/Velopack.Packaging/PackageBuilder.cs)
- [Velopack 1.2.161 download/cache behavior](https://github.com/velopack/velopack/blob/1.2.161/src/lib-csharp/UpdateManager.cs)
- [Velopack 1.2.161 test locator behavior](https://github.com/velopack/velopack/blob/1.2.161/src/lib-csharp/Locators/TestVelopackLocator.cs)
