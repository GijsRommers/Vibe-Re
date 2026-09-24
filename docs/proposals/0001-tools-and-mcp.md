# Proposal: tools, a tool registry, and MCP

Status: proposal. The code in this commit is a working skeleton that shows the shape, not a finished feature.

## Summary

- **One interface, `ITool`**, for everything a model can call. Built-in tools and MCP tools are both just `ITool`s.
- **MCP servers are tool _sources_, not a separate concept at call time.** An MCP server contributes tools named
  `mcp__{server}__{tool}` (the same convention as Claude Code), so the agent loop never needs to know where a tool
  came from.
- **`ToolRegistry`** merges every `IToolSource`, applies the user's approval policies, and is the only thing the
  agent loop talks to (`GetAvailableAsync` to advertise tools, `InvokeAsync` to run one).
- **SQLite stores user configuration, not tool definitions.** That means MCP server configs (`mcp_servers`) and
  per-tool approval policies (`tool_policies`). Built-in tool definitions live in code. MCP tool lists are
  discovered live from the servers. Tool calls and results already go in `conversation_events`.

## Layers

```
Domain                     McpServer (stdio | http config), ToolPolicy (Ask | Allow | Deny)
Application                ITool, IToolSource, IToolApprover, ToolRegistry
                           IMcpServerRepository, IToolPolicyStore
Infrastructure             EF configs + repositories (SQLite), incl. mcp_servers / tool_policies
Infrastructure.Mcp         McpClientPool, McpToolSource, McpTool   – only project using the MCP SDK
Tools                      Built-in tools as an IToolSource; registers ToolRegistry
```

Interfaces (ports) live in Application and implementations (adapters) live in their own projects, so no single
Infrastructure project collects every dependency. `ToolRegistry` is a concrete Application class, like
`AuthService`. It holds the rules (merging tools, approval gating), not storage. What it reads from SQLite
comes in through `IToolPolicyStore`.

A host composes everything:

```csharp
services.AddVibeRePersistence(connectionString)  // SQLite, repositories
        .AddVibeReTools()                         // ToolRegistry + built-in tools
        .AddVibeReMcp();                          // MCP servers as another IToolSource
```

### `ITool`

```csharp
public interface ITool
{
    string Name { get; }              // ^[a-zA-Z0-9_-]{1,64}$ – what model APIs accept
    string? Description { get; }
    JsonElement InputSchema { get; }  // JSON Schema, sent to the model as-is
    Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext context, CancellationToken ct = default);
}

public sealed record ToolContext(Guid ConversationId, string WorkingDirectory);
public sealed record ToolResult(string Content, bool IsError = false);
```

Arguments stay as raw `JsonElement` because that's what models produce and what MCP consumes, so neither side
converts them. A built-in tool can deserialize into its own record type if it wants.

### Registry and approvals

`ToolRegistry(IEnumerable<IToolSource>, IToolPolicyStore)`:

- `ListAsync(context)`: every tool plus its effective approval. Denied tools are included, for settings UIs.
- `GetAvailableAsync(context)`: the tools to advertise to the model. Anything denied is left out.
- `InvokeAsync(name, args, context, approver)`: runs the call. Unknown, denied, declined, and throwing calls
  return `ToolResult { IsError = true }` so the model can recover. Only cancellation propagates.

A tool with no stored policy defaults to **Ask**. `IToolApprover` is the front end's hook for asking the user.
It works like `IAuthInteraction`: the gRPC service would stream an approval request to the CLI or desktop app.

Duplicate or invalid tool names fail fast in `ListAsync`. MCP names are namespaced and sanitized, so in
practice a clash means two built-ins share a name.

### MCP

- `McpServer` holds the config for a `stdio` server (command, args, env) or an `http` server (Streamable HTTP
  with SSE fallback, plus headers). Server names are limited to `[a-z0-9_-]` without `__` so they embed
  unambiguously in tool names.
- `McpClientPool` (singleton) keeps one live client per server. It reconnects when the stored config changes,
  retries after a failed connect, and disconnects servers that were disabled or removed.
- `McpToolSource` (scoped) lists enabled servers from SQLite, gets a client from the pool, and wraps each MCP
  tool in `McpTool`. A server that fails is skipped and logged, so one broken server doesn't hide every tool.
