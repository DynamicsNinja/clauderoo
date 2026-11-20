# Packaging

## winget

**After the package is in winget, releases go there on their own.** The release workflow's `winget` job runs
`wingetcreate update` for every tag without a `-`, which opens a PR on
[microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) with the new version, URL and hash. It needs a
repository secret named `WINGET_TOKEN`: a classic personal access token with the `public_repo` scope, from the account
that owns the winget-pkgs fork. Without the secret, or while the package isn't in winget yet, the job skips with a
notice and the release is unaffected. winget's reviewers still merge each PR.

`winget/` holds a manifest for `DynamicsNinja.clauderoo`, for the first submission and for testing by hand. Once the
package is in winget, wingetcreate builds each version from the one there, so these files don't need updating. To use
them for a version by hand:

1. Set `PackageVersion` in all three files to the release version (no `v`), and the version inside `InstallerUrl`.
2. Set `InstallerSha256` to the contents of the release's `clauderoo-<version>-win-x64.zip.sha256`.
3. Check it with `winget validate packaging\winget`, then test it with `winget install --manifest packaging\winget`.

The package installs `clauderoo` and `cr` as portable commands. The tray app (`clauderoow.exe`) is only set up
by `clauderoo.exe install`, so winget users who want it run that once.

## Releases

Pushing a tag such as `v1.2.0` runs `.github/workflows/release.yml`, which tests, publishes both projects stamped
with the version and repository, and attaches `clauderoo-<version>-win-x64.zip` and its `.sha256` to the GitHub
release. A tag with a `-` (such as `v1.3.0-rc1`) is marked as a pre-release. `clauderoo upgrade` and the tray's
update notice read those two assets.
