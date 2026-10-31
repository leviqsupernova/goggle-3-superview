using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Goggles3.Core;

namespace Goggles3.Bench;

/// <summary>
/// Fake goggles -> UdpVideoReceiver -> FfmpegVideoSession on loopback, one CSV row per interval.
/// Frames are only counted (no WPF copy). When a session ends the harness starts a new one after 1 s
/// and counts it in "ended": the shipped app does not reconnect on its own.
/// </summary>
public static class Soak
{
    private const string Header = "t_s,frames,fps,max_gap_ms,fake_frames,fake_handshakes,dropped_pkts,acks,decoder_lines,ended,gen0,gen1,gen2,gc_pause_ms,alloc_mb,heap_mb,ws_mb,cpu_pct";

    public static async Task<int> RunAsync(string ffmpeg, byte[] h264, TimeSpan duration, TimeSpan interval, FakeOptions fakeOptions, TextWriter output, string? record = null)
    {
        int port;
        using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        var goggles = new IPEndPoint(IPAddress.Loopback, port);
        using var stop = new CancellationTokenSource(duration);
        var fake = new FakeGoggles(fakeOptions);
        Task fakeTask = fake.RunAsync(h264, goggles, stop.Token);

        long frames = 0, lastFrameTicks = Stopwatch.GetTimestamp(), maxGapTicks = 0, decoderLines = 0, ended = 0;
        var sessions = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var finished = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                await using var session = new FfmpegVideoSession(ffmpeg);
                session.Frame += () =>
                {
                    long now = Stopwatch.GetTimestamp();
                    long gap = now - Interlocked.Exchange(ref lastFrameTicks, now);
                    if (gap > Interlocked.Read(ref maxGapTicks)) Interlocked.Exchange(ref maxGapTicks, gap);
                    Interlocked.Increment(ref frames);
                };
                session.Log += line => { Interlocked.Increment(ref decoderLines); Console.Error.WriteLine("decoder: " + line); };
                session.Ended += reason => finished.TrySetResult(reason);
                session.RecordingEnded += r => Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} recording saved: {r.Chunks} chunks, {r.Dropped} dropped, error: {r.Error ?? "none"}");
                session.Start(new UdpVideoReceiver(IPAddress.Loopback, goggles, 0));
                if (record != null && Interlocked.Read(ref ended) == 0) session.StartRecording(record); // first session only
                try
                {
                    string reason = await finished.Task.WaitAsync(stop.Token);
                    Interlocked.Increment(ref ended);
                    Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} session ended: {reason}");
                    await Task.Delay(1000, stop.Token);
                }
                catch (OperationCanceledException) { }
            }
        });

        output.WriteLine(Header); output.Flush();
        var clock = Stopwatch.StartNew();
        var self = Process.GetCurrentProcess();
        FakeStats lastFake = fake.Stats;
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        TimeSpan pause = GC.GetTotalPauseDuration(), cpu = self.TotalProcessorTime;
        long allocated = GC.GetTotalAllocatedBytes();
        double last = 0;
        while (!stop.IsCancellationRequested)
        {
            try { await Task.Delay(interval, stop.Token); } catch (OperationCanceledException) { }
            double now = clock.Elapsed.TotalSeconds, span = now - last;
            if (span < 0.5) break;
            self.Refresh();
            FakeStats f = fake.Stats;
            long n = Interlocked.Exchange(ref frames, 0);
            double gapMs = Interlocked.Exchange(ref maxGapTicks, 0) * 1000.0 / Stopwatch.Frequency;
            if (n == 0) gapMs = (Stopwatch.GetTimestamp() - Interlocked.Read(ref lastFrameTicks)) * 1000.0 / Stopwatch.Frequency;
            output.WriteLine(string.Join(',', [
                F(now, 0), n.ToString(CultureInfo.InvariantCulture), F(n / span, 1), F(gapMs, 0),
                (f.Frames - lastFake.Frames).ToString(CultureInfo.InvariantCulture), f.Handshakes.ToString(CultureInfo.InvariantCulture),
                (f.Dropped - lastFake.Dropped).ToString(CultureInfo.InvariantCulture), (f.Acks - lastFake.Acks).ToString(CultureInfo.InvariantCulture),
                Interlocked.Exchange(ref decoderLines, 0).ToString(CultureInfo.InvariantCulture), Interlocked.Read(ref ended).ToString(CultureInfo.InvariantCulture),
                (GC.CollectionCount(0) - g0).ToString(CultureInfo.InvariantCulture), (GC.CollectionCount(1) - g1).ToString(CultureInfo.InvariantCulture),
                (GC.CollectionCount(2) - g2).ToString(CultureInfo.InvariantCulture), F((GC.GetTotalPauseDuration() - pause).TotalMilliseconds, 0),
                F((GC.GetTotalAllocatedBytes() - allocated) / 1048576.0, 0), F(GC.GetTotalMemory(false) / 1048576.0, 0),
                F(self.WorkingSet64 / 1048576.0, 0), F((self.TotalProcessorTime - cpu).TotalSeconds / span * 100, 0)]));
            output.Flush();
            lastFake = f; last = now;
            g0 = GC.CollectionCount(0); g1 = GC.CollectionCount(1); g2 = GC.CollectionCount(2);
            pause = GC.GetTotalPauseDuration(); cpu = self.TotalProcessorTime; allocated = GC.GetTotalAllocatedBytes();
        }
        await sessions;
        try { await fakeTask; } catch (OperationCanceledException) { }
        return 0;
    }

    private static string F(double value, int digits) => value.ToString("F" + digits, CultureInfo.InvariantCulture);
}
