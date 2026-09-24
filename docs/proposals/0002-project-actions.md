# Proposal: project actions in `vibe.re`

Status: proposal. Builds on [0001](0001-tools-and-mcp.md): actions are one more `IToolSource`.

## Summary

A project describes its common commands in a `vibe.re` file at its root, like T3 Code's project scripts:

```jsonc
{
  "actions": [
    { "name": "restore", "command": "dotnet restore", "runOnWorktreeCreate": true },
    { "name": "test", "command": "make test", "description": "Run every test project." },
    { "name": "server", "command": "make server", "tool": false }
  ]
}
```

| Field                 | Default | Meaning                                                                 |
|-----------------------|---------|-------------------------------------------------------------------------|
| `name`                | –       | `[a-z0-9_-]`, 1–32 chars, no `__`, unique in the file.                  |
| `command`             | –       | Run with `/bin/sh -c` (`cmd /c` on Windows) from the project root.      |
| `description`         | none    | Shown to the model as the tool description.                             |
| `runOnWorktreeCreate` | `false` | Run, in file order, right after a worktree is created.                  |
| `tool`                | `true`  | Offer it to the model as `project__{name}`. Turn off for dev servers.   |

The file is JSON with comments and trailing commas. Unknown properties are errors, so a typo doesn't silently
do nothing.

## How it works

- `ProjectConfigFileLoader` finds the nearest `vibe.re` from the working directory up, stopping at the
  repository root (`.git` directory or worktree `.git` file), so it never picks up a file from outside the repo.
- `ProjectActionToolSource` turns each action into a `project__{name}` tool with no arguments. It reads the file
  on every listing, so edits apply to the next model turn. An invalid file is logged and gives no tools.
  Calls go through `ToolRegistry`, so the usual Ask/Allow/Deny policies apply.
- Tool results are the combined stdout/stderr (the last 30k characters) plus the exit code. A non-zero exit
  is an error result.
- `ProjectActions.SetUpWorktreeAsync(worktreePath, sourceRoot)` runs the `runOnWorktreeCreate` actions from
  the new worktree's own `vibe.re` and stops at the first failure. Whatever creates worktrees calls it.
- Every action gets `VIBE_RE_PROJECT_ROOT`. Worktree setup also gets `VIBE_RE_SOURCE_ROOT`, the checkout the
  worktree came from, e.g. for `cp "$VIBE_RE_SOURCE_ROOT/.env" .`.

`IToolSource.GetToolsAsync` and `ToolRegistry.ListAsync`/`GetAvailableAsync` now take the `ToolContext`,
because which tools exist depends on the conversation's working directory.

## Open questions

1. **Arguments.** Actions take none, so `test` can't be narrowed to one test. An optional `args` string
   appended to the command is easy, but then allowing the action is close to allowing a shell.
2. **Timeouts.** A command that never exits blocks the turn until it's cancelled. A per-action
   `timeoutSeconds` could cap it.
3. **Streaming.** Output only comes back when the command exits. The front ends will want live output for
   long builds.
4. **Trust.** A cloned repo's `vibe.re` runs on worktree creation. Before running setup for a project for the
   first time, we should ask the user.

## Next steps

- Create worktrees from the server (`git worktree add`) and call `SetUpWorktreeAsync`.
- gRPC and CLI: `vibe-re actions` to list and `vibe-re run <name>` to run one, plus buttons in the desktop app.
