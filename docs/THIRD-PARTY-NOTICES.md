# Third-party notices

Rewind's application code uses MIT. Its Windows setup and update package include the Microsoft .NET 10 runtime. The runtime packages' license and third-party notices are included in the download's `licenses` folder.

Rewind uses FFmpeg as a separate recording process. Setup downloads Gyan Doshi's FFmpeg 9.1 essentials build directly from the publisher's official `GyanD/codexffmpeg` GitHub release and checks it against the pinned publisher SHA-256 checksum. The engine, its GPLv3 license and matching source/build information are cached in `%LOCALAPPDATA%/Rewind/engine`. The public Rewind installer and update ZIP do not redistribute that executable.

If you explicitly build a package with `-IncludeCaptureEngine`, you are redistributing that FFmpeg build and must provide its corresponding source and licensing information for the exact executable. The normal public release avoids bundling it.

- FFmpeg source and legal information: https://ffmpeg.org/legal.html
- FFmpeg build and exact source revision information: https://www.gyan.dev/ffmpeg/builds/
- .NET license: https://github.com/dotnet/runtime/blob/main/LICENSE.TXT
- Inno Setup installer license: https://jrsoftware.org/inno-setup

Discord Rich Presence uses the local Discord IPC protocol. Rewind is not affiliated with Discord.
