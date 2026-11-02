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
            double cu = frame[width * height + y / 2 * (width / 2) + x / 2] - 128;
            double cv = frame[width * height + width * height / 4 + y / 2 * (width / 2) + x / 2] - 128;
            Assert.InRange(scalar[i * 4 + 2], Clamp(luma + 1.792741 * cv) - 1, Clamp(luma + 1.792741 * cv) + 1);
            Assert.InRange(scalar[i * 4 + 1], Clamp(luma - 0.213249 * cu - 0.532909 * cv) - 1, Clamp(luma - 0.213249 * cu - 0.532909 * cv) + 1);
            Assert.InRange(scalar[i * 4], Clamp(luma + 2.112402 * cu) - 1, Clamp(luma + 2.112402 * cu) + 1);
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
