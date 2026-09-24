# Getting started

VibeRe is a coding agent with two parts: the `vibe-re` command you run, and a background server
(`vibe-re-server`) that holds your credentials, conversations and tools.

## Install

From a checkout of the repository:

```sh
make install                 # installs vibe-re and vibe-re-server into ~/.local/bin
make install PREFIX=/usr/local
```

Make sure the install directory is on your `PATH`. `make uninstall` removes both binaries.

## Signing in

Run `vibe-re`. If you aren't signed in to Mistral yet, it opens your browser to sign in.
The key is stored in the macOS Keychain, or elsewhere in an owner-only file in `~/.vibe-re/credentials.json`.

Setting `MISTRAL_API_KEY` in the server's environment takes precedence over the stored key. After changing it,
run `make stop` so the server restarts and picks up the change.

## The background server

`vibe-re` starts `vibe-re-server` automatically when it isn't running. The server listens on
`http://localhost:5067` and writes its log to `~/.vibe-re/server.log`.

- `make stop` stops the server. The next `vibe-re` starts it again.
- `make server` runs the server in the foreground from source, for development.

## Environment variables

| Variable | Effect |
|---|---|
| `VIBE_RE_HOME` | Data directory. Defaults to `~/.vibe-re`. |
| `VIBE_RE_SERVER` | Server address the CLI connects to. Defaults to `http://localhost:5067`. |
| `MISTRAL_API_KEY` | Mistral API key. Overrides the stored credential. |
| `VIBE_RE_RG_PATH` | Path to the ripgrep executable used by the search tools. Defaults to `rg` on the `PATH`. |
