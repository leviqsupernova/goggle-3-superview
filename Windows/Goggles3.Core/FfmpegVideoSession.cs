using System.Diagnostics;

namespace Goggles3.Core;

public sealed class FfmpegVideoSession : IAsyncDisposable
{
    public const int Width = 1920, Height = 1080, FrameSize = Width * Height * 4;
    private readonly string decoderPath;
    private readonly CancellationTokenSource lifetime = new();
    private Process? decoder;
    private Task? worker;
    private readonly object recordingLock = new();
    private Mp4Recorder? recorder;
    private Task saving = Task.CompletedTask;
    /// <summary>A new frame is ready: get it with <see cref="TakeLatestFrame"/> (any thread).</summary>
    public event Action? Frame;
    private readonly LatestFrame latest = new(FrameSize);
    public event Action<double>? Bitrate;
    public event Action<string>? Log;
    public event Action<string>? Ended;
    public event Action<RecordingResult>? RecordingEnded;

    public FfmpegVideoSession(string decoderPath) => this.decoderPath = decoderPath;

    /// <summary>Newest decoded BGRA frame, valid until the next call; null if none arrived since the last call.</summary>
    public byte[]? TakeLatestFrame() => latest.TakeLatest();

    public void Start(UdpVideoReceiver receiver)
    {
        receiver.Bitrate += rate => Bitrate?.Invoke(rate);
        receiver.Log += text => Log?.Invoke(text);
        Begin(async (feed, token) =>
        {
            // ~4 s at 60 units/s: rides out decoder hiccups without blocking UDP reception or ACKs.
            var queue = new H264Queue(240, text => Log?.Invoke(text));
            Task receiving = Task.Run(async () =>
            {
                try { await receiver.RunAsync((data, _) => { queue.Add(data); return ValueTask.CompletedTask; }, token).ConfigureAwait(false); }
                finally { queue.Complete(); }
            }, CancellationToken.None);
            await foreach (byte[] chunk in queue.ReadAllAsync(token).ConfigureAwait(false))
                await feed(chunk, token).ConfigureAwait(false);
            await receiving.ConfigureAwait(false); // surfaces a receiver failure
        });
    }

    // Explicit local-file test mode, never passed off as a live USB session.
    public void StartReplay(string path) => Begin(async (feed, token) =>
    {
        await using var file = File.OpenRead(path);
        while (true)
        {
            var buffer = new byte[65536]; // new array per chunk: the recorder keeps a reference
            int read = await file.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            await feed(read == buffer.Length ? buffer : buffer[..read], token).ConfigureAwait(false);
        }
    }, paced: true);

    /// <summary>The source hands each H.264 chunk to the feed: recorder (copy) and decoder stdin.</summary>
    private void Begin(Func<Func<byte[], CancellationToken, ValueTask>, CancellationToken, Task> source, bool paced = false)
    {
        if (worker != null) throw new InvalidOperationException("The session is already running.");
        decoder = CreateProcess(decoderPath, ["-hide_banner","-loglevel","error","-flags2","+showall",
            "-threads","2","-probesize","65536","-analyzeduration","0","-f","h264","-r","60","-i","pipe:0",
            "-an","-pix_fmt","bgra","-vf","scale=1920:1080","-fps_mode","passthrough","-f","rawvideo","pipe:1"]);
        decoder.Start();
        Process active = decoder;
        worker = Task.Run(async () =>
        {
            var errors = DrainErrorsAsync(active, text => Log?.Invoke(text));
            Exception? sourceFailure = null;
            var writer = Task.Run(async () =>
            {
                Stream stdin = active.StandardInput.BaseStream;
                async ValueTask Feed(byte[] chunk, CancellationToken token)
                {
                    lock (recordingLock) recorder?.Append(chunk);
                    await stdin.WriteAsync(chunk, token).ConfigureAwait(false);
                }
                try { await source(Feed, lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                catch (Exception error) { sourceFailure = error; Log?.Invoke("USB reception: " + error.Message); }
                finally { active.StandardInput.Close(); }
            });
            try
            {
                var watch = Stopwatch.StartNew(); long frames = 0;
                while (!lifetime.IsCancellationRequested)
                {
                    byte[] pixels = latest.Back;
                    int offset = 0;
                    while (offset < pixels.Length)
                    {
                        int read = await active.StandardOutput.BaseStream.ReadAsync(pixels.AsMemory(offset), lifetime.Token).ConfigureAwait(false);
                        if (read == 0) break;
                        offset += read;
                    }
                    if (offset != pixels.Length) break;
                    if (paced)
                    {
                        double delay = frames / 60.0 - watch.Elapsed.TotalSeconds;
                        if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay), lifetime.Token).ConfigureAwait(false);
                    }
                    frames++;
                    latest.Publish();
                    Frame?.Invoke();
                }
                await active.WaitForExitAsync(lifetime.Token).ConfigureAwait(false);
                if (sourceFailure != null) throw new IOException("USB reception: " + sourceFailure.Message, sourceFailure);
                if (active.ExitCode != 0) throw new IOException($"The decoder exited with code {active.ExitCode}.");
                Ended?.Invoke(paced ? "End of local test recording." : "The USB connection has closed.");
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception error) { Log?.Invoke(error.Message); Ended?.Invoke(error.Message); }
            finally
            {
                lifetime.Cancel();
                StopProcess(active);
                try { await writer.ConfigureAwait(false); }
                catch (Exception error) { if (error is not OperationCanceledException) Log?.Invoke(error.Message); }
                await errors.ConfigureAwait(false);
                await StopRecordingAsync().ConfigureAwait(false);
            }
        });
    }

