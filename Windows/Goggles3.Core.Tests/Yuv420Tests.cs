using System.Diagnostics;
using System.Runtime.Intrinsics.X86;
using Goggles3.Core;
using Xunit;

namespace Goggles3.Core.Tests;

public sealed class Yuv420Tests
{
    private static byte[] Frame(int width, int height, byte y, byte u, byte v)
    {
        var frame = new byte[Yuv420.Size(width, height)];
        frame.AsSpan(0, width * height).Fill(y);
        frame.AsSpan(width * height).Fill(u);
        frame.AsSpan(width * height + width * height / 4).Fill(v);
        return frame;
    }

    [Theory]
    [InlineData(16, 128, 128, 0, 0, 0)]        // black
    [InlineData(235, 128, 128, 255, 255, 255)] // white
    [InlineData(126, 128, 128, 128, 128, 128)] // mid grey
    [InlineData(63, 102, 240, 255, 0, 0)]      // BT.709 limited-range red (standard YUV for 100 % red)
    [InlineData(173, 42, 26, 0, 255, 0)]       // green
    [InlineData(32, 240, 118, 0, 0, 255)]      // blue
    public void ToBgra_GivesBt709LimitedRangeColours(byte y, byte u, byte v, int r, int g, int b)
    {
        var bgra = new byte[32 * 2 * 4];
        Yuv420.ToBgra(Frame(32, 2, y, u, v), bgra, 32, 2);
        for (int i = 0; i < bgra.Length; i += 4)
        {
            Assert.InRange(bgra[i], b - 2, b + 2); Assert.InRange(bgra[i + 1], g - 2, g + 2);
            Assert.InRange(bgra[i + 2], r - 2, r + 2); Assert.Equal(255, bgra[i + 3]);
        }
    }

    [Theory] [InlineData(1920, 1080)] [InlineData(38, 6)] [InlineData(2, 2)]
    public void ToBgra_SimdAndScalarAgree_AndMatchTheFormula(int width, int height)
    {
        var frame = new byte[Yuv420.Size(width, height)];
        new Random(5).NextBytes(frame);
        var scalar = new byte[width * height * 4];
        Yuv420.Convert(frame, scalar, width, height, simd: false);
        if (Avx2.IsSupported)
        {
            var simd = new byte[width * height * 4];
            Yuv420.Convert(frame, simd, width, height, simd: true);
            Assert.Equal(scalar, simd);
        }
        for (int i = 0; i < width * height; i += 97) // spot check against the floating point definition
        {
            int x = i % width, y = i / width;
            double luma = 1.164384 * (frame[i] - 16);
            double cu = Chroma(frame, width, height, 0, x, y) - 128, cv = Chroma(frame, width, height, 1, x, y) - 128;
            Assert.InRange(scalar[i * 4 + 2], Clamp(luma + 1.792741 * cv) - 2, Clamp(luma + 1.792741 * cv) + 2);
            Assert.InRange(scalar[i * 4 + 1], Clamp(luma - 0.213249 * cu - 0.532909 * cv) - 2, Clamp(luma - 0.213249 * cu - 0.532909 * cv) + 2);
            // Blue: chroma is rounded to 8 bits twice (vertical, horizontal), up to ~1 step, times 2.11 (measured on this
            // frame: max 3, on 0.1 % of pixels; green max 1, red max 2).
            Assert.InRange(scalar[i * 4], Clamp(luma + 2.112402 * cu) - 3, Clamp(luma + 2.112402 * cu) + 3);
        }
    }

