# Bundled FFmpeg runtime

Place a Windows x64 FFmpeg runtime in this directory before publishing Gluj Drive.

Preferred redistribution layout:

- `ffmpeg.exe`
- every FFmpeg DLL shipped beside `ffmpeg.exe` by the same LGPL shared build
- the build's license/copyright files

Gluj Drive does not invoke `ffprobe.exe` or `ffplay.exe`, so those executables may be omitted.
Do not mix an executable and DLLs from different builds or versions.

The server copies this directory into build and publish output and selects
`runtime/ffmpeg/win-x64/ffmpeg.exe` automatically. When it is absent, development
builds fall back to an `ffmpeg` command available on `PATH`.

Before distributing the application, comply with the exact FFmpeg build's license.
Prefer a build configured without `--enable-gpl` and `--enable-nonfree`, ship its
license notices, and make the corresponding source and build configuration available.
