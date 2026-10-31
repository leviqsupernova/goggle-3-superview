namespace Goggles3.Core;

/// <summary>
/// Newest SPS and PPS seen in the stream. Goggles may send them only once; a recording that starts
/// later at a bare IDR needs them in front or ffmpeg fails with "non-existing PPS".
/// Used from the single thread that feeds the stream.
/// </summary>
public sealed class ParameterSets
{
    private byte[]? sps, pps;
    private byte[]? partial; // SPS/PPS cut off by the end of the last chunk
    private int partialType;

    public void Observe(byte[] chunk)
    {
        int begin = -1, type = 0;
        for (int i = 0; i + 3 < chunk.Length; i++)
        {
            if (chunk[i] != 0 || chunk[i + 1] != 0 || chunk[i + 2] != 1) continue;
            int next = i > 0 && chunk[i - 1] == 0 ? i - 1 : i;
            if (begin >= 0) Keep(type, chunk[begin..next]);
            else if (partial != null) { Keep(partialType, [.. partial, .. chunk[..next]]); partial = null; }
            begin = next; type = chunk[i + 3] & 0x1f; i += 2;
        }
        // shortcut: a start code split across two chunks is not detected (2 bytes in a whole chunk; the next SPS fixes it).
        if (begin < 0) { if (partial != null) partial = partial.Length < 4096 ? [.. partial, .. chunk] : null; }
        else if (type is 7 or 8) { partial = chunk[begin..]; partialType = type; }
    }

    private void Keep(int type, byte[] nal)
    {
        if (type == 7) sps = nal;
        else if (type == 8) pps = nal;
    }

    /// <summary>The chunk, with the cached SPS and/or PPS in front when it lacks its own.</summary>
    public byte[] Complete(byte[] chunk)
    {
        bool needPps = pps != null && !Has(chunk, 8);
        // A PPS needs its SPS parsed first, so a missing PPS brings the cached SPS along (the chunk's own SPS then follows).
        bool needSps = sps != null && (needPps || !Has(chunk, 7));
        return !needSps && !needPps ? chunk : [.. needSps ? sps! : [], .. needPps ? pps! : [], .. chunk];
    }

    private static bool Has(ReadOnlySpan<byte> chunk, int nalType)
    {
        for (int i = 0; i + 3 < chunk.Length; i++)
            if (chunk[i] == 0 && chunk[i + 1] == 0 && chunk[i + 2] == 1 && (chunk[i + 3] & 0x1f) == nalType) return true;
        return false;
    }
}
