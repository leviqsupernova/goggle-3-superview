using System.Diagnostics;
using Goggles3.Bench;
using Goggles3.Core;
using Xunit;

namespace Goggles3.Core.Tests;

/// <summary>Step 3 findings #10 (remux) and #4 (a failed recorder blocks the next one). Needs ffmpeg.</summary>
public sealed class RecordingTests
{
    private const string Clip = "testdata/testsrc-320x180-60fps-2s.264"; // 120 frames, synthetic testsrc2

    /// <summary>System ffmpeg on Linux, the bundled LGPL build on Windows.</summary>
    internal static string Ffmpeg
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return "ffmpeg";
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "third_party", "ffmpeg-x64", "ffmpeg.exe");
                if (File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException("Windows/third_party/ffmpeg-x64/ffmpeg.exe not found.");
        }
    }

    /// <summary>
    /// Codec and packet count via a full ffmpeg on PATH. The bundled Windows build cannot read MP4
    /// (no mov demuxer, no framecrc muxer): without a system ffmpeg only "saved, non-empty" is checked.
    /// </summary>
    private static async Task<(string Codec, int Packets)?> ProbeAsync(string mp4)
    {
        Assert.True(new FileInfo(mp4).Length > 0, "Recording is empty.");
        var info = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-hide_banner", "-i", mp4, "-map", "0:v", "-c", "copy", "-f", "framecrc", "-" }) info.ArgumentList.Add(a);
        Process process;
        try { process = Process.Start(info)!; }
        catch (System.ComponentModel.Win32Exception) when (OperatingSystem.IsWindows()) { return null; }
        using var _ = process;
        Task<string> errors = process.StandardError.ReadToEndAsync();
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        string header = await errors;
        string codec = header.Contains("Video: h264", StringComparison.Ordinal) ? "h264" : header.Contains("Video: mpeg4", StringComparison.Ordinal) ? "mpeg4" : "?";
        return (codec, output.Split('\n').Count(line => line.Length > 0 && line[0] != '#'));
    }

    private static string TempMp4() => Path.Combine(Path.GetTempPath(), $"goggles-test-{Guid.NewGuid():N}.mp4");

    [Fact] public async Task Recording_CopiesTheOriginalH264_EveryFrame()
    {
        string mp4 = TempMp4();
        try
        {
            var ended = new TaskCompletionSource<RecordingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (var session = new FfmpegVideoSession(Ffmpeg))
            {
                session.RecordingEnded += result => ended.TrySetResult(result);
                session.StartRecording(mp4);
                session.StartReplay(Clip);
                RecordingResult result = await ended.Task.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.Null(result.Error);
            }
            if (await ProbeAsync(mp4) is (string codec, int packets))
            {
                Assert.Equal("h264", codec);
                Assert.Equal(120, packets);
            }
        }
        finally { File.Delete(mp4); }
    }

    [Fact] public async Task Recording_StartingAtABareIdr_GetsTheCachedSpsAndPps()
    {
        // Goggles that send SPS/PPS once: a recording started later begins at an IDR without them.
        List<byte[]> units = FakeGoggles.SplitAccessUnits(File.ReadAllBytes(Clip)).Select(u => u.ToArray()).ToList();
        static byte[] WithoutHeaders(byte[] unit) // keep from the IDR slice on: drops the SPS/PPS in front of it
        {
            for (int i = 0; i + 3 < unit.Length; i++)
                if (unit[i] == 0 && unit[i + 1] == 0 && unit[i + 2] == 1 && (unit[i + 3] & 0x1f) == 5) return unit[i..];
            return unit;
        }
        var headers = new ParameterSets();
        headers.Observe(units[0]); // the first GOP's SPS/PPS, seen live before Record was clicked
        string mp4 = TempMp4();
        try
        {
            var ended = new TaskCompletionSource<RecordingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var recorder = new Mp4Recorder(Ffmpeg, mp4, 60, result => ended.TrySetResult(result), headers);
            foreach (byte[] unit in units.Skip(30)) recorder.Append(WithoutHeaders(unit)); // starts at the 2nd IDR
            await recorder.FinishAsync();
            RecordingResult result = await ended.Task;
            Assert.Null(result.Error);
            if (await ProbeAsync(mp4) is (string codec, int packets))
            {
                Assert.Equal("h264", codec);
                Assert.Equal(90, packets);
            }
        }
        finally { File.Delete(mp4); }
    }

    [Fact] public async Task StartRecording_Works_AfterTheEncoderFailedMidSession()
    {
        string bad = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing-folder", "x.mp4");
        string good = TempMp4();
        try
        {
            var ended = new TaskCompletionSource<RecordingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var session = new FfmpegVideoSession(Ffmpeg);
            session.RecordingEnded += result => ended.TrySetResult(result);
            session.StartRecording(bad);
            session.StartReplay(Clip); // 2 s paced: still running when the bad recording fails
            RecordingResult failed = await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(failed.Error);
            session.StartRecording(good); // threw "already in progress" before the fix
        }
        finally { File.Delete(good); }
    }
}
