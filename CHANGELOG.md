# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- `cr removed` lists removed copies of profiles with when they were removed and their size (`--json` too).
- `cr restore <name> --at <id>` restores a specific removed copy instead of the newest. A restore now also brings back the profile's isolated folder.
- `cr purge [name] [--at <id>] [--older-than <age>] [--all] [--yes]` permanently deletes removed copies, after showing what goes and the space freed. It removes junctions as links and never touches their targets.
- **Removed…** in the profile manager window lists removed copies, with **Restore** and **Delete permanently**.
- `cr startdir <name> [<dir> | --clear]` sets the folder **Start session** opens a profile in; `cr list` (and `--json`) shows it, and the profile manager has a **Start folder** field with **Browse…**.
- `cr clone <source> <new>` (also `cr add <new> --from <source>` and **Duplicate…** in the profile manager) copies a profile's provider, Vertex settings, environment variables, owned items and start folder. Never the sign-in, conversations or API key/token.
- `cr home [<path>]` shows the home Claude folder, or points clauderoo at it after `~/.claude` moved, re-linking every profile.
- `config.json` has a `schemaVersion`, with step-by-step upgrades of older files. A file from a newer clauderoo is still read, but changes are refused instead of overwriting settings this version doesn't understand.
- A log file at `~/.clauderoo/logs/clauderoo.log` (1 MB, three files) for warnings, errors, sync problems, auto-switch decisions and settings saves, from the CLI, the tray app and the MCP server. Secrets are never written to it.
- `cr logs [--path] [--tail N]` shows the end of the log, or where it is.
- `cr exclude <profile> <item>/<entry>` and `cr include` leave single entries of a shared item out of one profile: a skill, agent, command, hook or output style, an MCP server (`mcpServers/<name>`) or a plugin (`plugins/<name>[@marketplace]`). `cr exclude <profile>` and `cr list` show what each profile leaves out, and exports carry it.
- `cr shared list`, `cr shared add <name> [--dir|--file]` and `cr shared remove <name>` share more files and folders of the home profile. Sign-ins, `.claude.json`, conversations, history and sessions can't be added, and the built-in items can't be removed.
- The profile manager has a **Sharing…** window per profile: each shared item's state (shared, own copy, or what's left out), with **Own**, **Share again** and **Choose what's shared…**.
- `cr config terminal wt|conhost` and `cr config shell cmd|pwsh|powershell` choose where `--tab` and the tray open sessions and which shell runs them. The `terminal` setting is now honoured (`conhost` forces a console window). With PowerShell, prompts and folders may contain `" % ! ;`. Also in the tray under **Advanced › New sessions open in**.
- `cr wt-profiles [--remove]` adds a "Claude · <name>" Windows Terminal profile for each profile, kept up to date as profiles change.
- `cr code [name] [dir]` opens VS Code (or Cursor with `--cursor`, or `--editor <exe>`) with the profile's environment, so the Claude Code extension uses that profile. The tray has **Open in VS Code…** for each profile.

### Fixed
- A missing home Claude folder, or a shared link whose home folder is gone, is now reported as a problem (in `cr status` and the tray) instead of being skipped silently.
- Settings changed at the same time by the CLI, the tray app, the MCP server and `cr auto --once` no longer overwrite each other: every change to `config.json` now takes a machine-wide lock, re-reads the file and saves it in one go.

## [0.1.0] - 2026-11-17

First public release.

### Profiles and sessions
- Run Claude Code under several named profiles side by side, each with its own login and history.
- Your existing `~/.claude` becomes the home profile.
- Every profile shares plugins, skills, hooks, agents, commands, settings, `CLAUDE.md` and MCP servers from home.
- `cr start` / `cr run` start a session in this terminal, or in a new Windows Terminal tab with `--tab`.
- `cr default` picks the profile a plain `claude` uses.
- `cr own` / `cr share` let a profile keep its own copy of an item.
- `cr rename`, `cr remove` and `cr restore`: removed profiles are moved aside, never deleted.
- Folder mapping: `cr map`, `cr unmap` and `cr maps`. `cr start` with no name picks the profile mapped to the folder.
- `cr start --isolated` runs a profile without the shared plugins, skills, hooks, `CLAUDE.md` or MCP servers.
- `cr which` shows which profile this terminal or session uses.
- `cr sessions` and `cr resume <id> --as <profile>` continue a conversation under another profile. The tray has a **Continue a conversation…** picker.

### Sign-in providers
- Each profile can use its own Claude login, Google Vertex AI, an Anthropic API key, or a Claude token from `claude setup-token`.
- API keys and tokens are stored encrypted with DPAPI.
- `cr check` runs a read-only Vertex setup checklist, with all checks in parallel and live progress.
- Per-profile environment variables with `cr env`.

### Usage limits and auto-switch
- `cr usage` and the tray show each Claude-login profile's 5-hour and 7-day usage, reset times, per-model limits and plan.
- Auto-switch (`cr auto`, plus a tray setting) moves the default for new terminals to the next profile with room when one reaches its limit. A default you pick yourself is never overridden.

### Sharing and scripting
- `cr export` / `cr import`, plus window buttons, share profile settings (never secrets or logins), for example to onboard a colleague onto a Vertex project.
- `--json` output for `list`, `status`, `maps`, `which`, `check`, `usage`, `sessions` and `auto --once`.
- `cr mcp` is an MCP server, so a Claude session can list profiles, read usage, change the default, and delegate a prompt to another profile. Set it up with `cr mcp install` or **Use from Claude…**.

### App
- A tray app with status icons, a profile manager window, a Problems list with one-click fixes, and an update notice.
- `cr version`, `cr upgrade` and `cr uninstall`.
- Release builds from GitHub Actions, with a draft winget manifest.

[Unreleased]: https://github.com/DynamicsNinja/clauderoo/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/DynamicsNinja/clauderoo/releases/tag/v0.1.0
