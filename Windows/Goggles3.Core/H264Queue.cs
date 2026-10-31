using System.Threading.Channels;

namespace Goggles3.Core;

/// <summary>
/// Holds H.264 chunks between UDP reception and the decoder so a slow decoder never stalls
/// reception or ACKs. When full, chunks are dropped until the next keyframe (SPS or IDR), or until
/// a capacity of drops has passed, for goggles that rarely send one.
/// Single writer (the receiver loop), single reader (the decoder pipe).
/// </summary>
public sealed class H264Queue(int capacity, Action<string>? log = null)
{
    private readonly Channel<byte[]> channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private int resyncDrops = -1; // -1 = not resyncing
    private long dropped;

    public long Dropped => Interlocked.Read(ref dropped);

    public void Add(byte[] chunk)
    {
        if (resyncDrops >= 0 && resyncDrops < capacity && !ContainsKeyframe(chunk)) { Drop(); return; }
        if (channel.Writer.TryWrite(chunk)) { resyncDrops = -1; return; }
        if (resyncDrops < 0) log?.Invoke("The video decoder fell behind. Skipping to the next keyframe.");
        resyncDrops = 0; Drop();
    }

    private void Drop() { resyncDrops++; Interlocked.Increment(ref dropped); }

    public IAsyncEnumerable<byte[]> ReadAllAsync(CancellationToken token) => channel.Reader.ReadAllAsync(token);

    public bool Complete() => channel.Writer.TryComplete();

    /// <summary>True when the chunk holds an SPS (7) or IDR slice (5) NAL unit.</summary>
    public static bool ContainsKeyframe(ReadOnlySpan<byte> chunk) => IndexOfKeyframe(chunk) >= 0;

    /// <summary>Offset of the start code of the first SPS or IDR NAL unit, or -1.</summary>
    public static int IndexOfKeyframe(ReadOnlySpan<byte> chunk)
    {
        for (int i = 0; i + 3 < chunk.Length; i++)
            if (chunk[i] == 0 && chunk[i + 1] == 0 && chunk[i + 2] == 1 && (chunk[i + 3] & 0x1f) is 5 or 7)
                return i > 0 && chunk[i - 1] == 0 ? i - 1 : i;
        return -1;
    }
}
