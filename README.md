# VibeRe

VibeRe runs provider authentication on the server. CLI, Avalonia desktop, MAUI mobile, and Blazor WebAssembly use the same SignalR API and shared JSON DTOs in `VibeRe.Contracts`. `VibeRe.Client.Application` shares startup, settings policy, and presentation state; `VibeRe.Client.Infrastructure` implements native storage and platform adapters; `VibeRe.Client.SignalR` supplies the shared browser/native transport. Clients can save multiple named servers and choose an active server. Each server owns its Mistral credentials; logging in from one client is visible to other clients connected to that server.

## Run locally

For installers, portable downloads, CI and local packaging commands, see
[Build and install VibeRe](docs/installation.md). Windows gets a per-user installer;
Linux and macOS get self-contained archives. Mobile packaging is tracked separately.

Launch either client. No setup step or separate server command is needed:

```sh
dotnet run --project VibeRe.Cli
dotnet run --project VibeRe.Desktop
```

The CLI and desktop create and select a `local` connection at `http://localhost:5067`, with automatic local startup enabled. They probe the server's HTTP readiness endpoint, reuse a running VibeRe server, or start it and wait for readiness. A shared launch lock avoids duplicate startup across clients. An unrelated process on the port produces a connection error. Use **Refresh connection** to retry after a server stops or startup is canceled.

