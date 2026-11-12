using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.DownloadTranscoder.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.DownloadTranscoder.Tests;

/// <summary>
/// Runs the generated ffmpeg command lines against a real ffmpeg (from PATH, or FFMPEG_PATH)
/// on a synthetic 1080p clip with two audio tracks and a subtitle track.
/// </summary>
public sealed class FfmpegArgumentsTests : IDisposable
{
    private static readonly string Ffmpeg = Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg";
    private static readonly string Ffprobe = Environment.GetEnvironmentVariable("FFPROBE_PATH") ?? "ffprobe";

    private readonly string _dir = Directory.CreateTempSubdirectory("dt-tests").FullName;
    private readonly string _source;

    public FfmpegArgumentsTests()
    {
        var srt = Path.Combine(_dir, "subs.srt");
        File.WriteAllText(srt, "1\n00:00:01,000 --> 00:00:03,000\nHello\n\n2\n00:00:04,000 --> 00:00:06,000\nWorld\n");
        _source = Path.Combine(_dir, "source movie.mkv");
        Run(Ffmpeg, [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=24:duration=8",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=8",
            "-f", "lavfi", "-i", "sine=frequency=880:duration=8",
            "-i", srt,
            "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:s",
            "-c:v", "libx264", "-preset", "ultrafast", "-b:v", "8M",
            "-c:a", "ac3", "-ac:a:0", "6", "-ac:a:1", "2",
            "-c:s", "srt", _source
        ]);
    }

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void CachedFileIsSmallerSeekableMp4WithAllTracks()
    {
        var output = Path.Combine(_dir, "out.mp4");
        Transcode(output, TranscodeService.LiveFormat.File, Phone());

        var probe = Probe(output);
        Assert.Contains("format_name=mov,mp4", probe, StringComparison.Ordinal);
        Assert.Contains("codec_name=h264", probe, StringComparison.Ordinal);
        Assert.Contains("height=720", probe, StringComparison.Ordinal);
        Assert.Equal(2, Count(probe, "codec_name=aac"));
        Assert.Equal(2, Count(probe, "channels=2"));
        Assert.Contains("codec_name=mov_text", probe, StringComparison.Ordinal);
        Assert.True(new FileInfo(output).Length < new FileInfo(_source).Length);
        Assert.True(MoovBeforeMdat(output), "moov atom should be at the start (faststart)");
    }

