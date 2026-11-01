# Third-party notices

## FFmpeg 8.0.3

Copyright (c) 2000–2026 the FFmpeg developers.

Goggle 3 SuperView runs a separately built Windows x64 FFmpeg executable (`ffmpeg/ffmpeg.exe`), without GPL or non-free components. License: **GNU Lesser General Public License, version 2.1 or later**. The license text is in `ffmpeg/COPYING.LGPLv2.1` in the download and in `third_party/ffmpeg/COPYING.LGPLv2.1` in the source.

- Complete original source: https://ffmpeg.org/releases/ffmpeg-8.0.3.tar.xz
- SHA-256: `6136812ea6d4e68bdba27e33c2a94382711cdf4f8602ffef056ff792bd6f9818`
- Build: `scripts/build-windows-decoder.sh` with llvm-mingw 20261006. The FFmpeg source is unmodified; components are selected through build configuration. No external libraries.
- The decoder is a separate executable and can be replaced by another build. Replacing it means updating this file.

## Windows app

The app uses **.NET 10 and WPF**, open-source software from Microsoft and contributors. The runtime is bundled through `dotnet publish --self-contained`. Notices are in `Licenses/DotNET-THIRD-PARTY-NOTICES.txt`, `Licenses/DotNET-LICENSE.txt` and `Licenses/WPF-LICENSE.txt`. Sources: https://github.com/dotnet/runtime and https://github.com/dotnet/wpf.

The UI uses **WPF UI 4.3.0** (MIT), Copyright (C) 2021-2026 Leszek Pomianowski and WPF UI Contributors. License and notices: `Licenses/WPF-UI-LICENSE.md` and `Licenses/WPF-UI-THIRD-PARTY-NOTICES.txt`. Source: https://github.com/lepoco/wpfui.

No proprietary DJI code is redistributed.

## Research references, not bundled dependencies

- https://github.com/samuelsadok/dji_protocol/blob/master/udp_protocol.md describes earlier DJI UDP protocol fields. Compatibility was investigated on the developer’s own Goggles 3; no repository code was incorporated.
- https://github.com/wllm-rbnt/third-eye documents Goggles 3 and a network path distinct from its mobile transport. Its code is not included.
- https://github.com/planktonwhc/Goggles3-USB-Livestream was observed as a reference for USB video. Its proprietary software is not redistributed.
- https://github.com/morluto/rea was used locally for analysis. REA is not part of this app or required to run it.
