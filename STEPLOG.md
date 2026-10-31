# Step log

One section per plan step: what was found, verdicts (kept / rejected + why), measurements.

## Step 1: Audit (2026-10-31)

Sources: own read of all of `Windows/` (~1000 lines, ponytail-audit rules) and a Codex review
(gpt-6.1-sol, xhigh, read-only, whole files). Graphify: not used, the repo is not indexed in the Graphify workspace
(owner action: add `leviqsupernova/goggle-3-superview` in the Graphify app).

Load assumed: one viewer, one pair of goggles, 1080p60 H.264, running unattended for 8 h+.
"Kept" = real per the code; still needs a failing test or soak number before the step 3 fix (working rules).

### Must fix (step 3 unless noted)

| # | Finding | Where | Found by | Verdict |
|---|---|---|---|---|
| 1 | ICMP port unreachable (handshake before LiveView is on) throws `WSAECONNRESET` out of `ReceiveAsync`; the session ends with a raw socket error. Fix: `SIO_UDP_CONNRESET` off. | `UdpVideoReceiver.cs:43` | both | **kept** |
| 2 | `started` never resets: after the stream stops (LiveView toggled, goggles restart) no new handshake, "Waiting for video" forever. Fix: re-handshake after N s without video, reset protocol state. | `UdpVideoReceiver.cs:35` | both | **kept** |
| 3 | Stop recording: `+faststart` rewrites the whole MP4, `FinishAsync` kills ffmpeg after 30 s. A long recording on a slow disk ends corrupt (no moov). Fix: drop `+faststart` (or no kill deadline). | `FfmpegVideoSession.cs:162,195` | both | **kept** (data loss) |
| 4 | Encoder dies mid-recording (disk full): UI resets, but `recorder` stays set, every new Record says "already in progress" until reconnect. Fix: clear `recorder` when its worker ends. | `FfmpegVideoSession.cs:106` | both | **kept** |
| 5 | Closing during "Saving…": `StopRecordingAsync` already nulled `recorder`, `DisposeAsync` does not wait for it; a second Close/Alt-F4 skips `e.Cancel`. App exits mid-finalize -> truncated MP4. Fix: keep the finish task, await it on dispose; cancel every close until cleanup is done. | `FfmpegVideoSession.cs:113`, `MainWindow.xaml.cs:224` | Codex | **kept** (merged #10 + #12) |
| 6 | UDP loop awaits ffmpeg stdin per unit: a slow decoder stops receiving and ACKs, the 2 MB socket buffer overflows, H.264 breaks; `-flags2 +showall` then shows the broken frames. Fix: bounded queue between UDP and pipe, drop to next IDR on overflow. | `UdpVideoReceiver.cs:52`, `FfmpegVideoSession.cs:28` | both | **kept**, measure first (fake goggles + slow decoder) |

### Should fix

| # | Finding | Where | Found by | Verdict |
|---|---|---|---|---|
| 7 | `new byte[8 294 400]` per decoded frame = ~500 MB/s on the large object heap -> Gen2 GCs that stall pipe reading and UDP. Fix: small buffer pool, ownership until UI + recorder are done. | `FfmpegVideoSession.cs:64` | both | **kept**, measure GC count/pause in soak first |
| 8 | Every ffmpeg stderr line -> `BeginInvoke` -> `File.WriteAllLines` on the UI thread. An error burst (packet loss) freezes the UI and queues unbounded dispatcher work. Fix: queue + timed write off the UI thread. | `MainWindow.xaml.cs:239` | both | **kept** |
| 9 | Video lost > 3 s while recording disables the Record/Stop button: the operator cannot stop the recording. Fix: keep Stop enabled while `recording && !saving`. | `MainWindow.xaml.cs:155` | Codex | **kept** |
| 10 | Recording re-encodes decoded BGRA (third 1080p60 CPU job) with wallclock timestamps; after a stall, bursts share a timestamp and VFR drops them (Codex measured 21/32 frames kept, ffmpeg 9.0.2). Fix: remux the original H.264 (`-c copy`), as already planned. | `FfmpegVideoSession.cs:158-165` | both | **kept**, replaced by remux |
| 11 | No decoder auto-restart: ffmpeg crash or exit ends the session. | `FfmpegVideoSession.cs:81-84` | plan | **kept** |
| 12 | Complete units are emitted in completion order, not sequence order (`A.p0, B, A.p1` -> `B, A`). | `LiveProtocol.cs:74` | Codex | **kept as plausible**: USB RNDIS rarely reorders. Reproduce with the fake goggles reorder option; fix only if seen (in-order release adds latency). |
| 13 | ffmpeg sends 8 MB BGRA per frame through an anonymous pipe (~500 MB/s, small pipe buffer = many syscalls). Likely the main decode-side CPU cost. | `FfmpegVideoSession.cs:43` | own | **kept as plausible**, measure ffmpeg + app CPU before changing |
| 14 | `Prepare-Usb.ps1`: an existing `192.168.60.1/16` passes the guard, backup says `Added=true`, create fails, a later `-Restore` deletes the user's own address. Fix: refuse 192.168.60.1 with another prefix before writing the backup. | `Prepare-Usb.ps1:30` | Codex | **kept** (safety check, one line) |
| 15 | Right after setup the new address can still be tentative (DAD); first Connect may fail to bind. | `Prepare-Usb.ps1:41`, `AdapterLocator.cs:34` | Codex | **kept, low**: step 4 auto-retry covers it; no extra code unless seen on the bench |
| 16 | `UdpReceiver_..._AndCancelsCleanly` test is timing-dependent: cancel between loop checks returns normally, test expects an exception. Fix: `ThrowIfCancellationRequested()` after the loop (also the Multi port item). | `UdpVideoReceiver.cs:57`, `ProtocolTests.cs:91` | Codex | **kept** |
| 17 | Missing adapter shows a modal `MessageBox`: blocks the superflash `--connect` contract. | `MainWindow.xaml.cs:63` | own | **kept**, step 4 |

### Nice to have

| # | Finding | Verdict |
|---|---|---|
| 18 | Per datagram: new linked CTS + 250 ms timer, `ToArray()` per part, `UdpClient` byte[] per receive. | kept as soak metric, fix only if the soak shows it |
| 19 | `Goggles3.Check` (dev replay + record check, Spanish strings) overlaps the step 2 soak harness. | decide in step 2: reuse or delete |

Rejected: none. Codex's claims were each checked against the code; #12 and #13 are downgraded to "plausible" because their
impact depends on hardware behaviour not visible in the code.
Ported from Multi (still to do in step 3): refuse a new recording while the last one is saving (UI already guards via `saving`;
#4/#5 cover the session side), `-threads 1` / `-filter_threads 1` (measure against #6/#13 first).

## Step 2: Fake goggles + soak harness (2026-10-31)

`Windows/Goggles3.Bench` (was `Goggles3.Check`, finding #19: reused, not deleted): `check` = old replay + record check,
`soak` = `FakeGoggles` (goggles side of the UDP session, plays an H.264 file one access unit per frame, faults: loss,
reorder, stall, stall + resubscribe, port closed at start) -> `UdpVideoReceiver` -> `FfmpegVideoSession` with system ffmpeg,
one CSV row per interval (fps, max frame gap, fake frames/handshakes/drops/ACKs, decoder lines, sessions ended, GC gen0/1/2,
GC pause, allocated MB, heap, working set, CPU). When a session ends the harness restarts it after 1 s (the shipped app
does not). Tests: AU splitter, packetizer through `LiveProtocol` across the 16-bit wrap, fake -> receiver byte-for-byte (13/13 green).
Limits: the fake's unit size and timing are guesses (real goggles not captured); frame counts do not detect corrupt pictures;
no WPF copy in the frame handler.

Baseline before fixes (Arch, 16 cores, ffmpeg 9.0.2, 20 s testsrc2 clip 1080p60 ~26 Mbps, no B-frames, looped):

| Run | Result | Finding |
|---|---|---|
| clean, 90 s | 60.0 fps, max gap 20 ms, 0 sessions ended; per 15 s: ~7340 MB allocated (~490 MB/s), 100 Gen2 GCs (~6.7/s), ~71 ms GC pause; app CPU ~52 % of one core | #7 measured: allocation rate confirmed; pause small on this box, recheck on the bench PC |
| `--closed 3` | 3 sessions ended with `USB reception: Connection refused` (Linux form of `WSAECONNRESET`), ffmpeg also logs `Error opening output file pipe:1`; video only after harness restart | #1 **reproduced** |
| `--stall 15:3 --resubscribe` | after the stall: 0 frames for 45 s, handshakes stay at 1 (viewer never re-subscribes) | #2 **reproduced** |
| 1 s decoder pause x3 (`kill -STOP`) | 1587 UDP datagrams dropped by the kernel (`RcvbufErrors`), ACKs stop during each pause (575/600), 1 s frame gaps | #6 **reproduced** |
| `--loss 0.5` | ~42 fps (~30 % frames lost; one lost part drops the whole unit), gaps up to 450 ms, ffmpeg logs nothing, ACK still sent for units with missing parts | new note: ACK on last part only, decoder silent |
| `--reorder 1` | 60 fps, max gap 35 ms, no decoder errors | #12: no visible effect at frame-count level; stays "plausible", needs a picture check to judge |
| slow decoder (`taskset -c 0`) | still 60 fps on this CPU | #6 needs the pause test, not just one core |

## Step 3: Stability fixes (2026-10-31)

Each fix: failing test or soak number first, then the fix. Soak = `Goggles3.Bench soak`, same 20 s 1080p60 ~26 Mbps clip as step 2.

| # | Fix | Proof (before -> after) |
|---|---|---|
| 1 | Receiver catches `ConnectionReset`/`ConnectionRefused` on receive and send; handshake continues | test `Receiver_KeepsHandshaking_WhenGogglesPortIsClosedAtFirst`: red (`SocketException: Connection refused`) -> green |
| 2 | No video for 2 s (`ResubscribeAfter`): reset `LiveProtocol` cache, handshake again | test `Receiver_SubscribesAgain_...`: red with the re-subscribe disabled (checked by patching it out), green with it |
| 16 | `ThrowIfCancellationRequested` after the receive loop | existing cancel test now deterministic |
| 6 | `H264Queue` (240 chunks) between UDP and ffmpeg stdin; full -> drop to next SPS/IDR, give up waiting after 240 drops | soak, 3 x 1 s decoder pause: kernel UDP drops 1587 -> 0, ACKs 575/600 -> 600/600, no frames lost; 5 s pause: one resync, session survives. 3 queue unit tests |
| 10 | Recorder copies the original H.264 (`-c copy`, `-framerate 60`) from the first keyframe | test `Recording_CopiesTheOriginalH264_EveryFrame`: red (`mpeg4`) -> green (h264, 120/120 frames) |
| 3 | Fragmented MP4 (`+frag_keyframe+empty_moov+default_base_moof`), no `+faststart` | `kill -9` of the recorder mid-file: plain MP4 "moov atom not found" (0 frames) vs fragmented 541/545 frames readable |
| 4 | Recorder clears itself when it ends on its own | test `StartRecording_Works_AfterTheEncoderFailedMidSession`: red ("already in progress") -> green |
| 5 | `StopRecordingAsync` returns the running save, `DisposeAsync` awaits it; window cancels every Close until cleanup is done; refuse Record while saving (Multi port) | no deterministic failing test (race); fragmented MP4 limits the damage anyway. UI part: bench check |
| 7 | `LatestFrame` triple buffer, `Frame` event + `TakeLatestFrame()` | soak per 15 s: allocated 7344 -> 233 MB, Gen2 100 -> 0, GC pause 70 -> 5 ms, app CPU 52 -> 40 %, working set 216 -> 160 MB. 2 unit tests |
| 8 | Log lines go to a `ConcurrentQueue`; the 1 s timer moves them to the list + file | WPF: build only, bench check |
| 9 | Stop stays enabled while recording when video drops | WPF: build only, bench check |
| 14 | `Prepare-Usb.ps1` refuses 192.168.60.1 with another prefix before writing the backup | no pwsh/NetTCPIP on Linux: read-checked only, bench check |
| new | Decoder queue starts at a keyframe (`startAtKeyframe`) | soak, `kill -9` ffmpeg + session restart: decoder log lines 208 -> 4 |

Not done in step 3, with verdict:
- #11 decoder auto-restart: **moved to step 4**. Soak shows a full session restart after `kill -9` of ffmpeg recovers 60 fps within ~1.2 s,
  so the step 4 auto-reconnect (restart on `Ended`) covers it; no second restart path inside the session.
- #12 out-of-order units: unchanged, still plausible only (no effect at frame-count level, see step 2).
- #13 BGRA pipe: measured ffmpeg ~70 % of one core (decode + scale + BGRA) on this box; no change until the bench PC shows lag.
- #15 tentative address after setup: step 4 retry covers it.
- Multi port `-threads 1` / `-filter_threads 1`: **rejected for now**: no decoder lag measured; revisit with bench PC numbers.
- Recording timestamps: constant 60 fps (`shortcut:` comment in `Mp4Recorder`). Bench check: compare one recording's length to a clock.

### Step 3 Codex review, round 1 (verdict: REVISE) and Claude's verdicts

| Codex | Point | Verdict + proof |
|---|---|---|
| 1 CRITICAL | `frag_keyframe` without IDRs buffers the whole recording in memory | **kept**: one-IDR stream, 3 MB in: 28 bytes on disk -> 2.6 MB with `-frag_duration 1000000` |
| 2 CRITICAL | Disconnect, then Close while saving: second `DisconnectAsync` returns at once | **kept**: one shared disconnect task (WPF, bench check) |
| 3 WARNING | Recording test reads the MP4 with the bundled Windows ffmpeg | **kept**, worse than reported: the bundled build has no mov demuxer and no framecrc (`strings`: demuxers h264+rawvideo only). Probe uses a system ffmpeg from PATH; without one only "saved, non-empty" is checked |
| 4 WARNING | Goggles that send SPS/PPS once: a later recording starts at a bare IDR and fails | **kept**: `ParameterSets` caches the newest SPS/PPS, recorder prepends them. Test red without the cache ("Could not write header") -> green, 90/90 frames |
| 5 WARNING | Keyframe wait gives up after `capacity` drops (startup and recorder) | **rejected**: deliberate, so goggles with rare or no IDRs (intra refresh) never freeze forever; recorder drops are counted in `Dropped` |
| 6 WARNING | Recorder fed after the decoder queue: a decoder stall cuts the recording | **kept**: recorder fed in the receiver callback. Soak `--record`, decoder paused 5 s: decoder resynced, MP4 has 1796/1796 frames, 0 dropped |
| 7 WARNING | Failed feed returns before the receiver ends; reconnect may hit the still-bound UDP port | **kept**: `finally` awaits the receiver |
| 8 WARNING | `FlushLog` still writes the file on the UI thread | **kept**: write in `Task.Run`, one at a time, dirty flag (WPF, bench check) |
