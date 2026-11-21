# Publishing Rewind

The public repository is **[rejzormax/Rewind](https://github.com/rejzormax/Rewind)**. This development folder is a local Git repository on `main`. Adding its remote does not upload anything; the reviewed source becomes public when you push it.

## Decide what people can see

**The selected approach is public source with private branding.** The application code uses MIT. Original `assets/` and generated `src/Rewind/Resources/Logo.xaml` are excluded from Git. Contributors can build with the generic public fallback mark. Your locally built Windows release keeps your private R logo. Any assets bundled in the downloaded executable can still be extracted; Git ignore rules do not encrypt them.

The local repository's ignore rules already exclude SDKs, build outputs, logs, recordings, secrets and original branding. Use GitHub Desktop to publish the reviewed commit. **scripts/prepare-public-source.ps1** can also create a source-only ZIP for a separate checkout.

**Public source** means anyone can read, clone and fork the source files you commit. It does not automatically upload the rest of your computer. Changing a public repository back to private does not erase copies or forks people already made.

Rewind currently has an MIT license. That license permits copying, modification, redistribution and commercial reuse while retaining the copyright and license notice. Keeping source private does not change the reuse permission already granted to any code you distribute under MIT. Choose the license you want before publishing; the current license has not been changed here. A restrictive license can limit permitted reuse, but cannot prevent someone from copying visible code. .NET binaries can also be decompiled, so a downloads-only release is not perfect protection against reverse engineering.

## Upload the source with GitHub Desktop

1. Open GitHub Desktop and sign in as **rejzormax** if needed.
2. Select **File → Add local repository**, choose this Rewind development folder, then select **Add repository**. Use the existing local repository.
3. Check **History** for the initial Rewind commit. The remote should be **https://github.com/rejzormax/Rewind.git** under **Repository → Repository settings → Remote**.
4. Select **Publish branch**. If the branch is already published, use **Push origin**.
5. Refresh the GitHub repository page. It should show `src`, `scripts`, `tests`, `.github`, README and the license, with original `assets`, `.tools` and `dist` absent.

The first source push starts the build checks and Discord notification workflow. Later edits appear in Desktop's Changes list: review them, commit them with a short description, then push.

Website uploads do not apply `.gitignore`. If using the website instead of Desktop, upload only an extracted **Rewind-Public-Source.zip**, including `.github` and `.gitignore`.

## Discord channel and release logo

Open **Settings → Secrets and variables → Actions** in the GitHub repository.

- **DISCORD_GITHUB_WEBHOOK** stores the existing channel webhook. Its value stays in GitHub's secret storage.
- **REWIND_LOGO_SVG** can store the contents of the private `assets/rewind.svg`. Automated releases then use the R logo while its source stays excluded from Git. Without this secret, builds use the public fallback mark.

After uploading, use **Actions → Discord updates → Run workflow** to check the channel connection. Version releases announce directly from the release job after the downloadable files are published. See **RELEASE.md** before publishing a Windows download.

## Files to review before uploading source

The intended source files are in `src/`, `tests/`, `scripts/` and `.github/`, plus the root project configuration, README, license and third-party notices. `rewind-branding.json` contains Rewind's **public Discord application ID**, not a Discord account token. The original `assets/` directory and generated logo dictionary are private; the generic fallback dictionary is public.

`.gitignore` excludes development SDKs, build outputs, test artifacts, logs, environment files and signing keys. Never upload your recordings, `%LOCALAPPDATA%/Rewind` user settings or logs, Discord account/bot tokens, private keys or passwords. Ignore rules do not remove files that were already committed.

## The bundled FFmpeg executable

The Windows package includes a separate GPLv3 FFmpeg executable. Its license and matching build/source information are in `tools/LICENSE`, `tools/README.txt` and `THIRD-PARTY-NOTICES.md`. Before publicly distributing that executable, satisfy its corresponding-source obligations for the exact build you ship. Keep this requirement separate from the decision to publish Rewind's own application source.

## Official documentation

- Repository visibility: https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/managing-repository-settings/setting-repository-visibility
- Licensing: https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/licensing-a-repository
- Create a repository: https://docs.github.com/en/repositories/creating-and-managing-repositories/creating-a-new-repository
- Create releases: https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository
- GitHub Desktop: https://desktop.github.com/
