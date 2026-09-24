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
Domain          McpServer (stdio | http config), ToolPolicy (Ask | Allow | Deny)
Application     ITool, IToolSource, IToolApprover, ToolRegistry
                IMcpServerRepository, IToolPolicyStore
Infrastructure  EF configs + repositories (SQLite)
                Tools/Mcp: McpClientPool, McpToolSource, McpTool (ModelContextProtocol.Core SDK)
                AddVibeReTools()
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

- `ListAsync()`: every tool plus its effective approval. Denied tools are included, for settings UIs.
- `GetAvailableAsync()`: the tools to advertise to the model. Anything denied is left out.
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

- Built-in tools (`read_file`, `write_file`, `bash`, `grep`, ...) as an `IToolSource` registered in DI.
- A `tools.proto` gRPC service: list tools, set policies, and add, remove, or enable MCP servers, plus an
  approval stream.
- CLI: `vibe-re mcp add <name> -- <command> [args...]`, `vibe-re mcp list`, `vibe-re tools allow <name>`.
- Wire `ToolRegistry` into the agent loop. Record `tool/call` and `tool/result` events with the registry name.
