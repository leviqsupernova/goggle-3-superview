namespace Goggles3.Core;

/// <summary>
/// Triple buffer for decoded frames: the decoder fills <see cref="Back"/> and publishes it, the UI takes the
/// newest published frame. Three fixed buffers replace an 8 MB allocation per frame, the decoder never waits,
/// and the buffer returned by <see cref="TakeLatest"/> stays untouched until the next take.
/// </summary>
public sealed class LatestFrame(int size)
{
    private readonly object gate = new();
    private byte[] back = new byte[size], ready = new byte[size], front = new byte[size];
    private bool fresh;

    /// <summary>Decoder side: the buffer to fill next. Changes after every <see cref="Publish"/>.</summary>
    public byte[] Back => back;

    public void Publish() { lock (gate) { (back, ready) = (ready, back); fresh = true; } }

    /// <summary>Reader side: the newest frame, or null if none arrived since the last take.</summary>
    public byte[]? TakeLatest()
    {
        lock (gate)
        {
            if (!fresh) return null;
            (front, ready) = (ready, front); fresh = false;
            return front;
        }
    }
}
