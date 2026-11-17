# Packaging

## winget (draft)

`winget/` holds a draft manifest for `clauderoo.clauderoo`. It is not submitted anywhere. The placeholders
must be filled in for each release before it is sent to
[microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs):

1. Replace `OWNER` with the GitHub owner of the repository.
2. Set `PackageVersion` in all three files to the release version (no `v`), and the version inside `InstallerUrl`.
3. Set `InstallerSha256` to the contents of the release's `clauderoo-<version>-win-x64.zip.sha256`.
4. Check it with `winget validate packaging\winget`, then test it with `winget install --manifest packaging\winget`.

The package installs `clauderoo` and `cr` as portable commands. The tray app (`clauderoow.exe`) is only set up
by `clauderoo.exe install`, so winget users who want it run that once.

## Releases

Pushing a tag such as `v1.2.0` runs `.github/workflows/release.yml`, which tests, publishes both projects stamped
with the version and repository, and attaches `clauderoo-<version>-win-x64.zip` and its `.sha256` to the GitHub
release. `clauderoo upgrade` and the tray's update notice read those two assets.
