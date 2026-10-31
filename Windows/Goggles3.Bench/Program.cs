using System.Globalization;
using System.Text.Json;
using Goggles3.Bench;
using Goggles3.Core;

// Developer bench tool, not shipped. Runs on Linux and Windows (Core only, no WPF).
const string usage = """
    Goggles3.Bench soak  --file <clip.h264> [--ffmpeg ffmpeg] [--minutes 1] [--interval 60] [--csv out.csv] [--record out.mp4] [--restart-every seconds]
                         [--loss %] [--reorder %] [--stall every:for] [--resubscribe] [--closed seconds]
                         [--fps 60] [--part-size 1400] [--seed 1]
    Goggles3.Bench check <ffmpeg> <clip.h264> <out.mp4>    replay + MP4 recording check (exit 0 = ok)
    """;
if (args.Length == 0) { Console.Error.WriteLine(usage); return 2; }
if (args[0] == "check" && args.Length == 4) return await Check(args[1], args[2], args[3]);
if (args[0] != "soak") { Console.Error.WriteLine(usage); return 2; }

var named = new Dictionary<string, string>();
for (int i = 1; i < args.Length; i++)
{
    if (!args[i].StartsWith("--", StringComparison.Ordinal)) { Console.Error.WriteLine($"Unexpected argument: {args[i]}\n{usage}"); return 2; }
    bool flag = i + 1 == args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal);
    named[args[i][2..]] = flag ? "true" : args[++i];
}
double Number(string key, double fallback) => named.TryGetValue(key, out string? v) ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;
if (!named.TryGetValue("file", out string? file)) { Console.Error.WriteLine(usage); return 2; }
string[] stall = named.TryGetValue("stall", out string? s) ? s.Split(':') : ["0", "0"];
var options = new FakeOptions
{
    Fps = Number("fps", 60), PartSize = (int)Number("part-size", 1400), Seed = (int)Number("seed", 1),
    LossPercent = Number("loss", 0), ReorderPercent = Number("reorder", 0),
    StallEvery = double.Parse(stall[0], CultureInfo.InvariantCulture), StallFor = double.Parse(stall[^1], CultureInfo.InvariantCulture),
    ResubscribeAfterStall = named.ContainsKey("resubscribe"), ClosedFor = Number("closed", 0),
};
using TextWriter output = named.TryGetValue("csv", out string? csv) ? new TeeWriter(File.CreateText(csv)) : new TeeWriter(null);
return await Soak.RunAsync(named.GetValueOrDefault("ffmpeg", "ffmpeg"), File.ReadAllBytes(file),
    TimeSpan.FromMinutes(Number("minutes", 1)), TimeSpan.FromSeconds(Number("interval", 60)), options, output, named.GetValueOrDefault("record"),
    named.ContainsKey("restart-every") ? TimeSpan.FromSeconds(Number("restart-every", 0)) : null);

static async Task<int> Check(string ffmpeg, string clip, string mp4)
{
    var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var recorded = new TaskCompletionSource<RecordingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    int frames = 0;
    await using var session = new FfmpegVideoSession(ffmpeg);
    session.Log += text => Console.Error.WriteLine(text);
    session.Frame += () =>
    {
        if (session.TakeLatestFrame() is { Length: not FfmpegVideoSession.FrameSize }) throw new InvalidDataException("Unexpected frame size.");
        Interlocked.Increment(ref frames);
    };
    session.Ended += _ => done.TrySetResult();
    session.RecordingEnded += result => recorded.TrySetResult(result);
    session.StartRecording(mp4); session.StartReplay(clip);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    await done.Task.WaitAsync(deadline.Token);
    RecordingResult recording = await recorded.Task.WaitAsync(deadline.Token);
    Console.WriteLine(JsonSerializer.Serialize(new { frames, recording }, new JsonSerializerOptions { WriteIndented = true }));
    return frames > 0 && recording.Chunks > 0 && recording.Error == null ? 0 : 1;
}

/// <summary>Writes CSV rows to the console and, if given, a file.</summary>
sealed class TeeWriter(StreamWriter? file) : TextWriter
{
    public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
    public override void Write(char value) { Console.Out.Write(value); file?.Write(value); }
    public override void WriteLine(string? value) { Console.Out.WriteLine(value); file?.WriteLine(value); }
    public override void Flush() { Console.Out.Flush(); file?.Flush(); }
    protected override void Dispose(bool disposing) { if (disposing) file?.Dispose(); base.Dispose(disposing); }
}
