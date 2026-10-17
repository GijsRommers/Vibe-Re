# Build and install VibeRe

## Download and run

The **Build, test and package** GitHub Actions workflow builds on pull requests,
pushes to `main`, version tags (`v0.1.0`), and manual dispatch. Download the
`vibe-re-win-x64`, `vibe-re-linux-x64`, or `vibe-re-osx-arm64` artifact from a
successful run. Artifacts expire after 14 days. They contain versioned archives
and SHA-256 checksums; Windows also includes an installer. These are unsigned
release candidates. The workflow never creates or publishes a GitHub Release.
Publishing a release and signing binaries require a separate maintainer decision.

On Windows x64 (Windows 10 build 17763 or later), run
`vibe-re-VERSION-win-x64-setup.exe`. This installs for the
current user in `%LOCALAPPDATA%\Programs\VibeRe`, without an administrator prompt.
Use the Start menu's **VibeRe** shortcut for Avalonia desktop or **VibeRe CLI**
for a command prompt in the CLI directory. To use the commands in any shell,
add the installation's `cli` and `server` directories to your user `PATH`, or
invoke their full paths. The installer does not change `PATH` or install a service.
An optional desktop shortcut is available. Windows may warn about the unsigned
installer. Only run artifacts from a trusted workflow revision.

For portable installs, extract the Windows ZIP or Linux/macOS tarball into a
directory you own. Keep its complete layout:

```text
cli/                  vibe-re[.exe], runtime files, native libraries
  server/             complete bundled local server
desktop/              VibeRe.Desktop[.exe], Avalonia and runtime files
  server/             complete bundled local server
server/               standalone vibe-re-server[.exe] and runtime files
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
There is no automatic plaintext-store fallback. macOS artifacts are raw
binaries, not `.app`/DMG bundles, and are neither signed nor notarized. Their
Gatekeeper behavior and native credential-store prompts need manual validation.
There are no native Linux packages or automatic updater in this pipeline.

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
For HTTPS configure Kestrel certificates or an HTTP/2-capable reverse proxy.
Remote use requires HTTPS and `Server__AccessToken`; see the README. Automatic
local startup inherits the client's environment and passes the selected connection
address as `--urls`, which takes precedence over `ASPNETCORE_URLS`. Configure the
connection address in the client for another port. `VIBE_RE_SERVER_EXECUTABLE`
selects an alternative server apphost. `VIBE_RE_CLIENT_DATABASE` isolates client
settings. Local instances requiring separate auth data need separate server
connection strings. Installing an upgrade does not migrate server connections
or overwrite user databases.

## Build locally

Install a stable .NET 10 SDK (`global.json` allows newer 10.0 feature bands) and
PowerShell 7 for the cross-platform scripts. Windows PowerShell 5.1 also works
for these scripts. The shared `vibe-re.desktop.slnf` solution filter builds CLI, server, Avalonia,
shared libraries and all non-mobile test projects (including CLI tests) without loading MAUI:

```powershell
./build/Test.ps1
./build/Package.ps1 -Runtime win-x64 -Version 0.1.0-dev
$compiler = ./build/Get-InnoSetup.ps1
./build/Package.ps1 -Runtime win-x64 -Version 0.1.0-dev -WindowsInstaller -IsccPath $compiler
dotnet run --project build/SmokeTests -c Release -- --root artifacts/stage/win-x64
./build/Test-Installer.ps1 # use a clean Windows account with no installed VibeRe
```

`Get-InnoSetup.ps1` downloads Inno Setup 6.7.3 from the publisher's GitHub release,
verifies a pinned SHA-256 digest, and installs a private compiler under ignored
`artifacts/tools` in the publisher's portable mode, without file associations or
uninstall registration. It does not replace a machine-wide compiler. Alternatively,
pass your own Inno Setup 6.3+ `ISCC.exe` with `-IsccPath`. Review Inno Setup's
license terms for your use.

Use `-Runtime linux-x64`, `osx-arm64`, or `osx-x64` for portable archives. Publish
on the target OS/architecture for startup validation. The CI matrix validates
Windows x64, Ubuntu x64 and macOS ARM64; Intel macOS publishing is available
locally but has no CI runner here. Build/intermediate directories are isolated
by project and runtime under `artifacts/publish-build`; staged apps are separate
under `artifacts/stage/RID/{cli,desktop,server}`. Packaging replaces only the
selected RID's staging/packages directories. Avoid concurrent packaging for the
same RID. Publishing a client directly also bundles its server by default;
`-p:BundleLocalServer=false` explicitly disables this for advanced deployments.
The Makefile's `install` target remains a CLI/server-only Unix source installer.

CI runs real published apphosts from relocated paths containing spaces and an
unrelated working directory. It tests CLI and desktop automatic server launch,
standalone server launch, HTTP/2 readiness, file configuration, environment
overrides, signed-out gRPC and token rejection without contacting Mistral. Linux
desktop startup uses Xvfb plus a D-Bus session and GNOME Keyring. Windows also installs, reinstalls and uninstalls the
generated EXE, checking the installed files and per-user registration. This is
startup validation, not a full interactive UI or real-provider login test.

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
