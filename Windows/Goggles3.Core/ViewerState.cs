namespace Goggles3.Core;

public enum ViewerLight { Off, Waiting, Live, Problem }

/// <summary>What the window shows: <see cref="Badge"/> always (top right), <see cref="Title"/> and <see cref="Hint"/> while there is no video.</summary>
public sealed record ViewerStatus(string Badge, string Title, string Hint, ViewerLight Light)
{
    public static readonly ViewerStatus NotConnected = new("Not connected", "Plug in the goggles, then click Connect", "Use a USB-C data cable. Tutorial shows the goggles settings.", ViewerLight.Off);
    public static readonly ViewerStatus WaitingForGoggles = new("Waiting for goggles", "Plug in the goggles", "Use a USB-C data cable. The viewer connects by itself.", ViewerLight.Waiting);
    public static readonly ViewerStatus NeedsSetup = new("Setup needed", "The USB connection is not set up", "Click Set up. Windows asks for admin permission once.", ViewerLight.Problem);
    public static readonly ViewerStatus Connecting = new("Connecting…", "Connecting to the goggles", "", ViewerLight.Waiting);
    public static readonly ViewerStatus NoVideo = new("No video", "No video from the goggles", "Turn on LiveView sharing on the goggles. Tutorial shows how.", ViewerLight.Waiting);
    public static readonly ViewerStatus Live = new("Live", "", "", ViewerLight.Live);
    public static readonly ViewerStatus Replay = new("Test video, not live", "", "", ViewerLight.Live);
    public static readonly ViewerStatus SetupCancelled = new("Not connected", "Setup cancelled", "Click Connect to try again.", ViewerLight.Off);

    /// <summary>A session ended; the auto-connect loop starts a new one.</summary>
    public static ViewerStatus Reconnecting(string reason) => new("Reconnecting…", reason, "Reconnecting by itself.", ViewerLight.Problem);
    /// <summary>A device or decoder problem; the auto-connect loop keeps trying.</summary>
    public static ViewerStatus Retrying(string problem) => new("Problem", problem, "Trying again by itself.", ViewerLight.Problem);
    /// <summary>Stopped for good (replay ended, connect failed); the operator decides what next.</summary>
    public static ViewerStatus Stopped(string reason) => new("Stopped", reason, "", ViewerLight.Off);
}

/// <summary>
/// Decides what the window shows, so the badge, the idle text and the buttons cannot contradict each other.
/// Times are seconds on one monotonic clock. UI thread only.
/// </summary>
public sealed class ViewerState
{
    /// <summary>No frame for this long = video lost.</summary>
    public const double VideoTimeout = 3;

    private bool session, replay;
    private double sessionStart, lastFrame = double.NegativeInfinity;
    private int frames;

    /// <summary>Set by the auto-connect loop, Connect/Disconnect and replay; shown whenever a session has no video yet.</summary>
    public ViewerStatus Connection { get; set; } = ViewerStatus.NotConnected;

    public void SessionStarted(double now, bool replay)
    {
        session = true; this.replay = replay; sessionStart = now; lastFrame = double.NegativeInfinity; frames = 0;
    }

    public void SessionStopped() { session = false; frames = 0; }

    public void FrameShown(double now) { lastFrame = now; frames++; }

    public bool HasVideo(double now) => session && now - lastFrame <= VideoTimeout;

    public ViewerStatus Status(double now) =>
        HasVideo(now) ? (replay ? ViewerStatus.Replay : ViewerStatus.Live)
        : session && now - sessionStart > VideoTimeout ? ViewerStatus.NoVideo
        : Connection;

    /// <summary>Frames shown since the last call (called once per second = fps).</summary>
    public int TakeFrameCount() { int count = frames; frames = 0; return count; }
}
