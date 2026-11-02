using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Goggles3.Core;

/// <summary>
/// I420 (yuv420p) to BGRA32, BT.709 limited range (the goggles tag their stream "bt709, tv").
/// The decoder outputs I420 and this converts it: an 8 MB BGRA frame through ffmpeg's stdout pipe cost ffmpeg
/// 31 ms CPU and 21 ms wall per 1080p frame on the bench PC, I420 17 ms and 13 ms (bench 2026-11-02).
/// Chroma is upsampled bilinearly for H.264's default siting (sample on the even column, halfway between two rows):
/// a row takes 3/4 of its own chroma row and 1/4 of the nearer other one, odd columns the mean of two neighbours.
/// Copying the nearest sample (before 2026-11-02) left 2-pixel colour steps on edges and coloured OSD text.
/// SIMD and scalar paths give identical bytes (same integer rounding).
/// </summary>
public static class Yuv420
{
    // 10-bit fixed point: 1.164384, 1.792741, 0.213249, 0.532909, 2.112402 times 1024.
    private const int Cy = 1192, Crv = 1836, Cgu = 218, Cgv = 546, Cbu = 2163, Round = 512;

    public static int Size(int width, int height) => width * height * 3 / 2;

    public static void ToBgra(ReadOnlySpan<byte> i420, Span<byte> bgra, int width, int height) =>
        Convert(i420, bgra, width, height, Avx2.IsSupported);

    internal static unsafe void Convert(ReadOnlySpan<byte> i420, Span<byte> bgra, int width, int height, bool simd)
    {
        if (width <= 0 || height <= 0 || width % 2 != 0 || height % 2 != 0) throw new ArgumentException("Width and height must be even.");
        if (i420.Length < Size(width, height) || bgra.Length < width * height * 4) throw new ArgumentException("Buffer too small.");
        int chroma = width / 2, chromaRows = height / 2;
        // Padding: the SIMD horizontal pass reads 17 bytes past a half row and writes 32 past a full row.
        byte[] rows = new byte[2 * (chroma + 32) + 2 * (width + 64)];
        fixed (byte* source = i420)
        fixed (byte* target = bgra)
        fixed (byte* buffers = rows)
        {
            byte* uPlane = source + width * height, vPlane = uPlane + chroma * chromaRows;
            byte* uHalf = buffers, vHalf = uHalf + chroma + 32, uFull = vHalf + chroma + 32, vFull = uFull + width + 64;
            for (int y = 0; y < height; y++)
            {
                int own = y / 2, other = Math.Clamp(y % 2 == 0 ? own - 1 : own + 1, 0, chromaRows - 1);
                Upsample(uPlane + own * chroma, uPlane + other * chroma, uHalf, uFull, chroma, simd);
                Upsample(vPlane + own * chroma, vPlane + other * chroma, vHalf, vFull, chroma, simd);
                byte* yRow = source + y * width;
                uint* outRow = (uint*)(target + (long)y * width * 4);
                int x = simd ? Row8(yRow, uFull, vFull, outRow, width) : 0;
                for (; x < width; x++) outRow[x] = Pixel(yRow[x], uFull[x], vFull[x]);
            }
        }
    }

    private static int Average(int a, int b) => (a + b + 1) >> 1;

