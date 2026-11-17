## What and why

<!-- What does this change, and what problem does it solve? Link the issue: "Fixes #123". -->

## How it was tested

<!-- New or changed tests, and anything you checked by hand (e.g. the tray at 150% scaling). -->

## Checklist

- [ ] `dotnet test clauderoo.slnx` passes, with no new warnings.
- [ ] Every behaviour change has a test.
- [ ] Tests stay off the real `~/.claude`, `%USERPROFILE%\.clauderoo`, the user environment, the registry and the network.
- [ ] No secret can reach a log, an exception message, `--json` output, a command line or a file.
- [ ] Nothing writes inside `~/.claude`, and nothing deletes user data.
- [ ] README and `CHANGELOG.md` (under **Unreleased**) are updated.