- Only text content is forwarded for now. Images and resources get a placeholder until the model layer can
  carry non-text content.

### Built-in tools (`VibeRe.Tools`)

| Tool | What it does |
|---|---|
| `read_file` | Returns lines with 1-based numbers, paged with `offset`/`limit`. Refuses binaries and directories. |
| `create_file` | Creates a file and any missing parent directories. Only overwrites when `overwrite: true`. |
| `edit_file` | Replaces exact text. `old_string` must be unique unless `replace_all` is set. |
| `list_directory` | Lists one directory. Directories end with `/`. |
| `grep` | Runs ripgrep with `files_with_matches` / `content` / `count` modes, glob filter, case and context options. |
| `find_files` | Finds files by glob, via `rg --files`. |
| `search_docs` | Keyword search over VibeRe's own user guide (`docs/guide`), returning the best-matching sections. |
| `read_doc` | Reads one guide page in full, or lists the pages. |

- Each tool extends `BuiltinTool<TArguments>`. The JSON Schema is generated from the argument record:
  snake_case names, `[Description]`s become descriptions, and constructor parameters without defaults become
  required. Arguments are validated against the same contract, so unknown or mistyped fields go back to the
  model as errors.
- Paths resolve against `ToolContext.WorkingDirectory`. Anything outside it is refused. Symlinks that point
  outside aren't handled yet.
- Ripgrep search behavior:
  - respects `.gitignore`, includes hidden files, and skips `.git`
  - sorts results by path, so the output is deterministic
  - stops early after `max_results` lines, and times out after 30 s
- Ripgrep is not bundled. The tools use `$VIBE_RE_RG_PATH`, or `rg` on the `PATH`, and explain how to install
  it when it's missing. Its tests are skipped when ripgrep isn't available.

### Own documentation

The user guide in `docs/guide/*.md` is embedded in `VibeRe.Tools` at build time, so the docs the agent sees
always match the installed version and work offline.

- This is a built-in tool rather than an MCP server because nothing outside VibeRe needs it yet, and a built-in
  tool needs no extra process or configuration.
- If other agents (for example Claude Code working on this repo) should query the docs, `vibe-re-server` could
  expose the same `DocsLibrary` as an MCP endpoint later.
- Search is keyword ranking over heading-delimited sections. Heading matches weigh more, and terms are cut to 6
  characters as crude stemming. The guide is small enough that this beats pulling in an embedding model.

### Storage

```
mcp_servers    Id, Name (unique), Transport, Enabled, Command, Arguments (json),
               Environment (json), Url, Headers (json)
tool_policies  ToolName (pk), Approval
```

Why SQLite rather than a JSON config file: conversations already live there, the server owns all state, and
the CLI and desktop app edit config through gRPC anyway. A file is easy to hand-edit, though. If we want that,
an `mcp.json` importer can write into the same table.

## Open questions

1. **Secrets in MCP config.** `Environment` and `Headers` often hold tokens, and they're currently plaintext
   in SQLite. Proposal: allow values like `${secret:github-token}` that resolve through `ICredentialStore` (the
   Keychain on macOS) when a client connects.
2. **Policy granularity.** Policies are exact tool names today. Allowing `mcp__github__*` would make it easy
   to allow or deny a whole server.
3. **Tool list caching.** `McpToolSource` calls `tools/list` on every `ListAsync`. We should cache per
   connection and invalidate on `notifications/tools/list_changed`.
4. **Per-conversation tool sets.** Should a conversation be able to pin or disable servers? It could be a
   `conversation_tools` table later. Nothing here blocks it.
5. **Schema migrations.** The DB is still created with `EnsureCreated`. These new tables are a good moment to
   switch to EF migrations before real user data exists.

## Next steps

- More built-ins: `bash` (needs its own design for timeouts, background processes and sandboxing), `web_fetch`.
- Ship ripgrep with `make install`, or at least check for it, so search works out of the box.
- A `tools.proto` gRPC service: list tools, set policies, and add, remove, or enable MCP servers, plus an
  approval stream.
- CLI: `vibe-re mcp add <name> -- <command> [args...]`, `vibe-re mcp list`, `vibe-re tools allow <name>`.
- Wire `ToolRegistry` into the agent loop. Record `tool/call` and `tool/result` events with the registry name.
