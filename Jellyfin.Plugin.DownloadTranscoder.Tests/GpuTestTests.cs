using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.DownloadTranscoder.Tests;

public class GpuTestTests
{
    [Fact]
    public async Task ReportsCpuResultsForBothSamplesWhenNoGpuIsConfigured()
    {
        var encoder = new Mock<IMediaEncoder>();
        encoder.SetupGet(e => e.EncoderPath).Returns(Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg");
        var config = new Mock<IServerConfigurationManager>();
        config.Setup(c => c.GetConfiguration("encoding")).Returns(new EncodingOptions());
        var tracker = new JobTracker(NullLogger<JobTracker>.Instance);
        using var service = new TranscodeService(encoder.Object, Mock.Of<IMediaSourceManager>(), NullLogger<TranscodeService>.Instance, tracker, config.Object);

        var results = await service.TestGpuAsync(CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Ok, r.Error));
        Assert.All(results, r => Assert.False(r.IsHardware));
        Assert.Contains(results, r => r.Sample.Contains("HEVC", StringComparison.Ordinal));
        Assert.All(results, r => Assert.True(r.Speed > 0));
    }
}
