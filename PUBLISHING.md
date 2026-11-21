# Publishing Rewind

The public repository is **[rejzormax/Rewind](https://github.com/rejzormax/Rewind)**. This development folder is a local Git repository on `main`. Adding its remote does not upload anything; the reviewed source becomes public when you push it.

## Decide what people can see

**The selected approach is public application source and public Rewind logo files.** The application code uses MIT. The approved SVG, icon and PNG logo files are included in `assets/` so everyone can build with the R logo. Other assets and generated `src/Rewind/Resources/Logo.xaml` stay ignored. Logo files are public artwork; Git ignore rules exclude other files rather than encrypting them.

The local repository's ignore rules exclude SDKs, build outputs, logs, recordings, secrets and other assets. Use GitHub Desktop to publish the reviewed commits. **scripts/prepare-public-source.ps1** can also create a source-only ZIP with the approved logo files for a separate checkout.

**Public source** means anyone can read, clone and fork the source files you commit. It does not automatically upload the rest of your computer. Changing a public repository back to private does not erase copies or forks people already made.

Rewind currently has an MIT license. That license permits copying, modification, redistribution and commercial reuse while retaining the copyright and license notice. Keeping source private does not change the reuse permission already granted to any code you distribute under MIT. Choose the license you want before publishing; the current license has not been changed here. A restrictive license can limit permitted reuse, but cannot prevent someone from copying visible code. .NET binaries can also be decompiled, so a downloads-only release is not perfect protection against reverse engineering.

## Upload the source with GitHub Desktop

1. Open GitHub Desktop and sign in as **rejzormax** if needed.
2. Select **File → Add local repository**, choose this Rewind development folder, then select **Add repository**. Use the existing local repository.
3. Check **History** for the initial Rewind commit. The remote should be **https://github.com/rejzormax/Rewind.git** under **Repository → Repository settings → Remote**.
4. Select **Publish branch**. If the branch is already published, use **Push origin**.
5. Refresh the GitHub repository page. It should show `src`, `scripts`, `tests`, `.github`, the approved logo files in `assets`, README and the license. Tools, build outputs, recordings and secrets are excluded.

The first source push starts the build checks and Discord notification workflow. Later edits appear in Desktop's Changes list: review them, commit them with a short description, then push.

Website uploads do not apply `.gitignore`. If using the website instead of Desktop, upload only an extracted **Rewind-Public-Source.zip**, including `.github` and `.gitignore`.

## Discord channel and logo

Open **Settings → Secrets and variables → Actions** in the GitHub repository.

- **DISCORD_GITHUB_WEBHOOK** stores the existing channel webhook. Its value stays in GitHub's secret storage.

The logo is already included in the public files. Builds render it automatically and require no logo secret.

After uploading, use **Actions → Discord updates → Run workflow** to check the channel connection. Version releases announce directly from the release job after the downloadable files are published. See **RELEASE.md** before publishing a Windows download.

## Files to review before uploading source

The intended source files are in `src/`, `tests/`, `scripts/` and `.github/`, plus the approved logo files, root project configuration, README, license and third-party notices. `rewind-branding.json` contains Rewind's **public Discord application ID**, not a Discord account token. Only the named Rewind logo files are allowed through the assets ignore rule; the generated logo dictionary stays ignored.

`.gitignore` excludes development SDKs, build outputs, test artifacts, logs, environment files and signing keys. Never upload your recordings, `%LOCALAPPDATA%/Rewind` user settings or logs, Discord account/bot tokens, private keys or passwords. Ignore rules do not remove files that were already committed.

## The bundled FFmpeg executable

The Windows package includes a separate GPLv3 FFmpeg executable. Its license and matching build/source information are in `tools/LICENSE`, `tools/README.txt` and `THIRD-PARTY-NOTICES.md`. Before publicly distributing that executable, satisfy its corresponding-source obligations for the exact build you ship. Keep this requirement separate from the decision to publish Rewind's own application source.

## Official documentation

- Repository visibility: https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/managing-repository-settings/setting-repository-visibility
- Licensing: https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/licensing-a-repository
- Create a repository: https://docs.github.com/en/repositories/creating-and-managing-repositories/creating-a-new-repository
- Create releases: https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository
- GitHub Desktop: https://desktop.github.com/
