using System.Diagnostics;
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

    private static async Task<(string Codec, int Packets)> ProbeAsync(string mp4)
    {
        var info = new ProcessStartInfo(Ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-hide_banner", "-i", mp4, "-map", "0:v", "-c", "copy", "-f", "framecrc", "-" }) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
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
            (string codec, int packets) = await ProbeAsync(mp4);
            Assert.Equal("h264", codec);
            Assert.Equal(120, packets);
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
