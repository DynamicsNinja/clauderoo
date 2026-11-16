# clauderoo

Run Claude Code under several named account profiles on Windows. Your existing `~/.claude` stays the **home** profile; extra profiles get their own login and history but share your plugins, skills, settings and MCP servers.

## Install

Publish both projects as single-file builds into one folder, then run the installer from it:

```
dotnet publish src\Clauderoo.Cli  -c Release -r win-x64 -p:PublishSingleFile=true --self-contained false -o publish
dotnet publish src\Clauderoo.Tray -c Release -r win-x64 -p:PublishSingleFile=true --self-contained false -o publish
publish\clauderoo.exe install
```

`install` copies `clauderoo.exe` and `clauderoow.exe` to `%LOCALAPPDATA%\clauderoo` and adds that folder to your user PATH. It also installs `cr.exe`, a short alias: `cr start work` is the same as `clauderoo start work`. Open a new terminal afterwards. Then run `clauderoo init`.

## Commands

| Command | What it does |
| --- | --- |
| `init [--name <n>] [--home <path>]` | Set up clauderoo around your existing `~/.claude` |
| `add <name>` | Create a profile (names: `a-z`, `0-9`, `-`) |
| `list` | List profiles and their logged-in accounts |
| `start <name> [dir] [-- args]` | Run Claude for a profile in this terminal (`run` is the same) |
| `start <name> [dir] --tab [-- args]` | Open it in a new Windows Terminal tab instead (prompts can't contain `" % ! ;` there) |
| `default <name>` / `default --clear` | Choose which profile plain `claude` uses in new terminals |
| `own <name> <item>` | Keep a profile's own copy of a shared item |
| `share <name> <item>` | Go back to sharing an item from the home profile |
| `remove <name> [--force]` | Remove a profile (its folder is moved, not deleted) |
| `provider <name> login\|vertex\|apikey` | Choose how a profile signs in (`vertex` takes `--project`, `--region`, optional `--sonnet`/`--opus`/`--haiku`; `apikey` prompts for the key) |
| `env <name> [set A=1 B=2 \| unset A]` | Show, set or unset a profile's environment variables |
| `check <name>` | Check that the profile's sign-in works (exit 1 if not) |
| `apikey <name>` | Print the profile's API key (what Claude's `apiKeyHelper` runs) |
| `manage` | Open the profile manager window |
| `status` | Show the default profile and any problems |
| `tray` | Start the tray app |
| `autostart on\|off` | Start the tray app at login |
| `install` | Copy clauderoo to `%LOCALAPPDATA%\clauderoo` and add it to PATH |

## Providers

Every profile except the home profile can use a different way of signing in: its own Claude **login** (default), **Google Vertex AI**, or an Anthropic **API key**. The home profile always uses its own Claude login.

Use the manager window (`clauderoo manage`, or the tray menu) to pick a provider and edit environment variables. The same from the command line:

```
cr provider work vertex --project my-gcp-project --region us-east5 [--sonnet <model>] [--opus <model>] [--haiku <model>]
cr provider work apikey          # reads the key from stdin or prompts without echo
cr provider work login
cr env work set HTTPS_PROXY=http://proxy:8080
cr env work unset HTTPS_PROXY
cr check work
```

Vertex needs the Google Cloud CLI (`winget install Google.CloudSDK`), signed in twice: `gcloud auth login` and `gcloud auth application-default login`. API keys are stored encrypted for your Windows user (DPAPI) inside the profile folder.

If a profile owns its `settings.json`, set the provider first and then run `cr share <profile> settings.json` to move it onto the shared copy plus the provider overlay. In an owned `settings.json`, provider settings replace any hand-written Vertex or API-key keys (`CLAUDE_CODE_USE_VERTEX`, `ANTHROPIC_API_KEY`, `apiKeyHelper`, …); the first time that happens clauderoo saves a `settings.json.bak-<timestamp>` copy.

## Sharing model

The home profile is the source of truth. Other profiles follow it one way (home to profile):

- **Folders** (`plugins`, `skills`, `hooks`, `agents`, `commands`, `output-styles`) are junctions pointing at the home folders.
- **Files** (`settings.json`, `CLAUDE.md`, `keybindings.json`) are copied.
- **MCP servers** (`mcpServers` in `~/.claude.json`) are merged into the profile's `.claude.json`.

If you edit a copied file inside a profile, the next sync replaces it with home's and keeps a `.bak-<timestamp>` copy next to it. To keep a profile's own version, run `clauderoo own <profile> <item>` (for example `settings.json`, `skills` or `mcpServers`); `clauderoo share <profile> <item>` goes back to following home.

clauderoo never writes inside `~/.claude` or `~/.claude.json`, and never touches `.credentials.json`.

## Plugins caveat

Plugins installed or updated from a non-home profile record that profile's folder path in the shared plugin records (`plugins\installed_plugins.json`, `plugins\known_marketplaces.json`). Prefer managing plugins from the home profile. `clauderoo remove` refuses to remove a profile whose folder is referenced there; reinstall those plugins from home, or use `clauderoo remove <name> --force`.

## Removed profiles

Removed profiles are moved to `%USERPROFILE%\.clauderoo\profiles\_removed\<name>-<timestamp>`, never deleted. Shared-folder junctions are unlinked first. If you clean up `_removed` yourself, use Explorer or `rd /s`. Do not use PowerShell 5.1 `Remove-Item -Recurse` on these folders: it can follow junctions into the shared folders.
