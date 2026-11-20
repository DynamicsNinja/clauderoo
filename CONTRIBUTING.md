# Contributing to clauderoo

Thanks for helping. Bug reports, ideas and pull requests are all welcome.

## Before you start

- **Bugs and ideas:** open an issue using one of the templates. For anything bigger than a small fix, open an issue first, so we can agree on the approach before you write code.
- **Security problems:** don't open an issue. Follow [SECURITY.md](SECURITY.md).

## Build and test

You need Windows 10 or 11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet build clauderoo.slnx
dotnet test  clauderoo.slnx
```

To try your build for real, publish both projects into one folder and install from there:

```powershell
dotnet publish src\Clauderoo.Cli  -c Release -r win-x64 -p:PublishSingleFile=true --self-contained false -o publish
dotnet publish src\Clauderoo.Tray -c Release -r win-x64 -p:PublishSingleFile=true --self-contained false -o publish
publish\clauderoo.exe install
```

Close the tray app first. If it's running, the installer can replace it but the old one keeps running.

## Rules for code and tests

These keep users' accounts safe. A pull request that breaks one won't be merged.

- **Tests never touch the real machine.** They must not touch the real `~/.claude`, `%USERPROFILE%\.clauderoo`, the user environment, the registry or the network. Use the `TempHome` and `FakeEnv` fixtures and a fake `HttpMessageHandler`.
- **Never write inside `~/.claude` or `~/.claude.json`.**
- **Secrets stay secret.** Only `CredentialPeek` reads `.credentials.json`, in memory. API keys and tokens are stored only through `ISecretStore` (DPAPI). No secret may reach a log, an exception message, `--json` output, a command line or a file.
- **Never delete user data.** Move it aside instead, and never delete a junction recursively.
- **Shared state files** use `AtomicFile` plus a named `Local\clauderoo-*` mutex.
- **Tray writes** to config or the default profile go through the tray's `WriteGate`, and never block the UI thread.
- **User-facing text** is plain language. Raw tool output belongs in **Show details**.
- **New commands** get a row in the README, plus `--json` if they print data.

## Pull requests

1. Branch from `main`.
2. Add or update tests for every behaviour change. Write the test first where you can.
3. Run the whole suite and make sure it passes with no new warnings.
4. Update `README.md` and add a line under **Unreleased** in `CHANGELOG.md`. CI fails a `feat:` or `fix:` PR
   without one; label it `no-changelog` if users won't notice the change.
5. Fill in the pull request template.

## Releases

Maintainers release by pushing a `vX.Y.Z` tag. See [packaging/README.md](packaging/README.md).