    /// <summary>One full-width chroma row for a luma row: vertical (3 own + 1 other + 2) / 4, then horizontal means.</summary>
    private static unsafe void Upsample(byte* own, byte* other, byte* half, byte* full, int chroma, bool simd)
    {
        int k = 0;
        if (simd)
        {
            Vector256<short> two = Vector256.Create((short)2);
            for (; k + 16 <= chroma; k += 16)
            {
                Vector256<short> a = Avx2.ConvertToVector256Int16(own + k), b = Avx2.ConvertToVector256Int16(other + k);
                Vector256<short> sum = Avx2.Add(Avx2.Add(Avx2.ShiftLeftLogical(a, 1), a), Avx2.Add(b, two));
                Vector256<byte> packed = Avx2.PackUnsignedSaturate(Avx2.ShiftRightLogical(sum, 2), Vector256<short>.Zero);
                Sse2.Store(half + k, Avx2.Permute4x64(packed.AsUInt64(), 0b1000).AsByte().GetLower()); // lanes 0, 2
            }
        }
        for (; k < chroma; k++) half[k] = (byte)((3 * own[k] + other[k] + 2) >> 2);
        for (int pad = 0; pad < 32; pad++) half[chroma + pad] = half[chroma - 1]; // edge repeats the last sample

        k = 0;
        if (simd)
            for (; k < chroma; k += 16)
            {
                Vector128<byte> a = Sse2.LoadVector128(half + k), mean = Sse2.Average(a, Sse2.LoadVector128(half + k + 1));
                Sse2.Store(full + 2 * k, Sse2.UnpackLow(a, mean));
                Sse2.Store(full + 2 * k + 16, Sse2.UnpackHigh(a, mean));
            }
        else
            for (; k < chroma; k++) { full[2 * k] = half[k]; full[2 * k + 1] = (byte)Average(half[k], half[k + 1]); }
    }

    private static uint Pixel(int y, int u, int v)
    {
        int luma = (y - 16) * Cy;
        u -= 128; v -= 128;
        int r = Clamp((luma + Crv * v + Round) >> 10);
        int g = Clamp((luma - Cgu * u - Cgv * v + Round) >> 10);
        int b = Clamp((luma + Cbu * u + Round) >> 10);
        return (uint)(b | g << 8 | r << 16) | 0xff000000;
    }

    private static int Clamp(int value) => value < 0 ? 0 : value > 255 ? 255 : value;

    /// <summary>8 pixels per step from full-width chroma rows; returns the first x it did not convert.</summary>
    private static unsafe int Row8(byte* yRow, byte* uRow, byte* vRow, uint* outRow, int width)
    {
        Vector256<int> c128 = Vector256.Create(128), c16 = Vector256.Create(16), round = Vector256.Create(Round);
        Vector256<int> cy = Vector256.Create(Cy), crv = Vector256.Create(Crv), cgu = Vector256.Create(Cgu), cgv = Vector256.Create(Cgv), cbu = Vector256.Create(Cbu);
        Vector256<int> zero = Vector256<int>.Zero, max = Vector256.Create(255), alpha = Vector256.Create(unchecked((int)0xff000000));
        int x = 0;
        for (; x + 8 <= width; x += 8)
        {
            Vector256<int> u = Avx2.Subtract(Avx2.ConvertToVector256Int32(uRow + x), c128);
            Vector256<int> v = Avx2.Subtract(Avx2.ConvertToVector256Int32(vRow + x), c128);
            Vector256<int> luma = Avx2.MultiplyLow(Avx2.Subtract(Avx2.ConvertToVector256Int32(yRow + x), c16), cy);
            Vector256<int> r = Channel(luma, Avx2.Add(Avx2.MultiplyLow(v, crv), round), zero, max);
            Vector256<int> g = Channel(luma, Avx2.Subtract(round, Avx2.Add(Avx2.MultiplyLow(u, cgu), Avx2.MultiplyLow(v, cgv))), zero, max);
            Vector256<int> b = Channel(luma, Avx2.Add(Avx2.MultiplyLow(u, cbu), round), zero, max);
            Avx.Store((int*)(outRow + x), Avx2.Or(Avx2.Or(b, Avx2.ShiftLeftLogical(g, 8)), Avx2.Or(Avx2.ShiftLeftLogical(r, 16), alpha)));
        }
        return x;
    }

    private static Vector256<int> Channel(Vector256<int> luma, Vector256<int> chroma, Vector256<int> zero, Vector256<int> max) =>
        Avx2.Min(Avx2.Max(Avx2.ShiftRightArithmetic(Avx2.Add(luma, chroma), 10), zero), max);
}
