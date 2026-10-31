using Goggles3.Core;
using Xunit;

namespace Goggles3.Core.Tests;

/// <summary>Step 3 finding #7: no 8 MB allocation per frame, and the frame being shown is never overwritten.</summary>
public sealed class LatestFrameTests
{
    [Fact] public void Take_ReturnsNothing_UntilAFrameIsPublished()
    {
        var frames = new LatestFrame(4);
        Assert.Null(frames.TakeLatest());
        frames.Back[0] = 1; frames.Publish();
        Assert.Equal(1, frames.TakeLatest()![0]);
        Assert.Null(frames.TakeLatest()); // no new frame since the last take
    }

    [Fact] public void Decoder_NeverWritesIntoTheFrameBeingRead_AndAlwaysGetsTheNewest()
    {
        var frames = new LatestFrame(4);
        frames.Back[0] = 1; frames.Publish();
        byte[] shown = frames.TakeLatest()!;
        for (byte i = 2; i < 10; i++)
        {
            Assert.NotSame(shown, frames.Back); // UI may still be copying "shown"
            frames.Back[0] = i; frames.Publish();
        }
        Assert.Equal(1, shown[0]);
        Assert.Equal(9, frames.TakeLatest()![0]);
    }
}