    public void StartRecording(string path, int frameRate = 60)
    {
        lock (recordingLock)
        {
            if (recorder != null) throw new InvalidOperationException("A recording is already in progress.");
            if (!saving.IsCompleted) throw new InvalidOperationException("The last recording is still being saved.");
            Mp4Recorder? created = null;
            created = new Mp4Recorder(decoderPath, path, frameRate, result =>
            {
                // A recorder that failed on its own (disk full) must not block the next recording.
                lock (recordingLock) if (recorder == created) recorder = null;
                RecordingEnded?.Invoke(result);
            });
            recorder = created;
        }
    }

    /// <summary>Completes when the file is saved, also when called again while a save is running.</summary>
    public Task StopRecordingAsync()
    {
        lock (recordingLock)
        {
            if (recorder != null) { saving = recorder.FinishAsync(); recorder = null; }
            return saving;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (decoder != null) StopProcess(decoder);
        if (worker != null) await worker.ConfigureAwait(false);
        await StopRecordingAsync().ConfigureAwait(false);
        decoder?.Dispose(); lifetime.Dispose();
    }

    internal static Process CreateProcess(string path, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        return new Process { StartInfo = info };
    }

    internal static async Task DrainErrorsAsync(Process process, Action<string> log)
    {
        while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line) log(line);
    }

    internal static void StopProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* Already exited before Kill. */ }
        catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
    }
}

public sealed record RecordingResult(string Path, long Chunks, long Dropped, string? Error);

/// <summary>
/// Copies the goggles' H.264 into an MP4 without re-encoding, starting at the first keyframe.
/// Fragmented MP4: a killed app or ffmpeg still leaves a playable file up to the last keyframe.
/// </summary>
internal sealed class Mp4Recorder
{
    private readonly Process process;
    private readonly H264Queue pending = new(240);
    private readonly Task worker;
    private readonly CancellationTokenSource shutdown = new();
    private bool started;

    public Mp4Recorder(string executable, string path, int fps, Action<RecordingResult> finished)
    {
        // shortcut: raw H.264 has no timestamps, so frames get a constant -framerate; if the goggles stream
        // another rate the file plays at the wrong speed. Check one bench recording's length against a clock.
        process = FfmpegVideoSession.CreateProcess(executable, ["-hide_banner","-loglevel","error",
            "-f","h264","-framerate",fps.ToString(System.Globalization.CultureInfo.InvariantCulture),"-i","pipe:0",
            "-an","-c:v","copy","-movflags","+frag_keyframe+empty_moov+default_base_moof","-f","mp4",path]);
        process.Start();
        worker = Task.Run(async () =>
        {
            string? failure = null; long chunks = 0;
            var errors = FfmpegVideoSession.DrainErrorsAsync(process, line => failure = line);
            try
            {
                await foreach (byte[] chunk in pending.ReadAllAsync(shutdown.Token).ConfigureAwait(false))
                { await process.StandardInput.BaseStream.WriteAsync(chunk, shutdown.Token).ConfigureAwait(false); chunks++; }
                process.StandardInput.Close();
                await process.WaitForExitAsync(shutdown.Token).ConfigureAwait(false);
                if (process.ExitCode == 0 && chunks > 0) failure = null;
                else failure ??= "No video was received, or the recording could not be saved.";
            }
            catch (Exception error)
            {
                failure = error.Message; pending.Complete();
                FfmpegVideoSession.StopProcess(process);
            }
            finally
            {
                await errors.ConfigureAwait(false);
                finished(new RecordingResult(path, chunks, pending.Dropped, failure));
                process.Dispose(); shutdown.Dispose();
            }
        });
    }

    /// <summary>Called with every H.264 chunk, from the session's feed (single thread).</summary>
    public void Append(byte[] chunk)
    {
        if (!started)
        {
            int key = H264Queue.IndexOfKeyframe(chunk);
            if (key < 0) return; // an MP4 must start on a keyframe
            chunk = key == 0 ? chunk : chunk[key..];
            started = true;
        }
        pending.Add(chunk);
    }

    public Task FinishAsync()
    {
        // No +faststart: no second pass over the file, so a long recording finishes in seconds.
        if (pending.Complete()) shutdown.CancelAfter(TimeSpan.FromSeconds(30));
        return worker;
    }
}
