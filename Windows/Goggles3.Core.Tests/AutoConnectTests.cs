using System.Collections.Concurrent;
using System.Net;
using Goggles3.Core;
using Xunit;

namespace Goggles3.Core.Tests;

/// <summary>Step 4: --connect keeps trying, never blocks on a dialog, restarts ended sessions (#11).</summary>
public sealed class AutoConnectTests
{
    private static readonly IPAddress Local = IPAddress.Parse("192.168.60.1");
    private readonly ConcurrentQueue<ViewerStatus> statuses = new();

    private AutoConnect Create(Func<int, Adapter> find, Func<int, CancellationToken, Task<string>> run) =>
        new(_ => Task.FromResult(find(Interlocked.Increment(ref finds))),
            (address, token) => { Assert.Equal(Local, address); return run(Interlocked.Increment(ref sessions), token); },
            statuses.Enqueue) { RetryDelay = TimeSpan.FromMilliseconds(5) };
    private int finds, sessions;

    private static async Task RunUntil(AutoConnect loop, Func<bool> done)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task running = loop.RunAsync(stop.Token);
        while (!done() && !stop.IsCancellationRequested) await Task.Delay(5);
        Assert.True(done(), "Condition not reached before the timeout.");
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact] public async Task WaitsForTheGoggles_ThenConnects()
    {
        var loop = Create(n => n < 3 ? new Adapter(AdapterState.Missing) : new Adapter(AdapterState.Ready, Local),
            (_, token) => Task.Delay(Timeout.Infinite, token).ContinueWith(_ => "stopped", TaskScheduler.Default));
        await RunUntil(loop, () => sessions == 1);
        Assert.Contains(ViewerStatus.WaitingForGoggles, statuses);
        Assert.Contains(ViewerStatus.Connecting, statuses);
    }

    [Fact] public async Task RestartsTheSession_WhenItEnds()
    {
        var loop = Create(_ => new Adapter(AdapterState.Ready, Local), (_, _) => Task.FromResult("The decoder exited with code 137."));
        await RunUntil(loop, () => sessions >= 3);
        Assert.Contains(ViewerStatus.Reconnecting("The decoder exited with code 137."), statuses);
    }

    [Fact] public async Task KeepsTrying_WhenFindingOrStartingFails()
    {
        var loop = Create(n => n == 1 ? throw new IOException("Windows cannot activate the goggles USB device (code 10).") : new Adapter(AdapterState.Ready, Local),
            (n, _) => n == 1 ? throw new FileNotFoundException("Decoder missing.") : Task.FromResult("ended"));
        await RunUntil(loop, () => sessions >= 2);
        Assert.Contains(ViewerStatus.Retrying("Windows cannot activate the goggles USB device (code 10)."), statuses);
        Assert.Contains(ViewerStatus.Retrying("Decoder missing."), statuses);
    }

    [Fact] public async Task NeverStartsASession_WhenTheAdapterNeedsSetup()
    {
        var loop = Create(_ => new Adapter(AdapterState.NeedsSetup), (_, _) => Task.FromResult("x"));
        await RunUntil(loop, () => finds >= 3);
        Assert.Equal(0, sessions);
        Assert.Contains(ViewerStatus.NeedsSetup, statuses);
    }

    [Fact] public async Task Cancel_StopsARunningSession()
    {
        var loop = Create(_ => new Adapter(AdapterState.Ready, Local),
            async (_, token) => { await Task.Delay(Timeout.Infinite, token); return "never"; });
        await RunUntil(loop, () => sessions == 1); // RunUntil asserts the loop ends with OperationCanceledException
        Assert.Equal(1, sessions);
    }
}
