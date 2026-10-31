using System.Net;
using System.Net.Sockets;
using Goggles3.Bench;
using Goggles3.Core;
using Xunit;

namespace Goggles3.Core.Tests;

/// <summary>Step 3 findings #1 and #2: the receiver must survive a closed port and a LiveView restart.</summary>
public sealed class ReceiverRecoveryTests
{
    // SPS, PPS, IDR, P: two access units per loop.
    private static readonly byte[] Clip = [0,0,0,1,0x67,1, 0,0,0,1,0x68,2, 0,0,1,0x65,0x88,3, 0,0,1,0x41,0x9a,4];

    private static int FreePort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private static async Task<FakeGoggles> RunUntilVideoAsync(FakeOptions options, Func<FakeGoggles, bool> done)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var goggles = new IPEndPoint(IPAddress.Loopback, FreePort());
        var fake = new FakeGoggles(options with { Fps = 100 });
        Task sending = fake.RunAsync(Clip, goggles, timeout.Token);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = new UdpVideoReceiver(IPAddress.Loopback, goggles, 0) { ResubscribeAfter = TimeSpan.FromMilliseconds(500) };
        Task receiving = receiver.RunAsync((_, _) =>
        {
            if (done(fake)) finished.TrySetResult();
            return ValueTask.CompletedTask;
        }, timeout.Token);
        // A receiver failure must surface here instead of a timeout.
        await Task.WhenAny(finished.Task, receiving).Unwrap().WaitAsync(timeout.Token);
        Assert.True(finished.Task.IsCompleted, "Receiver stopped before the goggles recovered.");
        timeout.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receiving);
        try { await sending; } catch (OperationCanceledException) { }
        return fake;
    }

    [Fact] public async Task Receiver_KeepsHandshaking_WhenGogglesPortIsClosedAtFirst()
    {
        // Handshakes into a closed port get ICMP port unreachable: Linux ECONNREFUSED, Windows WSAECONNRESET.
        FakeGoggles fake = await RunUntilVideoAsync(new FakeOptions { ClosedFor = 1.5 }, _ => true);
        Assert.Equal(1, fake.Stats.Handshakes);
    }

    [Fact] public async Task Receiver_SubscribesAgain_WhenStreamStopsAndGogglesNeedANewHandshake()
    {
        FakeGoggles fake = await RunUntilVideoAsync(
            new FakeOptions { StallEvery = 0.3, StallFor = 0.2, ResubscribeAfterStall = true },
            f => f.Stats.Stalls >= 2 && f.Stats.Handshakes >= 3);
        Assert.True(fake.Stats.Handshakes >= 3);
    }
}
