# Security policy

clauderoo works with sensitive data: Claude login tokens, API keys, Claude setup tokens and Google Cloud sign-ins. Security reports are taken seriously.

## Reporting a vulnerability

**Please don't open a public issue for a security problem.**

Report it privately through GitHub: open the repository's **Security** tab and choose **Report a vulnerability**. Include:

- what an attacker can do, and what they need first (for example "a Claude session calling the MCP server" or "a crafted import file");
- the steps or a minimal proof of concept;
- the clauderoo version (`cr version`) and Windows version.

You'll get a reply within a week. Once a fix is released, you'll be credited in the changelog unless you'd rather not be.

## Supported versions

Only the latest release gets security fixes. Update with `cr upgrade`.

## What clauderoo guarantees

These are the promises a report would break. Anything that breaks one of them is a vulnerability:

- **Your home profile stays untouched.** clauderoo never writes inside `~/.claude` or `~/.claude.json`.
- **Login tokens stay in memory.** clauderoo reads a profile's `.credentials.json` only to fetch usage. It never writes, copies, logs or shows the file or its tokens, and never refreshes them.
- **Secrets are stored encrypted.** API keys and Claude setup tokens are stored only with Windows DPAPI for your user. They never go into `settings.json`, a command line, `--json` output, an export file or a log.
- **Nothing is deleted.** Removed profiles and replaced files are moved aside, and junctions are never deleted recursively.
- **The MCP `delegate` tool stays inside its target profile.**
  - The prompt is sent on stdin, and the command line is fixed.
  - The run uses the target profile's own permissions and never skips them.
  - It never loads MCP servers, including clauderoo's own.
  - It never runs in a folder that holds or contains sign-ins: `%USERPROFILE%\.clauderoo`, `~/.claude`, your user folder, `%APPDATA%\gcloud` or `~/.ssh`. It also never runs in a network path or a folder that can't be resolved.
  - It never inherits the calling session's provider, model or account settings.
