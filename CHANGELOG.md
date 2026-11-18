# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- `cr check <name> --live` tests an API key (one free call to the Anthropic models list) or a Claude token (one tiny `claude -p` prompt, since a setup token can only make model requests). clauderoo now records when each key and token was saved, and `cr status`, `cr check`, the tray's Problems list and the manager warn when a Claude token nears or passes its one-year life (`tokenWarnAfterDays` in `config.json`, default 335).
- `cr doctor [--json]` and the tray's **Advanced › Check this PC…**: a read-only, parallel checklist of Claude Code and its version, Windows Terminal, gcloud (with Vertex profiles), the install folder and `PATH`, the MCP server registration, the home folder, the default profile and every profile's shared-folder links.
- `cr completion powershell|bash` prints a tab-completion script for commands, flags and profile names.
- MCP tools `which_profile`, `list_sessions` and `check_profile` (all read-only).
- `cr env <name> --json`.
- `cr auto --schedule on|off [--every N]` creates or removes a per-user Task Scheduler task that runs `auto --once` without a console window.
- `cr uninstall --purge` also deletes `~/.clauderoo` after you confirm, removing junctions as links only and never touching `~/.claude`.
- The manager's **Export…** can export only the selected profile.

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
