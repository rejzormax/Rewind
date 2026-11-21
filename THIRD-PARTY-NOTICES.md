# Third-party notices

Rewind's application source is licensed under MIT. It uses the installed Microsoft .NET 10 Windows Desktop runtime.

The optional bundled FFmpeg executable is a separate program distributed by Gyan Doshi. That build is licensed under GPL version 3. Its bundled LICENSE file and readme are copied into the portable distribution's tools folder. FFmpeg is invoked as a separate process; Rewind does not link its libraries.

- FFmpeg project and source: https://ffmpeg.org/
- Exact source revisions and build configuration: https://www.gyan.dev/ffmpeg/builds/
- FFmpeg source repository: https://github.com/FFmpeg/FFmpeg
- FFmpeg legal information: https://ffmpeg.org/legal.html
- .NET license: https://github.com/dotnet/runtime/blob/main/LICENSE.TXT

Redistributors are responsible for satisfying the FFmpeg build's GPL obligations, including providing corresponding source for the specific executable they redistribute. The setup script retains the build's versioned documentation and checks downloaded binaries against the publisher's checksum. For a source-only distribution, omit tools/ffmpeg.exe and let users install FFmpeg themselves.

Discord Rich Presence uses Discord's documented local IPC protocol. Rewind is not affiliated with Discord.
