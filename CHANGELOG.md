# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Changed
- **Profile manager redesigned.** A header shows the profile's name, provider and account, sessions running, its usage bars, and **Start session** / **Set as default**. Below it, tabs: **Account** (provider as a dropdown, Vertex model names folded under *Models (optional)*, the setup check), **Folders (n)** (projects that use the profile, and *Start session opens in*), **Environment (n)** and **Sharing**. **Save** and a new **Revert** sit under the tabs; Folders and Sharing say they save right away. The tabs scroll, so the window has a fixed size and fits small screens.
- The profile manager's list shows each profile's account and its usage (`5h 42% · 7d 63%`). List-wide and app-wide actions moved to a **File** / **Tools** menu bar; only **New**, **Rename** and **Remove** stay under the list.
- Tray menu: rows read `name · account · usage` (the icon shows the provider); a bold **Start ‹default›** comes first; **Update to vX** sits under the header with its own icon; **Advanced** is now **Settings**, with a **Troubleshoot** group; **Refresh usage** shows how old the numbers are; **Continue a conversation** has its own icon.
- Clearer labels: **Restore removed profiles…**, **Connect Claude to clauderoo (MCP)…**, **Auto-switch at 90% usage**, **Include a live test**, **Start session opens in**, **Reset**, **Remove variable**.

### Fixed
- Usage colours now follow one rule in the tray and the profile manager: amber from the auto-switch threshold (or 70%), red from 90%. Before, 78% was amber in the window and plain in the tray.
- A signed-out profile no longer shows stale usage bars in the profile manager, and "not signed in" is said once.
- Providers whose usage isn't tracked (Vertex, API key, Claude token) no longer show an empty Usage box.
- A highlighted tray row with coloured parts (near a limit, not signed in) draws all its text in the highlight colour.

## [0.3.2] - 2026-11-19

### Fixed
- The update window was blank: its text was squeezed to 1 pixel wide.
- **Refresh usage** no longer makes the open tray menu flicker when the new numbers arrive.

## [0.3.1] - 2026-11-19

### Added
- **Check for updates now…** under Advanced in the tray menu checks GitHub right away and offers **Update now** when a newer version exists.

### Changed
- The tray checks for updates every 6 hours instead of once a day, so a new release shows up the same day.
- Tray menu items that change a setting keep the menu open and show the change in place: **Start with Windows**, **Auto-switch**, **Check for updates automatically**, **New sessions open in**, **Refresh usage** (shows *Refreshing usage…*, then the new numbers) and **Set as default** (the star moves). Items that open a window or start a session still close it.

## [0.3.0] - 2026-11-19

### Added
- `cr start` in a folder that isn't mapped offers to map it to the profile being started (yes / no / never for this folder).
- `cr start <name>` in a folder mapped to a different profile asks before starting: start it anyway, start the mapped profile instead (default), or remap the folder.
- `cr start` says it is starting Claude (profile and folder) before Claude's own start-up, instead of a silent terminal.
- The tray's **Update to vX** downloads and installs in a progress window instead of a terminal, then restarts the tray on the new version.

### Changed
- `cr upgrade` restarts a running tray app itself instead of asking you to (a tray older than this release still needs restarting by hand once).

## [0.2.0] - 2026-11-18

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
- `cr check <name> --live` tests an API key (one free call to the Anthropic models list) or a Claude token (one tiny `claude -p` prompt, since a setup token can only make model requests). clauderoo now records when each key and token was saved, and `cr status`, `cr check`, the tray's Problems list and the manager warn when a Claude token nears or passes its one-year life (`tokenWarnAfterDays` in `config.json`, default 335).
- `cr doctor [--json]` and the tray's **Advanced › Check this PC…**: a read-only, parallel checklist of Claude Code and its version, Windows Terminal, gcloud (with Vertex profiles), the install folder and `PATH`, the MCP server registration, the home folder, the default profile and every profile's shared-folder links.
- `cr completion powershell|bash` prints a tab-completion script for commands, flags and profile names.
- MCP tools `which_profile`, `list_sessions` and `check_profile` (all read-only).
- `cr env <name> --json`.
- `cr auto --schedule on|off [--every N]` creates or removes a per-user Task Scheduler task that runs `auto --once` without a console window. `cr auto --schedule` on its own says whether the task is set up.
- `cr uninstall --purge` also deletes `~/.clauderoo` after you confirm, removing junctions as links only and never touching `~/.claude`.
- The manager's **Export…** can export only the selected profile.
- Switching a profile away from an API key or Claude token keeps the saved secret, so switching back reuses it: `cr provider <name> apikey|token` takes an empty answer (or `--keep`) to use it and `--new` to replace it, and the manager says the saved one will be used. `cr forget <name> apikey|token` and the manager's **Forget the saved key/token** delete one. Removing a profile still deletes both. A kept secret is never used, printed by `cr apikey` or warned about. `import --force` reuses a kept secret too.
- **Live check** in the profile manager tests an API key or Claude token for real, like `cr check --live`, with the same progress checklist as the Vertex check.

### Fixed
- A missing home Claude folder, or a shared link whose home folder is gone, is now reported as a problem (in `cr status` and the tray) instead of being skipped silently.
- Settings changed at the same time by the CLI, the tray app, the MCP server and `cr auto --once` no longer overwrite each other: every change to `config.json` now takes a machine-wide lock, re-reads the file and saves it in one go.
- The tray's Problems list now explains every problem a sync or sharing change can report, instead of showing some under "Something needs attention": an entry a profile has of its own, a folder with its own files mixed into shared ones (with **Keep its own copy**), excluded plugins that stay on because the profile owns `settings.json` (with **Share it again**), links left in the old home folder, and a link that couldn't be removed when an item stopped being shared.
- Saving settings no longer fails with "Access to the path is denied" when another clauderoo window, an editor or antivirus has `config.json` open at that moment: the save waits briefly and tries again.

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

[Unreleased]: https://github.com/DynamicsNinja/clauderoo/compare/v0.3.2...HEAD
[0.3.2]: https://github.com/DynamicsNinja/clauderoo/compare/v0.3.1...v0.3.2
[0.3.1]: https://github.com/DynamicsNinja/clauderoo/compare/v0.3.0...v0.3.1
[0.3.0]: https://github.com/DynamicsNinja/clauderoo/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/DynamicsNinja/clauderoo/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/DynamicsNinja/clauderoo/releases/tag/v0.1.0
