# MCP servers

VibeRe can use tools from [Model Context Protocol](https://modelcontextprotocol.io) servers. Each tool of a
server named `github` shows up as `mcp__github__{tool}`, next to the built-in tools, and follows the same
approvals (see tools.md).

## Server types

- **stdio**: VibeRe starts the server as a child process from a command, arguments and environment variables,
  for example `npx -y @modelcontextprotocol/server-github`.
- **http**: VibeRe connects to a URL over Streamable HTTP, falling back to SSE, optionally with extra headers
  such as `Authorization`.

Server names use lowercase letters, digits, `-` and `_` (at most 32 characters, no `__`).

## Where servers are stored

Servers are stored in VibeRe's SQLite database in the data directory (`~/.vibe-re` by default). Commands for
adding, listing and removing servers from the CLI are not available yet.

Environment variables and headers are currently stored as plain text. Avoid putting long-lived secrets there
until secret references are supported.

## Behavior

- VibeRe keeps one connection per enabled server and reconnects when its configuration changes.
- Disabling or removing a server disconnects it.
- If a server fails to start or answer, its tools are skipped and the server log explains why. Other tools
  keep working.
- Only text results are passed to the model for now. Images and resources are replaced by a placeholder.
