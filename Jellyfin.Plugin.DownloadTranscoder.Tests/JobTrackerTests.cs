using System;
using Jellyfin.Plugin.DownloadTranscoder;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.DownloadTranscoder.Tests;

public class JobTrackerTests
{
    [Fact]
    public void FinishedJobsMoveToHistoryNewestFirst()
    {
        var tracker = new JobTracker(NullLogger<JobTracker>.Instance);
        var a = tracker.Add(new TranscodeJob { ItemName = "A" });
        var b = tracker.Add(new TranscodeJob { ItemName = "B" });
        Assert.Equal(2, tracker.Active().Count);

        tracker.Finish(a, JobState.Completed);
        tracker.Finish(b, JobState.Failed, "boom");

        Assert.Empty(tracker.Active());
        var history = tracker.History();
        Assert.Equal("B", history[0].ItemName);
        Assert.Equal("boom", history[0].Error);
        Assert.Equal(100, history[1].Percent);
        Assert.NotNull(history[1].EndedUtc);
    }
}
