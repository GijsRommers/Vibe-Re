# Build and install VibeRe

## Download and install

The **Build, test and package** GitHub Actions workflow builds Windows x64,
Linux x64 and macOS ARM64 on native runners. It runs on pull requests, pushes to
`main`, version tags (`v0.1.0`), and manual dispatch. Download the matching
`vibe-re-win-x64`, `vibe-re-linux-x64`, or `vibe-re-osx-arm64` artifact from a
successful run. Artifacts expire after 14 days.

The `velopack/` directory contains the new application bundle and updater feed:

- Windows: `GijsRommers.VibeRe-win-x64-stable-Setup.exe`. Run Setup and use the
  **VibeRe** Start menu shortcut. The default per-user install root is
  `%LOCALAPPDATA%\GijsRommers.VibeRe`; its replaceable application is in `current`.
  No .NET installation or administrator permission is needed for this default.
- Linux: `GijsRommers.VibeRe-linux-x64-stable.AppImage`. Save it in a directory you
  own and can write, mark it executable (`chmod +x FILE.AppImage`), then launch
  it. Keep the AppImage at a stable location: updates replace that file. This is
  an AppImage, not a DEB/RPM or a system service. It normally needs FUSE; a system
  that cannot mount AppImages can run it with `APPIMAGE_EXTRACT_AND_RUN=1`.
- macOS Apple silicon: `GijsRommers.VibeRe-osx-arm64-stable-Setup.pkg`, or extract
  `GijsRommers.VibeRe-osx-arm64-stable-Portable.zip` to obtain **VibeRe.app**.
  Install/open the app bundle. Keep the complete bundle in a writable location
  such as `~/Applications` when using the ZIP; updating a system-owned location
  may require macOS authorization. Do not run individual files inside the bundle.

These are unsigned release candidates. Windows may show SmartScreen warnings;
macOS packages are not Developer ID signed or notarized and Gatekeeper may block
opening them. Do not disable OS security protections to run a candidate. Only
use artifacts from a trusted workflow revision; a public production release
requires signing/notarization and platform acceptance testing first. The
workflow never creates a GitHub Release or deploys an update feed, even on tags.

SHA-256 checksums accompany both the legacy archives and the Velopack files.
The `.nupkg` files and `releases.RID-stable.json` are update-feed artifacts, not
installers for users to unpack manually. Keep each RID's feed files together.
The stable app identity is `GijsRommers.VibeRe`; channels are `win-x64-stable`,
`linux-x64-stable`, and `osx-arm64-stable`. Do not rename these in a later release.

## Updating the new app

Open **Updates** in the desktop app, choose **Check for updates**, then
**Download**. After saving your work and closing other local clients, confirm the
saved-work checkbox and choose **Install and restart**. Checking and downloading
do not replace the running app. Updates are applied only after explicit consent;
a downloaded update is not silently applied on startup.

`vibe-re update` opens the same desktop update window from a bundled CLI, without
requiring CLI sign-in first. A standalone CLI/server or old portable installation
has no managed desktop updater and reports how to install the desktop package.
To invoke the bundled CLI directly, use `current/cli/vibe-re.exe` on Windows or
`VibeRe.app/Contents/MacOS/cli/vibe-re` on macOS. For a persistent standalone Linux
CLI, keep using the legacy portable CLI/server archive below. No installer
changes your shell PATH or creates a server service.

The updater checks the repository's [GitHub Releases](https://github.com/GijsRommers/Vibe-Re/releases)
for a newer stable release on the installed architecture's channel. There is no
official Velopack production release/feed published by this change. CI artifacts
alone are not an update source. If the repository is private, the unauthenticated
public updater cannot read its releases: maintainers must first choose an
accessible release/feed policy. No access token is embedded in the application.
Until a compatible accessible stable release exists, check may report an
unavailable source or no compatible update; normal app usage remains available.

The client checks full-package size and SHA-256, package identity, version,
channel and runtime before marking an update ready, and rechecks before apply.
Hashes detect corruption; they do not authenticate a compromised release feed.
Release publication and feed access must remain trusted. Downgrades and
prerelease promotion are not enabled. This is not automatic health-check rollback:
keep known-good artifacts and publish a higher fixed version for recovery.