    /// <summary>
    /// Bilinear 4:2:0 chroma at luma pixel (x, y), H.264 default siting (chroma_sample_loc_type 0): a chroma sample sits
    /// on the even luma column and halfway between two luma rows. So odd columns are the mean of two neighbours, and a
    /// luma row takes 3/4 of its own chroma row and 1/4 of the nearer other one. Edges repeat the last sample.
    /// </summary>
    private static double Chroma(byte[] frame, int width, int height, int plane, int x, int y)
    {
        int cw = width / 2, ch = height / 2, start = width * height + plane * cw * ch;
        int j = y / 2, other = Math.Clamp(y % 2 == 0 ? j - 1 : j + 1, 0, ch - 1);
        double Row(int k) => 0.75 * frame[start + j * cw + k] + 0.25 * frame[start + other * cw + k];
        int k0 = x / 2;
        return x % 2 == 0 ? Row(k0) : (Row(k0) + Row(Math.Min(k0 + 1, cw - 1))) / 2;
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public void ToBgra_BlendsChromaAcrossAColourEdge(bool simd)
    {
        // 64x4 frame, grey luma; U steps from 16 to 240 between chroma columns 15 and 16 (luma x 31 | 32),
        // V steps from 16 to 240 between chroma rows 0 and 1 (luma y 1 | 2). Nearest-sample copying keeps both edges hard.
        if (simd && !Avx2.IsSupported) return;
        const int width = 64, height = 4;
        var frame = Frame(width, height, 126, 128, 128);
        int u = width * height, v = u + width * height / 4;
        for (int row = 0; row < 2; row++)
            for (int k = 0; k < 32; k++)
            {
                frame[u + row * 32 + k] = (byte)(k < 16 ? 16 : 240);
                frame[v + row * 32 + k] = (byte)(row == 0 ? 16 : 240);
            }
        var bgra = new byte[width * height * 4];
        Yuv420.Convert(frame, bgra, width, height, simd);
        int Blue(int x, int y) => bgra[(y * width + x) * 4];
        int Red(int x, int y) => bgra[(y * width + x) * 4 + 2];
        // x 31 is odd: halfway between U 16 and U 240 = U 128, so blue = luma there (grey 128), not the dark side.
        Assert.InRange(Blue(31, 0), 126, 130);
        Assert.InRange(Blue(30, 0), 0, 10);
        // y 1 and y 2 straddle the V edge: each takes 1/4 of the other chroma row.
        Assert.InRange(Red(10, 1), 24, 32);   // V = 0.75*16 + 0.25*240 = 72 -> red 28 (nearest: 0)
        Assert.InRange(Red(10, 2), 224, 232); // V = 184 -> red 228 (nearest: 255)
    }

    [Theory] [InlineData(true, 40)] [InlineData(false, 40)] [InlineData(true, 1)] [InlineData(true, 17)]
    public unsafe void Upsample_RoundsHalfUpExactly(bool simd, int chroma)
    {
        // Codex review: the float check above has a tolerance, so a rounding slip shared by both paths could hide.
        // Expected values from round-half-up of the exact means, written independently of the shifts in Yuv420.
        if (simd && !Avx2.IsSupported) return;
        var random = new Random(chroma);
        byte[] own = new byte[chroma], other = new byte[chroma], half = new byte[chroma + 32], full = new byte[2 * chroma + 64];
        for (int trial = 0; trial < 200; trial++)
        {
            for (int k = 0; k < chroma; k++) // small values: every remainder of the /4 and /2 shows up often
            {
                own[k] = (byte)(random.Next(4) + (trial % 2) * 250); other[k] = (byte)(random.Next(6) + (trial % 2) * 250);
            }
            fixed (byte* o = own, t = other, h = half, f = full) Yuv420.Upsample(o, t, h, f, chroma, simd);
            int Vertical(int k) => (int)Math.Floor((3.0 * own[k] + other[k]) / 4 + 0.5);
            for (int k = 0; k < chroma; k++)
            {
                Assert.Equal(Vertical(k), full[2 * k]);
                Assert.Equal((int)Math.Floor((Vertical(k) + Vertical(Math.Min(k + 1, chroma - 1))) / 2.0 + 0.5), full[2 * k + 1]);
            }
        }
    }

    private static int Clamp(double value) => (int)Math.Round(Math.Clamp(value, 0, 255));

    [Fact] public void ToBgra_RejectsOddSizesAndShortBuffers()
    {
        Assert.Throws<ArgumentException>(() => Yuv420.ToBgra(new byte[100], new byte[100], 3, 2));
        Assert.Throws<ArgumentException>(() => Yuv420.ToBgra(new byte[5], new byte[16], 2, 2));
    }

    [Fact] public void ToBgra_Converts1080pFastEnoughForLiveVideo()
    {
        var frame = new byte[Yuv420.Size(1920, 1080)]; var bgra = new byte[1920 * 1080 * 4];
        new Random(1).NextBytes(frame);
        Yuv420.ToBgra(frame, bgra, 1920, 1080); // JIT warm-up
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 10; i++) Yuv420.ToBgra(frame, bgra, 1920, 1080);
        // Bench PC (i5-6500T, AVX2): 3.1 ms Release, 16 ms Debug (JIT barely optimises). Generous limits so slow CI
        // runners do not flake; one goggles frame is 33 ms.
#if DEBUG
        double limit = 60;
#else
        double limit = Avx2.IsSupported ? 15 : 60;
#endif
        Assert.True(watch.Elapsed.TotalMilliseconds / 10 < limit, $"{watch.Elapsed.TotalMilliseconds / 10:F1} ms per frame");
    }
}
