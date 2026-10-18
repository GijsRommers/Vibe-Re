

## Server-owned provider authentication

The server owns Mistral provider credentials. Its application layer contains the
login/status/logout use cases and bounded browser-login lifecycle; Mistral HTTP
requests and secret storage stay in server infrastructure. The gRPC host exposes
status, API-key login, browser login observation/cancellation, and logout.

The default listener is loopback. Remote authentication requires HTTPS and an
explicit server access token; provider keys are never returned to clients.
Public readiness at `/.well-known/vibe-re/server` contains no credentials.

Build the non-mobile projects with `dotnet build vibe-re.desktop.slnf`. The
application/provider tests use fake HTTP and secret stores, with no real account
grants. Client UX and client-backed integration tests follow in the next PR.