Before replacement, the app waits for its managed local server to stop and
blocks updates if another local client is using the installation. Remote or
unrelated servers are not terminated. If a failed apply leaves startup blocked,
close all VibeRe processes and finish reinstalling a trusted complete package
(or replacing the entire AppImage/app bundle). Wait until the installer and
updater have exited. A normal interactive Windows reinstall clears the startup
gate when it launches the repaired app. For a manual replacement or silent
reinstall, explicitly complete recovery with the repaired desktop executable:

```powershell
# Windows default installation
& "$env:LOCALAPPDATA\GijsRommers.VibeRe\current\VibeRe.Desktop.exe" --repair-update
```

```sh
# Linux: use your actual writable AppImage path
/path/to/GijsRommers.VibeRe-linux-x64-stable.AppImage --repair-update
# macOS: adjust if the bundle is in ~/Applications instead
/Applications/VibeRe.app/Contents/MacOS/VibeRe.Desktop --repair-update
```

Recovery refuses to clear the gate while another app/server or native updater is
still active. It is only for a completed reinstall/replacement; it does not
repair or download missing files. Do not delete user databases, credentials, or
lifecycle state by hand to force an update. Configuration and data stay outside
the bundle.

## Move from Inno Setup or an old portable copy

This is a one-time migration, not an in-place Inno upgrade:

1. Close desktop, CLI, and every local VibeRe server from the old installation.
   The server can outlive its clients. Back up `~/.vibe-re` before migrating.
2. Install the new Velopack package into its own default directory or bundle.
   Never point it at `%LOCALAPPDATA%\Programs\VibeRe`, and never overlay the
   existing `cli/desktop/server` portable tree.
3. Open the new **VibeRe** app and verify your connections and data. Existing
   `~/.vibe-re/client.db`, `~/.vibe-re/vibe-re.db`, and OS credential-store entries
   are preserved. The package does not copy or erase those stores.
4. Remove the legacy Windows copy using its original Inno uninstaller (the
   entry whose install location is `%LOCALAPPDATA%\Programs\VibeRe`). A portable
   copy can be removed after all of its processes are closed. Remove obsolete
   shortcuts/PATH entries carefully; use the shortcut for the new installation.

The two installers have separate identities and own separate directories. Keep
legacy portable archives and the Inno installer available during migration.
Avoid running both copies against the same local server during migration.

## Legacy portable archives and Windows installer

The top-level `vibe-re-VERSION-RID` ZIP/tarball and Windows
`vibe-re-VERSION-win-x64-setup.exe` remain available. The Inno installer installs
per user in `%LOCALAPPDATA%\Programs\VibeRe`, with **VibeRe** and **VibeRe CLI**
shortcuts, no PATH change and no server service. These legacy formats do not
self-update. Use them for CLI-only deployments or while validating migration.

For portable installs, extract the Windows ZIP or Linux/macOS tarball into a
directory you own. Keep its complete layout:

```text
cli/                  vibe-re[.exe], runtime files, native libraries
  server/             complete bundled local server, including wwwroot/_framework
desktop/              VibeRe.Desktop[.exe], Avalonia and runtime files
  server/             complete bundled local server, including wwwroot/_framework
server/               standalone vibe-re-server[.exe], runtime files and web assets
INSTALL.md
build-info.json       version, runtime identifier and source commit
```

