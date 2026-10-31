# goggle-3-superview — DJI Goggles 3 USB viewer for the superflash bench

Fork of `knpuma/goggle-3-liveview` (MIT, by a friend of the owner). Repo: `leviqsupernova/goggle-3-superview`, branch `main`.
**Standalone fork, no merging with upstream** (decided 2026-10-31): we never pull from or send PRs to the friend's repo, so
unneeded code is deleted, not just ignored. Keep the `upstream` remote (`https://github.com/knpuma/goggle-3-liveview.git`)
only to read their history (`git fetch upstream`, `git diff`); never `git merge upstream/...`. Development on Arch Linux, the app runs on **Windows** at the
drone bench, launched by the superflash tool (`~/Desktop/superflash`, separate repo) in its own window.

## Scope

- **In scope:** the Windows single viewer: `Windows/Goggles3.Core`, `Windows/Goggles3.Windows`, `Windows/Goggles3.Core.Tests`,
  `Windows/scripts/Publish.ps1`, `Windows/scripts/Prepare-Usb.ps1`.
- Goals: auto-connect, stable video for 8 h+, cleaner code, a better Windows UI, launch from superflash.

## Commands (Linux dev box)

```sh
dotnet test Windows/Goggles3.Core.Tests                         # protocol + UDP tests, run cross-platform
dotnet build Windows/Goggles3.Windows -c Release                # WPF builds on Linux (EnableWindowsTargeting), cannot run here
```
On Windows: `./Windows/scripts/Publish.ps1 -Runtime win-x64` -> `outputs/Goggle-3-LiveView-Windows-x64/`.
Replay without goggles: `"Goggle 3 LiveView.exe" --replay C:\path\capture.h264` (bypasses UDP + protocol).
Test clip: `ffmpeg -f lavfi -i testsrc2=size=1920x1080:rate=60 -t 10 -c:v libx264 -f h264 /tmp/test.h264`.
Needs .NET SDK 10 (`sudo pacman -S dotnet-sdk`). `TreatWarningsAsErrors` is on: fix warnings, never suppress them.

## How it works

- Goggles 3 = USB RNDIS network adapter, VID `2CA3` PID `0020`. `Prepare-Usb.ps1` (admin, once) adds `192.168.60.1/24`
  to that adapter only. Never WinUSB/Zadig.
- `UdpVideoReceiver`: local `192.168.60.1:12346` <-> goggles `192.168.60.2:9003`. Handshake once per second until
  video arrives, ACK after the last part of each unit.
- `LiveProtocol`: header (length, session id, XOR byte 7), type 2 = video part, reassembles units (max 32 cached) into
  H.264 Annex-B chunks.
- `FfmpegVideoSession`: H.264 -> `ffmpeg.exe` stdin; stdout = 1920x1080 BGRA raw frames -> WPF `WriteableBitmap`.
  `Mp4Recorder`: second ffmpeg, re-encodes BGRA to MPEG-4.
- `AdapterLocator`: finds the adapter via registry PnP id + cfgmgr32, runs `Prepare-Usb.ps1` elevated if needed.
- ffmpeg binaries in `Windows/third_party/ffmpeg-*` are LGPL builds: replacing one means updating
  `THIRD_PARTY_NOTICES.md`.

## Audit result (step 1, details + verdicts in `STEPLOG.md`)

Must fix in step 3: (1) `WSAECONNRESET` ends the session, (2) no re-handshake after the stream stops,
(3) `+faststart` + 30 s kill corrupts long recordings, (4) failed recorder blocks new recordings,
(5) closing while saving truncates the MP4, (6) UDP loop blocked by ffmpeg stdin. Then: 8 MB/frame GC, `AddLog` on UI
thread, Stop disabled when video drops, H.264 remux recording, decoder auto-restart, `Prepare-Usb.ps1` restore guard.
Each fix needs a test or soak measurement that shows the problem first. "Fixed" means measured, not "looks right".

## Working rules

- Plan before editing anything beyond a one-file change; state which files change and how it is verified.
- Bugs: reproduce first (test or fake goggles), then fix, then show the test passing.
- Smallest change that fully works. No new dependency for a few lines. Keep existing safety checks
  (adapter scoping, address conflict refusal, `Prepare-Usb.ps1` restore).
- Codex reviews findings; Claude makes the final call and writes the verdict (kept / rejected + why) in the step log.
- One branch + PR per plan step, small commits. Run `dotnet test` before every commit.
- **IMPORTANT:** never change network settings other than the DJI adapter, and never add flight commands or uploads.
- Do not commit captures (`*.h264`), `outputs/`, soak logs, or personal paths.
- After a decision or a bench result, update this file (Decisions / Bench results). Keep it under ~200 lines.
- Operator-facing text: short, plain English (UI strings and errors). The bench operator is not a developer.

## Plan (status: [ ] todo, [x] done)

- [x] 0. Setup: .NET SDK, build + `dotnet test` green on Arch, add `*.h264` to `.gitignore`.
- [x] 0b. Removal (own commit, `dotnet test` + build green after): delete Multi per the Removal list; update README.md,
      this file, and rename the app/exe if decided. Fixes from Multi to port in step 3 are listed under Removal list.
- [x] 1. Audit: `ponytail-audit` + Codex review -> ranked list + verdicts in `STEPLOG.md`. Graphify: repo not indexed (owner adds it).
- [ ] 2. Fake goggles (UDP sender speaking `LiveProtocol`, plays an H.264 file, packet loss/stall/reorder options)
      + soak harness (memory, GC, fps, reconnects, logged per minute). Built before the fixes.
- [ ] 3. Stability fixes (STEPLOG step 1 list) + H.264 remux recording + decoder auto-restart, each with a test or soak number.
- [ ] 4. `--connect` (auto-connect + auto-reconnect), single instance (second launch focuses the window),
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
Port from Multi in step 3: catch the per-read cancellation in the UDP loop + `ThrowIfCancellationRequested` at the end;
ffmpeg `-filter_threads 1` / `-threads 1` (measure first). Skip `IP_UNICAST_IF` (only for several goggles).

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

(none yet)
