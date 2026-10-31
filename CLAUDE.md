# goggle-3-superview — DJI Goggles 3 USB viewer for the superflash bench

Fork of `knpuma/goggle-3-liveview` (MIT, by a friend of the owner). Repo: `leviqsupernova/goggle-3-superview`, branch `main`.
**Standalone fork, no merging with upstream** (decided 2026-10-31): we never pull from or send PRs to the friend's repo, so
unneeded code is deleted, not just ignored. Only `origin` is configured; to read their history add
`git remote add upstream https://github.com/knpuma/goggle-3-liveview.git` (fetch/diff only, never merge).
Development on Arch Linux, the app runs on **Windows** at the drone bench, launched by the superflash tool (`~/Desktop/superflash`, separate repo) in its own window.

## Scope

- **In scope:** the Windows single viewer: `Windows/Goggles3.Core`, `Windows/Goggles3.Windows`, `Windows/Goggles3.Core.Tests`,
  `Windows/Goggles3.Bench` (dev tool: fake goggles + soak, not shipped), `Windows/scripts/Publish.ps1`, `Windows/scripts/Prepare-Usb.ps1`.
- Goals: auto-connect, stable video for 8 h+, cleaner code, a better Windows UI, launch from superflash.

## Commands (Linux dev box, verified 2026-10-31)

```sh
dotnet test Windows/Goggles3.Core.Tests                         # all Core tests, ~5 s; recording tests need ffmpeg on PATH
dotnet test Windows/Goggles3.Core.Tests --filter RecordingTests # one class (focused run while iterating)
dotnet build Windows/Goggles3.Windows -c Release                # WPF builds on Linux (EnableWindowsTargeting), cannot run here
```
The whole suite is fast: run it before each commit. On Windows the tests use the bundled ffmpeg (cannot read MP4, so the
recording tests only check "saved, non-empty" unless a full ffmpeg is on PATH).
On Windows: `./Windows/scripts/Publish.ps1 -Runtime win-x64` -> `outputs/Goggle-3-LiveView-Windows-x64/`.
Replay without goggles: `"Goggle 3 LiveView.exe" --replay C:\path\capture.h264` (bypasses UDP + protocol).
Bench start: `"Goggle 3 LiveView.exe" --connect` (connect at launch, keep reconnecting, no dialogs; second launch focuses the window).
Test clip (live-like: no B-frames, ~26 Mbps): `ffmpeg -f lavfi -i testsrc2=size=1920x1080:rate=60 -t 20 -c:v libx264 -preset veryfast -b:v 30M -maxrate 30M -bufsize 10M -g 60 -bf 0 -f h264 clip.h264`.
Soak (Linux, fake goggles -> real Core pipeline, CSV per interval): `dotnet run -c Release --project Windows/Goggles3.Bench -- soak --file clip.h264 --minutes 480 --csv soak.csv [--record out.mp4]`.
Faults: `--loss %`, `--reorder %`, `--stall every:for [--resubscribe]`, `--closed s`. Slow decoder: `kill -STOP/-CONT $(pgrep -x ffmpeg)`
(never `pkill -f`: it matches the calling shell). Socket drops: `Udp` `RcvbufErrors` in `/proc/net/snmp`.
Needs .NET SDK 10 (`sudo pacman -S dotnet-sdk`). `TreatWarningsAsErrors` is on: fix warnings, never suppress them.

## How it works

- Goggles 3 = USB RNDIS network adapter, VID `2CA3` PID `0020`. `Prepare-Usb.ps1` (admin, once) adds `192.168.60.1/24`
  to that adapter only. Never WinUSB/Zadig.
- `UdpVideoReceiver`: local `192.168.60.1:12346` <-> goggles `192.168.60.2:9003`. Handshake once per second until
  video arrives, again after 2 s without video; ACK after the last part of each unit. ICMP unreachable is ignored.
- `LiveProtocol`: header (length, session id, XOR byte 7), type 2 = video part, reassembles units (max 32 cached) into
  H.264 Annex-B chunks.
- `FfmpegVideoSession`: UDP -> `H264Queue` (never blocks UDP; full = skip to next keyframe) -> `ffmpeg.exe` stdin;
  stdout = 1920x1080 BGRA -> `LatestFrame` triple buffer -> `Frame` event, UI calls `TakeLatestFrame()`.
  `Mp4Recorder`: second ffmpeg, copies the H.264 (`-c copy`) into a fragmented MP4 (playable even if killed).
- `AdapterLocator`: finds the adapter via registry PnP id + cfgmgr32 (Missing / NeedsSetup / Ready), runs `Prepare-Usb.ps1`
  elevated only after a Connect click. `AutoConnect` (Core): find -> session -> retry every 2 s, status text only.
- `App`: single instance (`Local\Goggles3SuperView` mutex + `.show` / `.connect` events).
- ffmpeg binaries in `Windows/third_party/ffmpeg-*` are LGPL builds: replacing one means updating
  `THIRD_PARTY_NOTICES.md`.

## Audit result (step 1) and fixes (step 3), details in `STEPLOG.md`

