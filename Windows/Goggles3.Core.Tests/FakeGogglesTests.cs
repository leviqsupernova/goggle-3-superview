using System.Net;
using Goggles3.Bench;
using Goggles3.Core;
using Xunit;

namespace Goggles3.Core.Tests;

public sealed class FakeGogglesTests
{
    // SPS, PPS, IDR slice, P slice, P slice (first_mb_in_slice = 0 -> top bit set after the NAL header).
    private static readonly byte[] Stream = [
        0,0,0,1,0x67,1,2, 0,0,0,1,0x68,3, 0,0,1,0x65,0x88,4,5, 0,0,1,0x41,0x9a,6, 0,0,1,0x41,0x9a,7];

    [Fact] public void SplitAccessUnits_KeepsParameterSetsWithTheFirstPicture()
    {
        var units = FakeGoggles.SplitAccessUnits(Stream);
        Assert.Equal(3, units.Count);
        Assert.Equal(Stream[..20], units[0].ToArray());
        Assert.Equal(Stream, units.SelectMany(u => u.ToArray()).ToArray());
    }

    [Fact] public void Packetize_SplitsLargeFramesIntoUnitsTheReceiverReassembles()
    {
        byte[] frame = Enumerable.Range(0, 3000).Select(i => (byte)i).ToArray();
        ushort sequence = 0xfff0; // crosses the 16-bit wrap
        var packets = FakeGoggles.Packetize(frame, 7, ref sequence, 10); // 300 parts -> units of 127+127+46
        var protocol = new LiveProtocol(7);
        var video = packets.Select(p => protocol.Accept(p).Video).Where(v => v != null).SelectMany(v => v!).ToArray();
        Assert.Equal(frame, video);
    }

    [Fact] public async Task FakeGoggles_StreamsAFileThroughUdpVideoReceiver_ByteForByte()
    {
        byte[] clip = [.. Enumerable.Range(0, 200).SelectMany(i => Stream.Skip(i == 0 ? 0 : 13))];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int port;
        using (var probe = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        var fake = new FakeGoggles(new FakeOptions { Fps = 1000, PartSize = 4, Loop = false });
        Task sending = fake.RunAsync(clip, new IPEndPoint(IPAddress.Loopback, port), timeout.Token);
        var received = new List<byte>();
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task receiving = new UdpVideoReceiver(IPAddress.Loopback, new IPEndPoint(IPAddress.Loopback, port), 0).RunAsync((data, _) =>
        {
            received.AddRange(data);
            if (received.Count >= clip.Length) all.TrySetResult();
            return ValueTask.CompletedTask;
        }, timeout.Token);
        await all.Task.WaitAsync(timeout.Token);
        await sending;
        Assert.Equal(clip, received.ToArray());
        Assert.Equal(1, fake.Stats.Handshakes);
        Assert.Equal(600, fake.Stats.Frames); // 200 x (IDR, P, P)
        timeout.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receiving);
    }
}