Run `cli/vibe-re --help`, `cli/vibe-re auth status`, or
`desktop/VibeRe.Desktop` (append `.exe` on Windows). Clients automatically start
their bundled local server. A signed-out status returns exit code 1 and offers
login; it does not mean startup failed. No .NET installation is required. Do not
copy only the apphost executable: runtime, SQLite and Avalonia native files are
required. Linux requires a graphical session for desktop and the usual .NET and
Avalonia OS libraries (including X11, fontconfig and libICE/libSM); a working OS
credential store must be available even for signed-out status checks. On Linux
this requires `libsecret-1-0`, a D-Bus session and a Secret Service provider such
as GNOME Keyring; bare SSH/service sessions usually lack these. CI provisions
an isolated session and throwaway keyring, following
[Latchkey's integration workflow](https://github.com/kerem-acer/latchkey/blob/a7a234846a5eeb8968d1ee7ba38db3e8ac1e1574/.github/workflows/ci.yml).
There is no automatic plaintext-store fallback. The legacy macOS tarball contains
raw binaries; use the Velopack `.app`/PKG above for the managed desktop package.
Native credential-store prompts and complete interactive login flows still need
manual platform validation.

## Settings and upgrades

CLI and desktop settings remain in `~/.vibe-re/client.db`; the server database is
`~/.vibe-re/vibe-re.db`. Credential stores and databases are per user and outside
the install directory. The installer leaves them in place on uninstall. Close
desktop, CLI and any running local server before updating or uninstalling.
The server outlives clients. Windows Setup uses Restart Manager to detect files
in use; do not force an update over running binaries. Restart the client after
updating to start the new server. Portable users should stop their server and
replace the complete extracted tree rather than overlaying individual files.

Published servers read `appsettings.json` beside their own executable, regardless
of the caller's current directory. `ASPNETCORE_CONTENTROOT` can point to an
external configuration directory which survives upgrades. Environment variables
and command-line options still override JSON. Do not keep secrets or custom
settings in the packaged directory: upgrades replace those files. For example,
to run a separate server from PowerShell:

```powershell
$env:ASPNETCORE_URLS = 'http://localhost:5068'
$env:ConnectionStrings__VibeRe = 'Data Source=C:\Users\you\.vibe-re\secondary.db'
# Set Server__AccessToken from your secret manager when needed.
& "$env:LOCALAPPDATA\Programs\VibeRe\server\vibe-re-server.exe"
```

The database parent directory must already exist for a custom connection string.
For HTTPS configure Kestrel certificates or a WebSocket-capable reverse proxy.
Remote use requires HTTPS and a paired device or `Server__AccessToken`; see the README. Automatic
local startup inherits the client's environment and passes the selected connection
address as `--urls`, which takes precedence over `ASPNETCORE_URLS`. Configure the
connection address in the client for another port. `VIBE_RE_SERVER_EXECUTABLE`
selects an alternative server apphost. `VIBE_RE_CLIENT_DATABASE` isolates client
settings. Local instances requiring separate auth data need separate server
connection strings. Installing an upgrade does not migrate server connections
or overwrite user databases.

## Build locally

Install a stable .NET 10 SDK (`global.json` allows newer 10.0 feature bands) and
PowerShell 7.4 or later for Velopack packaging and smoke tests.
The older portable/Inno scripts also support Windows PowerShell 5.1. The shared `vibe-re.desktop.slnf` solution filter builds CLI, server, Avalonia,
shared libraries and all non-mobile test projects (including CLI tests) without loading MAUI:

```powershell
./build/Test.ps1
./build/Package.ps1 -Runtime win-x64 -Version 0.1.0-dev
$compiler = ./build/Get-InnoSetup.ps1
./build/Package.ps1 -Runtime win-x64 -Version 0.1.0-dev -Velopack -WindowsInstaller -IsccPath $compiler
dotnet run --project build/SmokeTests -c Release -- --root artifacts/stage/win-x64
./build/Test-Installer.ps1 # use a clean Windows account with no installed VibeRe
./build/Test-Velopack.ps1 -Runtime win-x64 # isolated install/apply/restart fixture
```

`Get-InnoSetup.ps1` downloads Inno Setup 6.7.3 from the publisher's GitHub release,
verifies a pinned SHA-256 digest, and installs a private compiler under ignored
`artifacts/tools` in the publisher's portable mode, without file associations or
uninstall registration. It does not replace a machine-wide compiler. Alternatively,
pass your own Inno Setup 6.3+ `ISCC.exe` with `-IsccPath`. Review Inno Setup's
license terms for your use.

Add `-Velopack` on native Windows x64, Linux x64 or macOS ARM64 to produce both
legacy and new artifacts. The local `.config/dotnet-tools.json` pins `vpk` to
1.2.161, matching the application SDK. Linux packaging additionally requires
`squashfs-tools` (`mksquashfs` on PATH); macOS requires Apple's command-line tools
for bundling and PKG creation. No signing identities are required for CI candidates.
Use `-Runtime linux-x64`, `osx-arm64`, or `osx-x64` for portable archives. Publish
on the target OS/architecture for startup validation. The CI matrix validates
Windows x64, Ubuntu x64 and macOS ARM64; Intel macOS publishing is available
locally but has no CI runner here. Build/intermediate directories are isolated
by project and runtime under `artifacts/publish-build`; staged apps are separate
under `artifacts/stage/RID/{cli,desktop,server}`. The Velopack input is separate at
`artifacts/velopack-stage/RID`: complete Desktop publish at the root, its complete
`server/` subtree, and complete `cli/` with its own `cli/server/`. Nothing is
flattened across publish outputs. macOS places this tree in `Contents/MacOS`;
Linux places it in the mounted AppImage's `usr/bin`; Windows uses `current/`.
Packaging replaces only the selected RID's staging/packages directories. Avoid concurrent packaging for the
same RID. Publishing a client directly also bundles its server by default;
`-p:BundleLocalServer=false` explicitly disables this for advanced deployments.
The Makefile's `install` target remains a CLI/server-only Unix source installer.

CI runs real published apphosts from relocated paths containing spaces and an
unrelated working directory. It tests CLI and desktop automatic server launch,
standalone server launch, HTTP/1.1 readiness with SignalR protocol v2, file configuration,
external configuration roots, environment overrides, signed-out SignalR hub calls
and token rejection without contacting Mistral. Each complete server package must
serve the Blazor entry point, JavaScript, CSS, and compiled WebAssembly assets
from an unrelated working directory, even with an external configuration root. Linux
desktop startup uses Xvfb plus a D-Bus session and GNOME Keyring. Windows also installs, reinstalls and uninstalls the
generated EXE, checking the installed files and per-user registration. This is
startup validation, not a full interactive UI or real-provider login test.

Velopack validation checks the generated native package's identity/RID/channel,
feed hashes and complete runtime, server and web-asset payload. A separate
uniquely named disposable fixture is packaged as 1.0.0 and 1.0.1 on each native
runner. It exercises native installation (Windows Setup; Linux AppImage copy;
macOS app ZIP), local-feed check/download, real updater apply and restart, and
verifies the installed version changed. It uses no real user account, provider,
VibeRe data or production release. macOS PKG installation through Installer,
Gatekeeper/SmartScreen UX, and signed/notarized distribution still need release
acceptance testing. Unit/integration tests also cover corrupt downloads, cached
package tampering, channel/runtime rejection, no-release/error handling and
explicit apply consent.

## MAUI mobile

MAUI is excluded from desktop CI and all release artifacts. Android and iOS need
MAUI workloads, platform SDKs, and device/emulator validation; iOS distribution
also needs a Mac, Apple provisioning and signing. The mobile toolchain follow-up
in PR #5 validated a local Android Debug APK with MAUI 10.0.20 and Android API 36.
That debug build is separate from this release pipeline: no APK, AAB or IPA is
produced here, and device execution or an iOS native package is not claimed.
On Windows/Linux the mobile project defaults to Android; macOS also includes
iOS, which can be explicitly included with `-p:EnableIosTarget=true`.
The current template app identifier/version still needs a distribution decision.
The shared client library is covered by desktop CI. Mobile release automation
should be added separately once the platform toolchains and signing identities
are configured. No signing credentials are required by this workflow.

## References

- [.NET artifacts output layout](https://learn.microsoft.com/en-us/dotnet/core/sdk/artifacts-output)
- [.NET 10 supported operating systems](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)
- [ASP.NET Core configuration](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/?view=aspnetcore-10.0)
- [GitHub Actions artifact storage](https://docs.github.com/en/actions/how-tos/writing-workflows/choosing-what-your-workflow-does/storing-and-sharing-data-from-a-workflow)
- [Inno Setup non-administrative installs](https://jrsoftware.org/ishelp/topic_setup_privilegesrequired.htm)
- [Inno Setup downloads and licensing](https://jrsoftware.org/isdl.php)

- [Velopack packaging and native output formats](https://docs.velopack.io/packaging/overview)
- [Velopack Linux/AppImage support](https://docs.velopack.io/packaging/operating-systems/linux)
- [Velopack macOS bundles and installers](https://docs.velopack.io/packaging/operating-systems/macos)
- [Velopack signing](https://docs.velopack.io/packaging/signing)
- [Velopack channels](https://docs.velopack.io/packaging/channels)
