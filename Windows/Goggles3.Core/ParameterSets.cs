namespace Goggles3.Core;

/// <summary>
/// Newest SPS and PPS seen in the stream. Goggles may send them only once; a recording that starts
/// later at a bare IDR needs them in front or ffmpeg fails with "non-existing PPS".
/// Used from the single thread that feeds the stream.
/// </summary>
public sealed class ParameterSets
{
    private byte[]? sps, pps;

    public void Observe(byte[] chunk)
    {
        int begin = -1, type = 0;
        for (int i = 0; i + 3 < chunk.Length; i++)
        {
            if (chunk[i] != 0 || chunk[i + 1] != 0 || chunk[i + 2] != 1) continue;
            int next = i > 0 && chunk[i - 1] == 0 ? i - 1 : i;
            Keep(chunk, begin, next, type);
            begin = next; type = chunk[i + 3] & 0x1f; i += 2;
        }
        Keep(chunk, begin, chunk.Length, type);
    }

    private void Keep(byte[] chunk, int begin, int end, int type)
    {
        if (begin < 0) return;
        if (type == 7) sps = chunk[begin..end];
        else if (type == 8) pps = chunk[begin..end];
    }

    /// <summary>The chunk, with the cached SPS/PPS in front when it has no SPS of its own.</summary>
    public byte[] Complete(byte[] chunk)
    {
        if (sps == null || pps == null || HasSps(chunk)) return chunk;
        return [.. sps, .. pps, .. chunk];
    }

    private static bool HasSps(ReadOnlySpan<byte> chunk)
    {
        for (int i = 0; i + 3 < chunk.Length; i++)
            if (chunk[i] == 0 && chunk[i + 1] == 0 && chunk[i + 2] == 1 && (chunk[i + 3] & 0x1f) == 7) return true;
        return false;
    }
}
