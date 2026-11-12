using System;
using System.Linq;
using Jellyfin.Plugin.DownloadTranscoder.Configuration;
using Xunit;

namespace Jellyfin.Plugin.DownloadTranscoder.Tests;

public class HardwareEncoderTests
{
    private static TranscodeProfile Profile(string encoder = "libx264") => new() { VideoEncoder = encoder, EncoderPreset = "veryfast" };

    [Theory]
    [InlineData("nvenc", "libx264", "h264_nvenc")]
    [InlineData("qsv", "libx264", "h264_qsv")]
    [InlineData("vaapi", "libx265", "hevc_vaapi")]
    [InlineData("videotoolbox", "libx265", "hevc_videotoolbox")]
    [InlineData("amf", "libx264", "h264_amf")]
    public void UsesJellyfinsHardwareType(string type, string profileEncoder, string expected)
    {
        var choice = HardwareEncoder.Resolve(Profile(profileEncoder), new PluginConfiguration(), type, true, "/dev/dri/renderD129", null);
        Assert.Equal(expected, choice.Encoder);
        Assert.True(choice.IsHardware);
        Assert.StartsWith("GPU", choice.Label, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("none", true)]
    [InlineData("qsv", false)]
    [InlineData("", true)]
    public void StaysOnCpuWhenJellyfinHasNoHardwareEncoding(string type, bool hwEncoding)
    {
        var choice = HardwareEncoder.Resolve(Profile(), new PluginConfiguration(), type, hwEncoding, null, null);
        Assert.Equal("libx264", choice.Encoder);
        Assert.False(choice.IsHardware);
    }

    [Fact]
    public void RespectsTheOptOutAndTheFallback()
    {
        var off = HardwareEncoder.Resolve(Profile(), new PluginConfiguration { UseJellyfinHardwareAcceleration = false }, "nvenc", true, null, null);
        Assert.False(off.IsHardware);

        var fallback = HardwareEncoder.Resolve(Profile("h264_nvenc"), new PluginConfiguration(), "nvenc", true, null, null, forceSoftware: true);
        Assert.Equal("libx264", fallback.Encoder);
    }

    [Fact]
    public void ExplicitHardwareEncoderIsKept()
    {
        var choice = HardwareEncoder.Resolve(Profile("hevc_vaapi"), new PluginConfiguration(), "none", false, null, null);
        Assert.Equal("hevc_vaapi", choice.Encoder);
        Assert.Equal("vaapi", choice.HardwareType);
        Assert.Contains("vaapi=va:/dev/dri/renderD128", choice.InitArguments);
    }

    [Fact]
    public void VaapiArgumentsUploadFramesAndSkipCpuPreset()
    {
        var cfg = new PluginConfiguration();
        var profile = Profile();
        var choice = HardwareEncoder.Resolve(profile, cfg, "vaapi", true, "/dev/dri/renderD129", null);
        var item = new MediaBrowser.Controller.Entities.Movies.Movie { Path = "/x.mkv", Id = Guid.NewGuid() };
        var streams = new System.Collections.Generic.List<MediaBrowser.Model.Entities.MediaStream>
        {
            new() { Index = 0, Type = MediaBrowser.Model.Entities.MediaStreamType.Video, Codec = "hevc", Height = 2160 }
        };
        var args = TranscodeService.BuildArguments(item, streams, "out.mp4", TranscodeService.LiveFormat.File, profile, cfg, choice);

        Assert.True(args.IndexOf("-init_hw_device") < args.IndexOf("-i"));
        Assert.Contains("vaapi=va:/dev/dri/renderD129", args);
        Assert.EndsWith(",format=nv12,hwupload", args[args.IndexOf("-vf") + 1], StringComparison.Ordinal);
        Assert.Equal("h264_vaapi", args[args.IndexOf("-c:v") + 1]);
        Assert.DoesNotContain("-preset", args);
        Assert.DoesNotContain("-pix_fmt", args);
    }

    [Fact]
    public void IntelQsvChainTriesFullPipelineThenEncodeOnlyThenCpu()
    {
        var chain = HardwareEncoder.Chain(Profile(), new PluginConfiguration(), "qsv", true, "/dev/dri/renderD128", null);
        Assert.Equal(3, chain.Count);
        Assert.True(chain[0].FullPipeline);
        Assert.Equal("h264_qsv", chain[0].Encoder);
        Assert.Contains("-hwaccel", chain[0].InitArguments);
        Assert.Contains("qsv=qs@va", chain[0].InitArguments);
        Assert.False(chain[1].FullPipeline);
        Assert.True(chain[1].IsHardware);
        Assert.False(chain[2].IsHardware);

        var filter = chain[0].FullPipelineFilter(720, hdr: true);
        Assert.Equal("tonemap_vaapi=format=nv12:t=bt709:m=bt709:p=bt709,scale_vaapi=w=-2:h='min(720,ih)':format=nv12,hwmap=derive_device=qsv,format=qsv", filter);
    }

    [Fact]
    public void FullPipelineCanBeTurnedOffAndIsNotUsedForNvenc()
    {
        Assert.Equal(2, HardwareEncoder.Chain(Profile(), new PluginConfiguration { FullGpuPipeline = false }, "vaapi", true, null, null).Count);
        var nvenc = HardwareEncoder.Chain(Profile(), new PluginConfiguration(), "nvenc", true, null, null);
        Assert.Equal(2, nvenc.Count);
        Assert.False(nvenc[0].FullPipeline);
        Assert.Single(HardwareEncoder.Chain(Profile(), new PluginConfiguration(), "none", false, null, null));
    }

    [Fact]
    public void FullPipelineArgumentsUseVaapiScaling()
    {
        var cfg = new PluginConfiguration();
        var profile = Profile();
        var full = HardwareEncoder.Chain(profile, cfg, "vaapi", true, null, null)[0];
        var item = new MediaBrowser.Controller.Entities.Movies.Movie { Path = "/x.mkv", Id = Guid.NewGuid() };
        var streams = new System.Collections.Generic.List<MediaBrowser.Model.Entities.MediaStream>
        {
            new() { Index = 0, Type = MediaBrowser.Model.Entities.MediaStreamType.Video, Codec = "hevc", Height = 2160 }
        };
        var args = TranscodeService.BuildArguments(item, streams, "out.mp4", TranscodeService.LiveFormat.File, profile, cfg, full);
        Assert.Equal("scale_vaapi=w=-2:h='min(720,ih)':format=nv12", args[args.IndexOf("-vf") + 1]);
        Assert.Equal("h264_vaapi", args[args.IndexOf("-c:v") + 1]);
        Assert.True(args.IndexOf("-hwaccel") < args.IndexOf("-i"));
    }
}
