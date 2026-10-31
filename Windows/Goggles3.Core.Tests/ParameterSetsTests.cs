using Goggles3.Core;
using Xunit;

namespace Goggles3.Core.Tests;

/// <summary>Step 3 review round 2: cached SPS/PPS for recordings that start mid-stream.</summary>
public sealed class ParameterSetsTests
{
    private static readonly byte[] Sps = [0,0,0,1,0x67,0x42,0x00,0x1f,0xab,0xcd];
    private static readonly byte[] Pps = [0,0,0,1,0x68,0xce,0x3c,0x80];
    private static readonly byte[] Idr = [0,0,1,0x65,0x88,0x84];

    [Fact] public void Complete_AddsBothSets_ToABareIdr()
    {
        var sets = new ParameterSets();
        sets.Observe([.. Sps, .. Pps, .. Idr]);
        Assert.Equal([.. Sps, .. Pps, .. Idr], sets.Complete(Idr));
    }

    [Fact] public void Complete_AddsOnlyTheMissingPps_WhenSpsRepeatsButPpsWasSentOnce()
    {
        var sets = new ParameterSets();
        sets.Observe([.. Sps, .. Pps, .. Idr]);
        Assert.Equal([.. Sps, .. Pps, .. Sps, .. Idr], sets.Complete([.. Sps, .. Idr])); // SPS before PPS, ffmpeg needs that order
    }

    [Fact] public void Observe_KeepsAnSpsThatIsSplitAcrossTwoChunks()
    {
        var sets = new ParameterSets();
        byte[] stream = [.. Sps, .. Pps, .. Idr];
        sets.Observe(stream[..7]);   // SPS cut after 3 payload bytes
        sets.Observe(stream[7..]);
        Assert.Equal([.. Sps, .. Pps, .. Idr], sets.Complete(Idr));
    }
}