    [Fact]
    public void LiveFragmentedMp4CanBeRemuxedIntoCache()
    {
        var live = Path.Combine(_dir, "live.partial");
        Transcode(live, TranscodeService.LiveFormat.FragmentedMp4, Phone());
        Assert.Contains("height=720", Probe(live), StringComparison.Ordinal);

        var remuxed = Path.Combine(_dir, "remuxed.mp4");
        Run(Ffmpeg, ["-hide_banner", "-nostdin", "-y", "-i", live, "-map", "0", "-c", "copy", "-movflags", "+faststart", "-f", "mp4", remuxed]);
        var probe = Probe(remuxed);
        Assert.Contains("codec_name=mov_text", probe, StringComparison.Ordinal);
        Assert.Contains("duration=8", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveMpegTsDropsSubtitles()
    {
        var output = Path.Combine(_dir, "live.ts");
        Transcode(output, TranscodeService.LiveFormat.MpegTs, Phone());
        var probe = Probe(output);
        Assert.Contains("format_name=mpegts", probe, StringComparison.Ordinal);
        Assert.Contains("height=720", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("codec_type=subtitle", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultAudioOnlyAndHevcAndCustomHeight()
    {
        var output = Path.Combine(_dir, "hevc.mp4");
        var cfg = new PluginConfiguration { KeepAllAudioTracks = false, KeepTextSubtitles = false };
        var profile = new TranscodeProfile
        {
            MaxHeight = 480,
            VideoEncoder = "libx265",
            EncoderPreset = "ultrafast",
            VideoBitrateKbps = 800
        };
        Transcode(output, TranscodeService.LiveFormat.File, profile, cfg);
        var probe = Probe(output);
        Assert.Contains("codec_name=hevc", probe, StringComparison.Ordinal);
        Assert.Contains("codec_tag_string=hvc1", probe, StringComparison.Ordinal);
        Assert.Contains("height=480", probe, StringComparison.Ordinal);
        Assert.Equal(1, Count(probe, "codec_type=audio"));
        Assert.DoesNotContain("codec_type=subtitle", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void HdrSourcesGetToneMapped()
    {
        var item = new Movie { Path = _source, Id = Guid.NewGuid() };
        var streams = Streams();
        streams[0].ColorTransfer = "smpte2084";
        streams[0].ColorPrimaries = "bt2020";
        streams[0].BitDepth = 10;
        var args = TranscodeService.BuildArguments(item, streams, "x.mp4", TranscodeService.LiveFormat.File, Phone(), new PluginConfiguration());
        Assert.Contains(args, a => a.Contains("tonemap=", StringComparison.Ordinal));

        var noTonemap = TranscodeService.BuildArguments(item, streams, "x.mp4", TranscodeService.LiveFormat.File, Phone(), new PluginConfiguration { TonemapHdr = false });
        Assert.DoesNotContain(noTonemap, a => a.Contains("tonemap=", StringComparison.Ordinal));
    }

    [Fact]
    public void ProgressOutputIsParsed()
    {
        var output = Path.Combine(_dir, "progress.mp4");
        var item = new Movie { Path = _source, Id = Guid.NewGuid() };
        var args = TranscodeService.BuildArguments(item, Streams(), output, TranscodeService.LiveFormat.File, Phone(), new PluginConfiguration());
        args.InsertRange(0, ["-progress", "pipe:1", "-nostats"]);
        var job = new TranscodeJob { DurationSeconds = 8 };
        foreach (var line in Run(Ffmpeg, args).Split('\n'))
        {
            TranscodeService.ParseProgress(job, line.Trim());
        }

        Assert.InRange(job.ProcessedSeconds, 1, 8.5); // ffmpeg builds differ in how late the final report is
        Assert.True(job.Speed > 0);
        Assert.True(job.OutputBytes > 0);
        job.State = JobState.Running;
        Assert.InRange(job.Percent!.Value, 10, 99.9);
    }

    [Fact]
    public void SplitArgumentsHonoursQuotes()
    {
        var parts = TranscodeService.SplitArguments("-hwaccel vaapi  -hwaccel_device \"/dev/dri/render D128\"");
        Assert.Equal(["-hwaccel", "vaapi", "-hwaccel_device", "/dev/dri/render D128"], parts);
    }

    private static TranscodeProfile Phone() => TranscodeProfile.Defaults()[0];

    private void Transcode(string output, TranscodeService.LiveFormat format, TranscodeProfile profile, PluginConfiguration? cfg = null)
    {
        var item = new Movie { Path = _source, Id = Guid.NewGuid() };
        var args = TranscodeService.BuildArguments(item, Streams(), output, format, profile, cfg ?? new PluginConfiguration());
        Run(Ffmpeg, args);
    }

    private static List<MediaStream> Streams() =>
    [
        new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "h264", Height = 1080, Width = 1920, BitRate = 8_000_000 },
        new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "ac3", Channels = 6, IsDefault = true },
        new MediaStream { Index = 2, Type = MediaStreamType.Audio, Codec = "ac3", Channels = 2 },
        new MediaStream { Index = 3, Type = MediaStreamType.Subtitle, Codec = "subrip" }
    ];

    private static string Probe(string file) =>
        Run(Ffprobe, ["-v", "error", "-show_format", "-show_streams", file]);

    private static int Count(string haystack, string needle) =>
        haystack.Split('\n').Count(l => l.Trim() == needle);

    private static bool MoovBeforeMdat(string file)
    {
        var bytes = File.ReadAllBytes(file);
        var text = System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 1 << 20));
        var moov = text.IndexOf("moov", StringComparison.Ordinal);
        var mdat = text.IndexOf("mdat", StringComparison.Ordinal);
        return moov >= 0 && (mdat < 0 || moov < mdat);
    }

    private static string Run(string exe, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }

        using var p = Process.Start(info)!;
        var stderr = p.StandardError.ReadToEndAsync();
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"{exe} failed: {stderr.Result}\nargs: {string.Join(' ', info.ArgumentList)}");
        return stdout;
    }
}
