# VibeRe

VibeRe runs provider authentication on the server. CLI, Avalonia desktop, and MAUI mobile use the same gRPC contract and `VibeRe.Client` library. Clients can save multiple named servers and choose an active server. Each server owns its Mistral credentials; logging in from one client is visible to other clients connected to that server.

## Run locally

Launch either client. No setup step or separate server command is needed:

```sh
dotnet run --project VibeRe.Cli -- auth status
dotnet run --project VibeRe.Desktop
```

The CLI and desktop create and select a `local` connection at `http://localhost:5067`, with automatic local startup enabled. They probe the server's HTTP/2 readiness endpoint, reuse a running VibeRe server, or start it and wait for readiness. A shared launch lock avoids duplicate startup across clients. An unrelated process on the port produces a connection error. Desktop also checks the selected managed local server periodically and restarts it after an unexpected stop.

This follows [T3's local/remote connection flow](https://github.com/pingdotgg/t3code/blob/main/docs/user/remote-access.md): local is available by default, connection settings belong to the client, and remote-only use does not depend on a local server. Select a remote connection to use it directly. Turn off **Start this server locally when needed** to manage a local server yourself; this changes future startup behavior and leaves an already running server running.

Published CLI and desktop clients include their server runtime in a `server` subdirectory. In source checkouts, clients start it with `dotnet run`. They can also find `vibe-re-server` beside the client or on `PATH`; `VIBE_RE_SERVER_EXECUTABLE` overrides the executable. The local server runs independently of the client. On macOS/Linux, its startup output goes to `~/.vibe-re/server.log` (`VIBE_RE_SERVER_LOG` can override this). For foreground development, use `dotnet run --project VibeRe.Server`.

When the selected server is signed out, desktop and mobile show a login invitation with browser and API-key choices. The CLI shows the login command and offers the same choices when run interactively. Redirected input never triggers an interactive prompt. An invalid stored key also offers login; provider outages and billing requirements show separate messages.

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

Browser attempts expire, can be canceled, and are lost when the server restarts. Logging out removes the stored key and cancels pending browser attempts on that server. Removing a client connection only removes that client's connection settings and access token.

Mistral browser sign-in follows the [official Vibe implementation](https://github.com/mistralai/mistral-vibe/blob/main/vibe/setup/auth/http_browser_sign_in_gateway.py), including server-side PKCE exchange. Credential validation checks the [Mistral models endpoint](https://docs.mistral.ai/api/endpoint/models), with Mistral account lookup for browser-issued credentials that the main API rejects. Browser endpoints are provider-specific and can change.

## Multiple servers

```sh
dotnet run --project VibeRe.Cli -- servers add work https://vibe.example.com
dotnet run --project VibeRe.Cli -- servers token work
dotnet run --project VibeRe.Cli -- servers use work
dotnet run --project VibeRe.Cli -- servers list

# Opt out of automatic startup, or manage another local server
dotnet run --project VibeRe.Cli -- servers local local off
dotnet run --project VibeRe.Cli -- servers add secondary http://localhost:5068 --local

# Override the active server for just this command
dotnet run --project VibeRe.Cli -- --server local auth login

dotnet run --project VibeRe.Cli -- servers update work https://new-vibe.example.com
dotnet run --project VibeRe.Cli -- servers remove work
```

`servers token NAME` prompts without echo. Pass `--token-stdin` for scripted input, or submit an empty value to clear a saved token. Changing a server address clears its token so credentials cannot follow a connection to another destination.

Desktop and mobile provide a server selector, add/remove actions, editable server address, and access-token settings. CLI and desktop share their own SQLite database at `~/.vibe-re/client.db`; `VIBE_RE_CLIENT_DATABASE` can select another database. Mobile has its own `client.db` in its app-data directory. The client database is independent of the server database and works while the local server is stopped. It is created automatically with the local server selected; there is no setup prompt. SQLite transactions serialize configuration updates across clients; malformed databases are reported without being overwritten.

The client database contains names, addresses, IDs, token-presence flags, and the active server selection. Server access tokens live in the OS credential store for CLI/desktop and MAUI `SecureStorage` on mobile. Mistral API keys are stored only on the server through `ISecretStore`/Latchkey. The server database contains credential references, never the keys themselves. The server database defaults to `~/.vibe-re/vibe-re.db`; `ConnectionStrings__VibeRe` overrides its connection string. Separate server instances need separate database paths to have independent provider logins.

`VIBE_RE_SERVER_TOKEN` overrides the selected connection's token for a CLI invocation. `MISTRAL_API_KEY` is imported only during explicit CLI API-key login; client environment variables do not silently override server credentials.

## Remote and mobile connections

Remote connections require HTTPS and a server access token. Configure `Server__AccessToken` on the server and enter the same token in each client. This token grants access to the server's authentication actions; it is separate from the Mistral API key. If it is configured, local clients must supply it too. Without it, the server accepts only loopback clients.

Bind the server to an HTTPS endpoint with `ASPNETCORE_URLS` and configure a trusted certificate through Kestrel or a reverse proxy that supports gRPC/HTTP/2. Clients verify certificates and do not follow redirects. Provider browser polling URLs are restricted to Mistral's HTTPS console origin, and HTTP client logging for provider authentication is disabled to keep polling tokens out of logs.

On a physical phone, `localhost` refers to the phone. Add your server's reachable HTTPS address in the mobile UI and configure its access token. Connection lists are local to each device; they are not synchronized between devices.

## Validation

```sh
dotnet build VibeRe.Server
dotnet build VibeRe.Cli
dotnet build VibeRe.Desktop
dotnet test VibeRe.Application.Tests
dotnet test VibeRe.Infrastructure.Tests
dotnet test VibeRe.Client.Tests
dotnet test VibeRe.Server.Tests
```

The tests use fake Mistral HTTP responses, isolated SQLite databases, and fake secret stores. They cover both login methods, PKCE, expiry/cancellation, billing/rejected-key behavior, credential rollback, server access control, multiple-client visibility, independent servers, restart persistence, and concurrent client configuration updates. They do not sign in to a real Mistral account.

Mobile builds and the full solution require the .NET MAUI workloads plus the Android/iOS platform SDKs. Install the workloads in your mobile development environment, then build `VibeRe.Mobile` for `net10.0-android` or `net10.0-ios`. The shared mobile/desktop connection logic is exercised by the client and server tests; native mobile packaging still needs platform validation.
