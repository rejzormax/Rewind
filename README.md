# Rewind

A lightweight Windows replay recorder with a local clip library, Discord Rich Presence and GitHub release updates.

## Record a clip

Open **Rewind.exe**, select a display in Capture settings and start the replay buffer. Press **Ctrl + Shift + F9** to save the last **15, 30, 60 or 120 seconds**. Clips default to **Videos/Rewind**.

Closing the window keeps Rewind in the tray. Pause stops capture and clears temporary history. Quit from the tray to exit completely. Recording starts only when you arm the buffer.

## Features

- GPU capture with Intel Quick Sync, NVIDIA NVENC or AMD AMF.
- CPU fallback capped at 30 FPS, 1280 px width and two encoder threads.
- A bounded disk history and stream-copy MP4 exports.
- Native system audio and optional microphone audio, mixed into stereo AAC.
- A virtualized clip library with search, date filters, sorting and imports.
- Automatic Discord presence using Rewind's shared application identity.
- GitHub release checks and a separate **auto-updater.exe** installer.

The interface is native WPF. It has no browser runtime, live preview or continuous resource-usage polling.

## Discord presence

Presence connects when the Discord desktop client is running. Users can turn it off in Rewind; no ID, token or login is required in the app.

The public project identity in **rewind-branding.json** uses application **1573632058495078501**, details **Clipping Games** and state **https://discord.gg/msQgnj3nBD**. Elapsed time starts at process launch and survives capture pauses and reconnections.

## Updates

Rewind checks completed public releases from **rejzormax/Rewind** after startup and every six hours. It verifies GitHub's digest, rejects unsafe archives and waits until recording is idle before installation. Automatic updates can be disabled in Capture settings.

Updates install into a version folder under **%LOCALAPPDATA%/Rewind/updates**. Settings and recordings are separate. Original portable launchers redirect to the latest installed version.

Source pushes run checks and notify the configured Discord channel. A matching version tag publishes a release after tests pass. See **RELEASE.md** for the release process and required repository secrets.

## Build

```powershell
.\scripts\setup.ps1
.\scripts\build.ps1 -OfficialRelease
.\scripts\verify.ps1
.\scripts\package.ps1
```

Output is **dist/Rewind**. Source builds require the .NET 10 SDK. The default portable build requires the .NET 10 Windows Desktop runtime; **build.ps1 -SelfContained** makes a larger standalone package.

Setup downloads verified tools into **.tools/**. The application has no NuGet package dependencies. Tests cover replay exports, Discord IPC, imports and update verification.

## Public source and branding

Application source uses MIT. Original **assets/** and the generated private logo dictionary are ignored by Git. Public checkouts use a generic fallback mark. Official release branding can come from a repository secret. Original brand assets are separate from the code license; see **BRANDING.md**. Assets bundled in an executable remain extractable.

Use **scripts/prepare-public-source.ps1** for a source-only ZIP, or commit this repository normally. **PUBLISHING.md** explains visibility and licensing.

## Limits and data

Rewind records a monitor. Per-game capture, editing, separate audio tracks, HDR preservation and cloud uploads are not implemented. Exclusive fullscreen, sleep or device changes may require restarting capture. Use borderless fullscreen when needed. Windows microphone access must already be enabled.

Settings, bounded logs and temporary history live in **%LOCALAPPDATA%/Rewind**. Clips default to **Videos/Rewind**. The library indexes the newest 300 MP4s. Recording data stays local; Discord sends only the configured activity text.

Performance depends on hardware, drivers and settings. Test the intended gaming machine before release.

## License

Application source: **MIT**. The separate FFmpeg executable uses **GPLv3**. See **THIRD-PARTY-NOTICES.md** and its bundled license for distribution requirements. FFmpeg runs as a separate process.
