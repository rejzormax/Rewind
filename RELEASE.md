# Release process

Rewind uses public releases from **rejzormax/Rewind**. Source pushes run checks and notify the configured Discord channel. Version tags publish a tested Windows ZIP. Installed users update from completed releases.

## Repository setup

- Publish this repository's `main` branch to **rejzormax/Rewind** as public source. Keep original assets, SDKs, build outputs and secrets ignored.
- Add an Actions secret named **DISCORD_GITHUB_WEBHOOK** containing the existing webhook for **#github-updates**. Never commit that URL.
- Add an Actions secret named **REWIND_LOGO_SVG** containing the private `assets/rewind.svg` contents to use the R logo in automated releases. Otherwise CI uses the generic fallback mark.
- The release job requests repository contents write access. Build and notification jobs are read-only.

## Publish a version

1. Set the version in **Directory.Build.props**.
2. Run `scripts/build.ps1 -OfficialRelease`, `scripts/verify.ps1` and `scripts/package.ps1` locally.
3. Commit the intended public files, create a matching version tag such as `v0.4.0`, and push it.
4. The release workflow builds, tests, packages **Rewind.exe** and **auto-updater.exe**, then publishes the Windows ZIP and checksum.

A source push without a matching version tag does not publish an executable update.

## Updater

The app checks after startup and every six hours. It selects newer stable public releases from the configured repository, requires GitHub's SHA-256 digest, bounds download sizes, validates archive paths and checks executable versions. Installation waits until recording and imports are idle.

The helper installs into a new per-user version folder and waits for the new app to acknowledge startup. The previous executable, settings and recordings stay separate. Original portable launchers redirect to the latest managed version. Failed startup restores the previous marker and attempts to reopen the previous app; the incomplete version is removed so the same update can be retried. Users can disable automatic updates in Capture settings.

## Release checks

- Verify clips, audio, microphone, hotkeys, pause/quit and imports on the target Windows machine.
- Run updater checks for hash failure, downgrades, traversal, duplicate files, links and invalid installation markers. The integration suite also runs the real helper against a versioned executable to check parent shutdown, startup confirmation, rollback and retry.
- Test upgrading from a previous release and confirm settings and clips remain available.
- Satisfy the bundled FFmpeg build's corresponding-source requirements described in **THIRD-PARTY-NOTICES.md**.
- Review the files to be committed. The Discord application ID is public. Webhook URLs and GitHub tokens are secrets.

Repository access, secret storage and a successful remote workflow must be verified before claiming a release or channel notification is live.
