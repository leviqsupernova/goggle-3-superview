using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Goggles3.Core;

/// <summary>
/// I420 (yuv420p) to BGRA32, BT.709 limited range (the goggles tag their stream "bt709, tv").
/// The decoder outputs I420 and this converts it: an 8 MB BGRA frame through ffmpeg's stdout pipe cost ffmpeg
/// 31 ms CPU and 21 ms wall per 1080p frame on the bench PC, I420 17 ms and 13 ms (bench 2026-11-02).
/// AVX2 path and scalar path give identical bytes.
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
        int chroma = width / 2;
        fixed (byte* source = i420)
        fixed (byte* target = bgra)
        {
            byte* uPlane = source + width * height, vPlane = uPlane + chroma * (height / 2);
            for (int y = 0; y < height; y++)
            {
                byte* yRow = source + y * width, uRow = uPlane + y / 2 * chroma, vRow = vPlane + y / 2 * chroma;
                uint* outRow = (uint*)(target + (long)y * width * 4);
                int x = simd ? Row16(yRow, uRow, vRow, outRow, width) : 0;
                for (; x < width; x++) outRow[x] = Pixel(yRow[x], uRow[x / 2], vRow[x / 2]);
            }
        }
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

    /// <summary>16 pixels per step; returns the first x it did not convert.</summary>
    private static unsafe int Row16(byte* yRow, byte* uRow, byte* vRow, uint* outRow, int width)
    {
        Vector256<int> lowHalf = Vector256.Create(0, 0, 1, 1, 2, 2, 3, 3), highHalf = Vector256.Create(4, 4, 5, 5, 6, 6, 7, 7);
        Vector256<int> c128 = Vector256.Create(128), c16 = Vector256.Create(16), round = Vector256.Create(Round);
        Vector256<int> cy = Vector256.Create(Cy), crv = Vector256.Create(Crv), cgu = Vector256.Create(Cgu), cgv = Vector256.Create(Cgv), cbu = Vector256.Create(Cbu);
        Vector256<int> zero = Vector256<int>.Zero, max = Vector256.Create(255), alpha = Vector256.Create(unchecked((int)0xff000000));
        int x = 0;
        for (; x + 16 <= width; x += 16)
        {
            Vector256<int> u = Avx2.Subtract(Avx2.ConvertToVector256Int32(uRow + x / 2), c128);
            Vector256<int> v = Avx2.Subtract(Avx2.ConvertToVector256Int32(vRow + x / 2), c128);
            Vector256<int> rc = Avx2.Add(Avx2.MultiplyLow(v, crv), round);
            Vector256<int> gc = Avx2.Subtract(round, Avx2.Add(Avx2.MultiplyLow(u, cgu), Avx2.MultiplyLow(v, cgv)));
            Vector256<int> bc = Avx2.Add(Avx2.MultiplyLow(u, cbu), round);
            for (int half = 0; half < 2; half++)
            {
                Vector256<int> index = half == 0 ? lowHalf : highHalf;
                Vector256<int> luma = Avx2.MultiplyLow(Avx2.Subtract(Avx2.ConvertToVector256Int32(yRow + x + half * 8), c16), cy);
                Vector256<int> r = Channel(luma, Avx2.PermuteVar8x32(rc, index), zero, max);
                Vector256<int> g = Channel(luma, Avx2.PermuteVar8x32(gc, index), zero, max);
                Vector256<int> b = Channel(luma, Avx2.PermuteVar8x32(bc, index), zero, max);
                Vector256<int> pixel = Avx2.Or(Avx2.Or(b, Avx2.ShiftLeftLogical(g, 8)), Avx2.Or(Avx2.ShiftLeftLogical(r, 16), alpha));
                Avx.Store((int*)(outRow + x + half * 8), pixel);
            }
        }
        return x;
    }

    private static Vector256<int> Channel(Vector256<int> luma, Vector256<int> chroma, Vector256<int> zero, Vector256<int> max) =>
        Avx2.Min(Avx2.Max(Avx2.ShiftRightArithmetic(Avx2.Add(luma, chroma), 10), zero), max);
}
