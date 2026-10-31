using Goggles3.Core;
using Xunit;

namespace Goggles3.Core.Tests;

/// <summary>Step 3 finding #6: a slow decoder must not block UDP reception.</summary>
public sealed class H264QueueTests
{
    private static readonly byte[] Key = [0,0,0,1,0x67,1, 0,0,1,0x65,0x88];
    private static byte[] Delta(byte id) => [0,0,1,0x41,0x9a,id];

    private static async Task<List<byte[]>> DrainAsync(H264Queue queue)
    {
        queue.Complete();
        var items = new List<byte[]>();
        await foreach (byte[] chunk in queue.ReadAllAsync(CancellationToken.None)) items.Add(chunk);
        return items;
    }

    [Fact] public void ContainsKeyframe_FindsSpsOrIdr_Only()
    {
        Assert.True(H264Queue.ContainsKeyframe(Key));
        Assert.True(H264Queue.ContainsKeyframe([0,0,1,0x65,0]));
        Assert.False(H264Queue.ContainsKeyframe(Delta(1)));
        Assert.False(H264Queue.ContainsKeyframe([0x65,0x67,0,0]));
    }

    [Fact] public async Task Add_NeverBlocks_AndSkipsToTheNextKeyframe_WhenFull()
    {
        var queue = new H264Queue(2);
        queue.Add(Key); queue.Add(Delta(1));
        queue.Add(Delta(2));          // full: dropped, now waiting for a keyframe
        await DrainOne(queue); await DrainOne(queue); // room again, but a delta would reference the lost frame
        queue.Add(Delta(3));          // dropped
        queue.Add(Key);               // accepted
        queue.Add(Delta(4));
        Assert.Equal(2, queue.Dropped);
        Assert.Equal([Key, Delta(4)], await DrainAsync(queue));
    }

    [Fact] public async Task Add_ResumesWithoutKeyframe_AfterACapacityOfDrops()
    {
        // Goggles that rarely send an IDR must not freeze the picture forever.
        var queue = new H264Queue(2);
        queue.Add(Delta(1)); queue.Add(Delta(2)); queue.Add(Delta(3)); // full -> resync
        await DrainOne(queue); await DrainOne(queue);
        queue.Add(Delta(4));                       // 2nd drop = capacity: stop waiting for a keyframe
        queue.Add(Delta(5)); queue.Add(Delta(6));  // accepted
        Assert.Equal(2, queue.Dropped);
        Assert.Equal([Delta(5), Delta(6)], await DrainAsync(queue));
    }

    private static async Task DrainOne(H264Queue queue)
    {
        await using var reader = queue.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
    }
}