Fixed + measured on Linux: ICMP unreachable, re-handshake, decoder backpressure queue, H.264 copy recording in fragmented
MP4 (+ cached SPS/PPS), recorder lifecycle, triple-buffered frames, decoder starts at keyframe. Fixed, bench check pending
(WPF/PowerShell): close waits for save, Stop without video, logs off UI thread, `Prepare-Usb.ps1` prefix guard.
Open: #11 decoder restart -> step 4 auto-reconnect; #12/#13 plausible only; constant 60 fps recording timestamps.
Each fix needs a test or soak measurement that shows the problem first. "Fixed" means measured, not "looks right".

## CI

`.github/workflows/tests.yml`, on every PR and push to `main`: `dotnet test` + WPF Release build on `ubuntu-latest`
(apt ffmpeg) and `windows-latest` (bundled ffmpeg, real Windows UDP/ICMP behaviour). Check it with `gh pr checks <n>`.
CI does not cover: running the WPF UI, `Prepare-Usb.ps1`, real goggles. Those are bench checks (see Bench results).

## Working rules

- Bugs: reproduce first (test or fake goggles soak), then fix, then show the test passing.
- Keep existing safety checks (adapter scoping, address conflict refusal, `Prepare-Usb.ps1` restore).
- Codex: one review + one follow-up per plan step (the `codex-review:code` skill would loop up to 5 rounds: stop after
  the follow-up). Claude makes the final call and writes kept / rejected + why in `STEPLOG.md`.
- One branch + PR per plan step, small commits.
- **IMPORTANT:** never change network settings other than the DJI adapter, and never add flight commands or uploads.
- Do not commit captures (`*.h264`), `outputs/`, soak logs, or personal paths.
- After a decision or a bench result, update this file (Decisions / Bench results). Keep it under ~200 lines.
- Operator-facing text: short, plain English (UI strings and errors). The bench operator is not a developer.

## Plan (status: [ ] todo, [x] done)

- [x] 0. Setup: .NET SDK, build + `dotnet test` green on Arch, add `*.h264` to `.gitignore`.
- [x] 0b. Removal: Multi, macOS, Linux, ARM64 deleted (see Removal). App/exe rename still open.
- [x] 1. Audit: `ponytail-audit` + Codex review -> ranked list + verdicts in `STEPLOG.md`. Graphify indexes `main` (MCP `graphify`).
- [x] 2. Fake goggles (UDP sender speaking `LiveProtocol`, plays an H.264 file, packet loss/stall/reorder options)
      + soak harness (memory, GC, fps, reconnects, logged per minute). Built before the fixes.
- [x] 3. Stability fixes (STEPLOG step 1 list) + H.264 remux recording, each with a test or soak number.
- [ ] 4. `--connect` (auto-connect + auto-reconnect, also restarts after a decoder crash = finding #11), single instance (second launch focuses the window),
      superflash: menu key + exe path in its `config.ini`.
- [ ] 5. Structure + UI (skills: `impeccable`, `stop-slop`, `run` for screenshots). Library: WPF + WPF-UI (decided). UI can only be run and screenshotted on
      the Windows PC, so UI work is done in small batches and the owner sends screenshots/feedback; logic stays in `Core` (testable on Linux).
- [ ] 6. Soak: 8 h fake goggles on Linux (needs `Core` only), 8 h real goggles on the Windows bench.
- [ ] 7. Release zip, README. No PRs to `upstream`.
Model: Opus for steps 1-3 and structure decisions; Sonnet for 0, 4, 6, 7 and mechanical porting.

## Removal (done in step 0b)

Deleted: Multi, macOS, Linux, ARM64 ffmpeg, Mac scripts. Kept: `LICENSE` (keep the friend's copyright line),
`THIRD_PARTY_NOTICES.md`, root `third_party/ffmpeg` + `scripts/build-windows-decoder.sh` (LGPL source/rebuild),
`assets/AppIcon.{ico,png}`. Still open: `docs/` website (upstream marketing, owner decides).

## Open decisions

- App/exe name in this fork (upstream: "Goggle 3 LiveView").

## Superflash integration contract (step 4)

superflash starts `<exe> --connect` detached and does not wait. The viewer must: start connected or keep retrying,
never block on a dialog when the goggles are absent (status text instead), refuse a second instance.

## Decisions

- 2026-10-31: standalone fork (see top), single viewer only, Multi removed.
- 2026-10-31: UI = **WPF + WPF-UI** (Windows 11 Fluent look). Not Avalonia: `Linux/` is deleted, nothing is shared across OSes.
  Consequence: no UI testing on the Arch box. Keep all logic out of `MainWindow.xaml.cs` (move to `Core`) so Linux tests cover it.

## Bench results

- 2026-10-31 step 2 baseline (Linux, fake goggles, 1080p60 26 Mbps, before fixes): clean = 60 fps, ~490 MB/s allocated,
  ~6.7 Gen2 GC/s, 4.7 ms GC pause/s. Findings #1, #2, #6 reproduced; 0.5 % packet loss = ~30 % frames lost. See `STEPLOG.md`.
- 2026-10-31 step 3 (Linux soak): decoder pauses -> 0 kernel UDP drops (was 1587); 15 MB/s allocated (was 490), 0 Gen2/s;
  recorder killed mid-file -> still playable. Bench checks pending: close while saving, Stop without video, Prepare-Usb guard,
  recording length vs clock (constant 60 fps timestamps).
