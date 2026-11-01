using Goggles3.Core;
using Xunit;

namespace Goggles3.Core.Tests;

/// <summary>Step 5: one place decides what the window shows (status, idle text, fps), so the texts cannot contradict.</summary>
public sealed class ViewerStateTests
{
    private readonly ViewerState state = new();

    [Fact] public void StartsNotConnected()
    {
        Assert.Equal(ViewerStatus.NotConnected, state.Status(0));
        Assert.False(state.HasVideo(0));
    }

    [Fact] public void WaitingForGoggles_ShowsOneMessage()
    {
        // Screenshot 02: the idle screen said "Waiting for video" while the status said "Waiting for the goggles".
        state.Connection = ViewerStatus.WaitingForGoggles;
        ViewerStatus shown = state.Status(10);
        Assert.Equal(ViewerStatus.WaitingForGoggles, shown);
        Assert.Contains("Plug in", shown.Title, StringComparison.Ordinal);
    }

    [Fact] public void SessionWithoutVideo_ShowsConnecting_ThenNoVideo()
    {
        state.Connection = ViewerStatus.Connecting;
        state.SessionStarted(100, replay: false);
        Assert.Equal(ViewerStatus.Connecting, state.Status(102));
        Assert.Equal(ViewerStatus.NoVideo, state.Status(100 + ViewerState.VideoTimeout + 0.1));
    }

    [Fact] public void Frames_ShowLive_UntilVideoStops()
    {
        state.SessionStarted(0, replay: false);
        state.FrameShown(1);
        Assert.True(state.HasVideo(2));
        Assert.Equal(ViewerStatus.Live, state.Status(2));
        Assert.False(state.HasVideo(1 + ViewerState.VideoTimeout + 0.1));
        Assert.Equal(ViewerStatus.NoVideo, state.Status(1 + ViewerState.VideoTimeout + 0.1));
    }

    [Fact] public void Replay_IsNeverShownAsLive()
    {
        state.SessionStarted(0, replay: true);
        state.FrameShown(0.5);
        Assert.Equal(ViewerStatus.Replay, state.Status(1));
    }

    [Fact] public void EndedSession_ShowsTheConnectionStatusAgain()
    {
        state.SessionStarted(0, replay: false);
        state.FrameShown(1);
        state.SessionStopped();
        state.Connection = ViewerStatus.Reconnecting("The USB connection has closed.");
        ViewerStatus shown = state.Status(1.5);
        Assert.False(state.HasVideo(1.5));
        Assert.Equal("The USB connection has closed.", shown.Title);
        Assert.Equal(ViewerLight.Problem, shown.Light);
    }

    [Fact] public void TakeFrameCount_CountsSinceTheLastCall()
    {
        state.SessionStarted(0, replay: false);
        for (int i = 0; i < 5; i++) state.FrameShown(i * 0.01);
        Assert.Equal(5, state.TakeFrameCount());
        Assert.Equal(0, state.TakeFrameCount());
    }
}
