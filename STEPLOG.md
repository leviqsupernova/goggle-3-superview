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
