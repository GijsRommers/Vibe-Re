# Tools

Tools are what the model calls to act on your project. VibeRe has built-in tools, and you can add more by
connecting MCP servers (see mcp.md). Both kinds work the same way and follow the same approval rules.

## Built-in tools

| Tool             | What it does                                                                                                      |
|------------------|-------------------------------------------------------------------------------------------------------------------|
| `read_file`      | Reads a text file with line numbers. `offset` and `limit` page through large files.                               |
| `create_file`    | Creates a file and any missing parent directories. Existing files are only replaced with `overwrite: true`.       |
| `edit_file`      | Replaces an exact piece of text in a file. The text must occur once unless `replace_all` is true.                 |
| `list_directory` | Lists one directory. Directories end with `/`.                                                                    |
| `grep`           | Searches file contents with ripgrep. Can list matching files, show matching lines with context, or count matches. |
| `find_files`     | Finds files by glob, such as `*.cs` or `src/**/*.ts`.                                                             |
| `search_docs`    | Searches this documentation.                                                                                      |
| `read_doc`       | Reads one documentation page, or lists all pages.                                                                 |

File tools only work inside the conversation's working directory. Paths outside it are refused.

## Search and ripgrep

`grep` and `find_files` need [ripgrep](https://github.com/BurntSushi/ripgrep). Install it with
`brew install ripgrep`, or set `VIBE_RE_RG_PATH` to the `rg` executable.

Searches respect `.gitignore`, include hidden files, skip `.git`, and time out after 30 seconds. A glob that
contains `/` is matched from the working directory, not from the searched folder.

## Approvals

Every tool has an approval setting:

- **Ask** (default): VibeRe asks you before each call.
- **Allow**: calls run without asking.
- **Deny**: the tool is hidden from the model and calls are refused.

Settings are stored per tool name, so MCP tools are configured as `mcp__{server}__{tool}`. When a call is
denied or declined, the model gets an error message and can try something else.