This follows [T3's local/remote connection flow](https://github.com/pingdotgg/t3code/blob/main/docs/user/remote-access.md): local is available by default, connection settings belong to the client, and remote-only use does not depend on a local server. Select a remote connection to use it directly. Turn off **Start this server locally when needed** to manage a local server yourself; this changes future startup behavior and leaves an already running server running.

Published CLI and desktop clients include their server runtime in a `server` subdirectory. In source checkouts, clients start it with `dotnet run`. They can also find `vibe-re-server` beside the client or on `PATH`; `VIBE_RE_SERVER_EXECUTABLE` overrides the executable. The local server runs independently of the client. On macOS/Linux, its startup output goes to `~/.vibe-re/server.log` (`VIBE_RE_SERVER_LOG` can override this). For foreground development, use `dotnet run --project VibeRe.Server`.

When the selected server is signed out, desktop and mobile show a square Mistral **Login** button on a near-black screen. Server settings and API-key entry stay collapsed until needed. Launching the CLI without a command prints **Not logged in**, starts login, prints and opens the Mistral verification URL, then waits for the server to complete the flow. The GUI button and CLI use the same `IVibeReClient.LoginWithBrowserAsync` flow. An invalid stored key also offers login; provider outages and billing requirements show separate messages. The explicit `auth status` command reports status without opening a browser, and scripted secret input uses the stdin options below.

## Mistral login

```sh
# Browser login (default)
dotnet run --project VibeRe.Cli -- auth login

# Masked API-key prompt, or import MISTRAL_API_KEY when explicitly selecting this method
dotnet run --project VibeRe.Cli -- auth login --method api-key

# Non-interactive input: pipe a secret from your secret manager, never put it in arguments
your-secret-manager-command | dotnet run --project VibeRe.Cli -- auth login --api-key-stdin

dotnet run --project VibeRe.Cli -- auth status
dotnet run --project VibeRe.Cli -- auth logout
```

Desktop and mobile expose these same actions. Browser login opens Mistral on the client and displays a URL that can also be opened manually on another device. The server creates the PKCE challenge, polls Mistral, and exchanges the result for a key. The verifier and returned API key stay on the server. No local callback port or SSH tunnel is needed.

Both login methods converge on `IAuthService.LoginAsync`: validate the credential, save it in the server's secret store, and persist a credential reference in SQLite. A rejected or unverifiable key does not replace a working key. A `402` billing response is retained as `SubscriptionRequired`, rather than prompting for repeated login. Status rechecks the provider; it is not merely a check for a stored value.

Browser attempts expire, can be canceled, and are lost when the server restarts. SignalR streams their status from the server. A disconnected client can watch the same attempt again; disconnecting stops observation without canceling an in-flight provider exchange. Native clients reconnect and resume that observation without replaying API-key login or creating another attempt. Explicit cancellation asks the server to cancel, with a bounded two-second client cleanup wait. A lost connection may leave a pending attempt until its expiry; refresh server status before retrying a request whose result was interrupted. Logging out removes the stored key and cancels pending browser attempts on that server. Removing a client connection only removes that client's connection settings and access token.

Mistral browser sign-in follows the [official Vibe implementation](https://github.com/mistralai/mistral-vibe/blob/main/vibe/setup/auth/http_browser_sign_in_gateway.py), including server-side PKCE exchange. Credential validation checks the [Mistral models endpoint](https://docs.mistral.ai/api/endpoint/models), with Mistral account lookup for browser-issued credentials that the main API rejects. Browser endpoints are provider-specific and can change.

## Multiple servers

```sh
dotnet run --project VibeRe.Cli -- servers add work https://vibe.example.com
dotnet run --project VibeRe.Cli -- servers pair work "Work laptop"
dotnet run --project VibeRe.Cli -- servers use work
dotnet run --project VibeRe.Cli -- servers list

# Skip server startup and authentication checks on launch (default: on)
dotnet run --project VibeRe.Cli -- servers required off

# Opt out of local process startup, or manage another local server
dotnet run --project VibeRe.Cli -- servers local local off
dotnet run --project VibeRe.Cli -- servers add secondary http://localhost:5068 --local

# Override the active server for just this command
dotnet run --project VibeRe.Cli -- --server local auth login

dotnet run --project VibeRe.Cli -- servers update work https://new-vibe.example.com
dotnet run --project VibeRe.Cli -- servers remove work
```

`servers token NAME` prompts without echo. Pass `--token-stdin` for scripted input, or submit an empty value to clear a saved token. Changing a server address clears its token so credentials cannot follow a connection to another destination.

Desktop and mobile provide a server selector, add/remove actions, editable server address, and access-token settings. CLI and desktop share their own SQLite database at `~/.vibe-re/client.db`; `VIBE_RE_CLIENT_DATABASE` can select another database. Mobile has its own `client.db` in its app-data directory. The client database is independent of the server database and works while the local server is stopped. It is created automatically with the local server selected; there is no setup prompt. SQLite transactions serialize configuration updates across clients; malformed databases are reported without being overwritten.

The client database contains names, addresses, IDs, token-presence flags, the active server selection, and the **ServerRequired** preference (default `true`). Server access tokens live in the OS credential store for CLI/desktop and MAUI `SecureStorage` on mobile. Mistral API keys are stored only on the server through `ISecretStore`/Latchkey. The server database contains credential references, never the keys themselves. The server database defaults to `~/.vibe-re/vibe-re.db`; `ConnectionStrings__VibeRe` overrides its connection string. Separate server instances need separate database paths to have independent provider logins.

`VIBE_RE_SERVER_TOKEN` overrides the selected connection's token for a CLI invocation. `MISTRAL_API_KEY` is imported only during explicit CLI API-key login; client environment variables do not silently override server credentials.

## Web, LAN, and mobile connections

Run `VibeRe.Server`, then open its address in a browser. The host serves `VibeRe.Web`, a Blazor WebAssembly client, at `/`; there is no server-side UI circuit. It uses the same application login flow and SignalR transport as the native clients. Published server output includes the web assets. Source startup also serves them in Production, including when managed clients launch without a development profile.

The browser uses the server that served the page. **Open another server** navigates to the other server's root instead of sending credentials cross-origin. Browsers cannot launch local processes or use the native keyring. Local CLI/desktop startup behavior is unchanged; opening a web page requires its server to be running.

For another PC or phone, use a reachable HTTPS address with a trusted certificate. Direct Kestrel HTTPS can be configured with `ASPNETCORE_URLS` and a certificate. Plain HTTP is accepted for privileged actions only when both the socket peer and Host are loopback. Native clients verify certificates and never follow connection or pairing redirects to another server.

Pair each device rather than sharing a server administrator token:

1. Open the server's HTTPS URL in the remote browser and choose **Get pairing code**, or add that URL in the native client's **Manage servers** and choose **Pair this device**. CLI: `servers pair NAME "My laptop"`.
2. On the server computer, open its direct `http://localhost:5067` page, expand **Manage devices**, and approve the displayed code.
3. The waiting device finishes pairing automatically. Use the local web **Manage devices** section to list devices and revoke access.

Pairing codes expire after five minutes and can be redeemed once using the requesting device's separate secret. Approval is available only through a direct loopback connection. Each approved device gets a separate 30-day session, stored as a hash on the server. Revocation and expiry invalidate further requests and terminate active connections. Pairing grants the current provider-auth actions, including login/logout, but no device-management authority. Use **Disconnect this browser** to revoke its device session; provider **Logout** removes the selected server's Mistral login for every connected client.

The browser receives a host-only, HttpOnly, SameSite=Strict cookie, Secure over HTTPS; its durable token is never returned to JavaScript or placed in a URL/localStorage. HTTPS cookies use the `__Host-` prefix and path `/`. Cookies are host-scoped, not port-scoped: use a dedicated hostname and trust every service on that hostname. Native device tokens go to the existing per-server OS credential store. Mistral credentials stay in the server's secret store. Explicit API-key entry travels to that server for validation and storage; clients do not retrieve stored provider keys.

`Server__AccessToken` remains an optional administrator credential for existing native deployments. When configured, it is required even for initial local access; enter it through the native client's masked token prompt, or the local web connection prompt. It must not be put in URLs. Remote administrator tokens still cannot approve devices; approval remains local-only.

Browser requests, including WebSocket upgrades, must be same-origin. Every state-changing pairing endpoint also requires an antiforgery token. There is no cross-origin credential sharing or permissive CORS. The native redemption endpoint rejects browser Origin headers; browser redemption only sets the HttpOnly cookie. Pairing start/approval/redemption are bounded and rate-limited. Query-string access tokens are rejected for every transport, including WebSocket and SSE. Avoid credential-bearing request/body logs in any proxy.

The versioned hub is `/hubs/v1/auth`; public readiness at `/.well-known/vibe-re/server` identifies protocol version 2 and transport `signalr`. Upgrade clients and servers together. The hub exposes `Login(apiKey)`, `GetStatus()`, `Logout()`, `StartBrowserLogin()`, `WatchBrowserLogin(attemptId)` (server stream), and `CancelBrowserLogin(attemptId)`. DTOs contain status, an opaque attempt ID/expiry, and a verification URL, never stored provider credentials or PKCE secrets. Disconnected clients can resume the same login attempt. Native and browser clients require WebSockets. Long polling is exercised separately by integration tests.

On a physical phone, `localhost` refers to the phone. Use the server's reachable HTTPS address and pair that device. Connection lists remain local to each native device; they are not synchronized.

### Tailscale Serve

A trusted HTTPS Tailscale address works with the same server selection and device pairing. No Tailscale SDK is required in VibeRe. For an already configured tailnet, [Tailscale Serve](https://tailscale.com/docs/reference/tailscale-cli/serve) can terminate HTTPS and forward to the loopback server. Configure the exact public origin on VibeRe and keep its backend loopback-only, for example:

```sh
# Replace this example with your machine's actual HTTPS tailnet origin.
Server__TailscaleOrigin=https://my-pc.example-tailnet.ts.net \
  ASPNETCORE_URLS=http://127.0.0.1:5067 dotnet run --project VibeRe.Server --no-launch-profile

# Run separately on that computer after reviewing your existing Tailscale setup.
tailscale serve --bg http://127.0.0.1:5067
```

Use the root mount and TCP loopback backend shown above, not a subpath or Unix socket. Open the resulting HTTPS URL in the other device's browser, or save it as a native server connection. Approve pairing through direct `http://localhost:5067` on the server computer. The HTTPS URL is remote access even when a request arrives through a local proxy; it cannot inherit local administrator privileges.

`Server:TailscaleOrigin` is opt-in and accepts one HTTPS `*.ts.net` origin. VibeRe requires all configured backend listeners to be loopback, accepts Serve headers only from a loopback socket, checks both Host values and forwarded HTTPS, and uses the configured origin for cookie security and exact-Origin checks. It does not trust Tailscale identity headers or forwarded IP addresses as authorization. Unknown/unconfigured forwarded requests and Funnel-marked requests are rejected. Local processes are in this trust boundary; do not run an untrusted local proxy or expose the backend directly. Tailscale access rules control network reachability, while VibeRe pairing controls application access.

Serve support is intentionally limited to this explicit configuration. It does not install Tailscale, sign into a tailnet, change firewall rules, enable Funnel, or provision a cloud service. Use direct HTTPS for other reverse-proxy deployments unless an equally narrow trusted adapter is provided. Actual tailnet connectivity, certificate provisioning, and the installed Serve version's WebSocket behavior must be verified on the user's machines.

## Validation

For CLI, desktop, and server development, open `vibe-re.desktop.slnf` in your IDE or run `dotnet build vibe-re.desktop.slnf`. It loads the existing solution without the mobile project, so mobile workloads are optional for this workflow.

```sh
dotnet build VibeRe.Server
dotnet build VibeRe.Cli
dotnet build VibeRe.Desktop
dotnet test VibeRe.Server.Application.Tests
dotnet test VibeRe.Server.Infrastructure.Tests
dotnet test VibeRe.Client.Tests
dotnet test VibeRe.Cli.Tests
dotnet test VibeRe.Server.Tests
dotnet test VibeRe.Web.Tests
```

The tests use fake Mistral HTTP responses, isolated SQLite databases, and fake secret stores. They cover both login methods, PKCE, expiry/cancellation, billing/rejected-key behavior, credential rollback, server access control, multiple-client visibility, independent servers, restart persistence, and concurrent client configuration updates. They do not sign in to a real Mistral account.

CLI and GUI share `ClientStartup`: read **ServerRequired**, connect to the selected server (starting only an opted-in local profile when needed), wait for readiness, then check its authentication. Turning off **Require a server at startup** in **Manage servers**, or running `servers required off`, skips automatic connection and authentication. Explicit login/status/refresh still connects to the selected server, without starting a local process. Existing client databases migrate with this preference enabled.

CLI, desktop, and mobile compose the client through `AddVibeReClient`. Configuration storage, token storage, server connections, local process startup, readiness checks, and browser opening have explicit interfaces. The connection manager edits saved settings; the connector opens destination-scoped clients; the startup coordinator returns either an explicit optional-server result or a session with a required authentication status; CLI parsing, auth commands, and server commands live in separate classes. Provider credential persistence and rollback are separate from authentication policy. Changing a server address or its access token rotates the profile's credential identifier while preserving its name and selection. Every token save is bound to the originally displayed or prompted profile ID and origin, so concurrent edits cannot send a token to a different destination. Replacement tokens are written under a fresh identifier; old secrets are retired only after the SQLite settings commit. Failed writes clean up only an unreferenced new secret and preserve the prior credential. SQLite and the OS keyring do not form a crash-atomic transaction: a process crash, unavailable keyring, or failed cleanup can leave an unreferenced keyring entry, but cleanup never replaces the original error or makes committed settings appear rolled back.

Mobile development requires the .NET 10 SDK and matching MAUI workload. Install it with `dotnet workload install maui`, then reload the solution in the IDE. `dotnet workload list` should list `maui`. A missing platform workload can also produce `NETSDK1135` during IDE project loading because the target platform version cannot be inferred; install the workload rather than lowering the app's minimum supported OS version.

The mobile project targets Android on Windows/Linux and adds iOS on macOS. Build Android with `dotnet build VibeRe.Mobile -f net10.0-android`; the Android SDK and Java SDK must also be installed. Build iOS on a Mac with a compatible Xcode using `dotnet build VibeRe.Mobile -f net10.0-ios`. For a configured Mac build host from Windows, explicitly opt in with `-p:EnableIosTarget=true`. The shared mobile/desktop connection logic is exercised by client and server tests; native packaging requires its platform toolchain.

## Configuration and settings

The projects separate application policy from IO on each side:

- `VibeRe.Server.Application`: authentication use cases and the repository/secret-store contracts they need.
- `VibeRe.Server.Infrastructure`: model-provider adapters, persistence, and credential-store implementations. The `VibeRe.Server` host composes these and exposes the SignalR hub.
- `VibeRe.Client.Application`: startup/login presentation state, settings policy, transport-independent authentication results, and small IO contracts. It has no database, process, filesystem, SignalR, or infrastructure dependency.
- `VibeRe.Client.Infrastructure`: native SQLite settings, keyring, browser/process/readiness adapters, and device-pairing HTTP adapter. It depends on Client.Application, never the reverse. The former Client and Runtime projects are replaced by these two layers. The former Protocol project is replaced by `VibeRe.Contracts`, a dependency-free set of versioned wire DTOs and endpoint constants shared only by the transport adapters.

`VibeRe.Client.SignalR` is the small shared transport assembly: the authentication adapter, with no filesystem, keyring, process, or browser-window code. `VibeRe.Web` composes it with browser-safe same-origin access and navigation, plus generated JSON metadata for trimmed WebAssembly publishing. It never references native Client.Infrastructure.

CLI and desktop reuse the infrastructure adapters. Mobile supplies its own browser/secure-storage implementations and app-data path at the host's `AddVibeReClient` call. Transport errors and cancellation are translated at the infrastructure boundary, so shared application and presentation code never handles transport-specific exceptions. Provider credentials remain server-owned; existing databases migrate automatically, including the separate hashed device-session table. ACP is not implemented by this change.

Inject `VibeRe.Client.Application.Configuration.IClientConfigurationStore` to read a snapshot:

```csharp
var configuration = await configurations.LoadAsync(cancellationToken);
var selectedServer = configuration.Find();
```

Use `IServerConnectionManager` to edit connections, tokens, or startup preferences, and `IServerConnector` to connect. This preserves validation, destination-scoped credentials, and concurrent access between CLI and desktop. Configuration is created on first use and can be read while every server is offline.

VibeRe has one Mistral login screen, with a **Server** picker above it. Use **Manage servers** to add or edit a server, then choose it from the picker. The selection is remembered locally and shared by CLI and desktop. Switching servers refreshes that server's Mistral status; login and logout always act on the selected server. An offline server stays selected and never silently falls back to another server.

This follows [T3's settings ownership](https://github.com/pingdotgg/t3code/blob/main/docs/user/project-settings.md): the connection choice belongs to the device, and authentication belongs to the chosen server. Separate provider-management pages are unnecessary for the single-provider app.

Keep API keys and access tokens in their existing secret stores. Client UI code does not open the server's SQLite database, and configuration replies must not return stored secrets. SQLite fits the existing shared CLI/desktop store; adding a second writable JSON file would introduce competing sources of configuration.
