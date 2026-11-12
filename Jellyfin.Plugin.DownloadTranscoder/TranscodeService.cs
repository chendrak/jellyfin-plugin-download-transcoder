using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DownloadTranscoder.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DownloadTranscoder;

/// <summary>
/// Decides whether an item needs transcoding and runs ffmpeg for it.
/// </summary>
public sealed class TranscodeService : IDisposable
{
    private const string CacheExtension = ".mp4";
    private const string PartialExtension = ".partial";

    private readonly IMediaEncoder _mediaEncoder;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ILogger<TranscodeService> _logger;
    private readonly JobTracker _jobs;
    private readonly IServerConfigurationManager _serverConfig;
    private readonly ConcurrentDictionary<string, Task<bool>> _inFlight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _failed = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slots;
    private readonly object _cacheLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="TranscodeService"/> class.
    /// </summary>
    /// <param name="mediaEncoder">Media encoder, used for the ffmpeg path.</param>
    /// <param name="mediaSourceManager">Media source manager, used for stream info.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="jobs">Job tracker.</param>
    /// <param name="serverConfig">Server configuration, for Jellyfin's hardware acceleration settings.</param>
    public TranscodeService(IMediaEncoder mediaEncoder, IMediaSourceManager mediaSourceManager, ILogger<TranscodeService> logger, JobTracker jobs, IServerConfigurationManager serverConfig)
    {
        _jobs = jobs;
        _serverConfig = serverConfig;
        _mediaEncoder = mediaEncoder;
        _mediaSourceManager = mediaSourceManager;
        _logger = logger;
        _slots = new SemaphoreSlim(Math.Max(1, Config.MaxConcurrentTranscodes));
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private static string CacheDir => Plugin.Instance?.CachePath ?? Path.Combine(Path.GetTempPath(), "download-transcoder");

    private static bool CacheEnabled => Config.MaxCacheSizeGb > 0;

    /// <summary>
    /// Returns true when the item is a local video that should be shrunk with the current settings.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="profile">The quality profile.</param>
    /// <returns>Whether to transcode.</returns>
    public bool ShouldTranscode(BaseItem item, TranscodeProfile profile)
    {
        if (!profile.Transcode || item is not Video || item.IsVirtualItem || !item.IsFileProtocol || !File.Exists(item.Path))
        {
            return false;
        }

        var cfg = Config;
        if (!cfg.SkipIfAlreadySmall)
        {
            return true;
        }

        var streams = GetStreams(item);
        var video = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        if (video is null)
        {
            return false;
        }

        long? bitrate = video.BitRate;
        if (bitrate is null or <= 0 && item.RunTimeTicks is > 0)
        {
            var seconds = TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalSeconds;
            bitrate = (long)(new FileInfo(item.Path).Length * 8 / seconds);
        }

        var smallEnough = (video.Height ?? int.MaxValue) <= profile.MaxHeight
            && bitrate is > 0 && bitrate <= profile.VideoBitrateKbps * 1000L * 13 / 10;
        var friendlyCodec = video.Codec is "h264" or "hevc";
        return !(smallEnough && friendlyCodec && !IsHdr(video));
    }

    /// <summary>
    /// Gets the cached transcode for the item if one is complete.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="profile">The quality profile.</param>
    /// <returns>The file path, or null.</returns>
    public string? TryGetCached(BaseItem item, TranscodeProfile profile)
    {
        if (!CacheEnabled)
        {
            return null;
        }

        var path = GetCachePath(item, profile);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
        }
        catch (IOException)
        {
            // Not important.
        }

        return path;
    }

    /// <summary>
    /// Transcodes the item into the cache unless it is already there or running.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="profile">The quality profile.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="requestedBy">Who asked, for the activity page.</param>
    /// <returns>True when a cached copy exists afterwards.</returns>
    public Task<bool> TranscodeToCacheAsync(BaseItem item, TranscodeProfile profile, CancellationToken cancellationToken, string requestedBy = "")
    {
        if (!CacheEnabled)
        {
            return Task.FromResult(false);
        }

        var target = GetCachePath(item, profile);
        if (File.Exists(target))
        {
            return Task.FromResult(true);
        }

        return _inFlight.GetOrAdd(target, _ => RunCacheJobAsync(item, profile, target, requestedBy, cancellationToken));
    }

    /// <summary>
    /// Returns true when transcoding this item with this profile failed recently, so callers can fall back to the original.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="profile">The quality profile.</param>
    /// <returns>Whether it failed in the last six hours.</returns>
    public bool HasFailedRecently(BaseItem item, TranscodeProfile profile)
        => _failed.TryGetValue(GetCachePath(item, profile), out var when) && when > DateTime.UtcNow.AddHours(-6);

    /// <summary>
    /// Returns true while a background transcode for this item and profile is running.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="profile">The quality profile.</param>
    /// <returns>Whether it is running.</returns>
    public bool IsTranscoding(BaseItem item, TranscodeProfile profile) => _inFlight.ContainsKey(GetCachePath(item, profile));

    /// <summary>
    /// Starts a background transcode into the cache.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="profile">The quality profile.</param>
    /// <param name="requestedBy">Who asked, for the activity page.</param>
    public void Queue(BaseItem item, TranscodeProfile profile, string requestedBy = "")
    {
        _ = TranscodeToCacheAsync(item, profile, CancellationToken.None, requestedBy);
    }

    /// <summary>
    /// Transcodes while sending to the client. The output is kept in the cache when it completes.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="profile">The quality profile.</param>
    /// <param name="context">The HTTP context.</param>
    /// <returns>A task.</returns>
    /// <param name="requestedBy">Who asked, for the activity page.</param>
    public async Task StreamLiveAsync(BaseItem item, TranscodeProfile profile, HttpContext context, string requestedBy = "")
    {
        var job = _jobs.Add(NewJob(item, profile, requestedBy, live: true));
        job.Encoder = EncoderChain(profile)[0].Label;
        job.State = JobState.Running;
        job.StartedUtc = DateTime.UtcNow;
        var cfg = Config;
        var ts = string.Equals(cfg.LiveContainer, "mpegts", StringComparison.OrdinalIgnoreCase);
        var streams = GetStreams(item);
        var encoder = EncoderChain(profile)[0];
        var args = BuildArguments(item, streams, "pipe:1", ts ? LiveFormat.MpegTs : LiveFormat.FragmentedMp4, profile, null, encoder);

        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = ts ? "video/mp2t" : "video/mp4";
        var disposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName(DownloadName(item, ts ? ".ts" : CacheExtension));
        response.Headers.ContentDisposition = disposition.ToString();

        string? partial = null;
        FileStream? tee = null;
        if (CacheEnabled)
        {
            Directory.CreateDirectory(CacheDir);
            partial = Path.Combine(CacheDir, Guid.NewGuid().ToString("N") + PartialExtension);
            tee = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, true);
        }

        var aborted = context.RequestAborted;
        using var process = StartFfmpeg(args, redirectStdout: true);
        var stderr = CollectStderrAsync(process);
        var ok = false;
        try
        {
            var buffer = new byte[1 << 16];
            var stdout = process.StandardOutput.BaseStream;
            int read;
            while ((read = await stdout.ReadAsync(buffer, aborted).ConfigureAwait(false)) > 0)
            {
                await response.Body.WriteAsync(buffer.AsMemory(0, read), aborted).ConfigureAwait(false);
                job.OutputBytes += read;
                if (tee is not null)
                {
                    await tee.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None).ConfigureAwait(false);
                }
            }

            await process.WaitForExitAsync(aborted).ConfigureAwait(false);
            ok = process.ExitCode == 0;
            if (!ok)
            {
                var log = await stderr.ConfigureAwait(false);
                _logger.LogError("Live transcode of {Name} failed (exit {Code}): {Log}", item.Name, process.ExitCode, log);
                _jobs.Finish(job, JobState.Failed, log);
            }
            else
            {
                _jobs.Finish(job, JobState.Completed);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Client stopped downloading {Name}; stopping ffmpeg", item.Name);
            _jobs.Finish(job, JobState.Cancelled, "The app stopped downloading.");
        }
        finally
        {
            Kill(process);
            if (tee is not null)
            {
                await tee.DisposeAsync().ConfigureAwait(false);
            }
        }

        if (partial is null)
        {
            return;
        }

        if (ok)
        {
            // Remux the streamed copy into a seekable MP4 for the next download. This is a fast stream copy.
            var target = GetCachePath(item, profile);
            _ = Task.Run(() => RemuxIntoCacheAsync(partial, target));
        }
        else
        {
            TryDelete(partial);
        }
    }

    /// <summary>
    /// Builds the file name offered to the client.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="extension">Extension including the dot.</param>
    /// <returns>The file name.</returns>
    public static string DownloadName(BaseItem item, string extension)
    {
        var name = Path.GetFileNameWithoutExtension(item.Path) ?? item.Name ?? "download";
        return name.Replace("\"", string.Empty, StringComparison.Ordinal) + extension;
    }

    /// <summary>
    /// Deletes the least recently used cache files until the cache fits the configured size.
    /// </summary>
    public void EnforceCacheLimit()
    {
        lock (_cacheLock)
        {
            var dir = new DirectoryInfo(CacheDir);
            if (!dir.Exists)
            {
                return;
            }

            foreach (var stale in dir.EnumerateFiles("*" + PartialExtension).Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)))
            {
                TryDelete(stale.FullName);
            }

            var limit = (long)Config.MaxCacheSizeGb * 1024 * 1024 * 1024;
            var files = dir.EnumerateFiles("*" + CacheExtension)
                .OrderBy(f => f.LastAccessTimeUtc > f.LastWriteTimeUtc ? f.LastAccessTimeUtc : f.LastWriteTimeUtc)
                .ToList();
            var total = files.Sum(f => f.Length);
            foreach (var file in files)
            {
                if (total <= limit)
                {
                    break;
                }

                _logger.LogInformation("Removing {File} from the download cache", file.Name);
                total -= file.Length;
                TryDelete(file.FullName);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _slots.Dispose();
    }

    internal enum LiveFormat
    {
        File,
        FragmentedMp4,
        MpegTs
    }

    private async Task<bool> RunCacheJobAsync(BaseItem item, TranscodeProfile profile, string target, string requestedBy, CancellationToken cancellationToken)
    {
        var job = _jobs.Add(NewJob(item, profile, requestedBy, live: false));
        try
        {
            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (File.Exists(target))
                {
                    _jobs.Finish(job, JobState.Completed);
                    return true;
                }

                job.State = JobState.Running;
                job.StartedUtc = DateTime.UtcNow;

                Directory.CreateDirectory(CacheDir);
                var partial = Path.Combine(CacheDir, Guid.NewGuid().ToString("N") + PartialExtension);
                var streams = GetStreams(item);
                // Try the best encoder first and fall back (full GPU, GPU encode only, CPU) if ffmpeg fails,
                // e.g. because the GPU can't decode this codec or the device isn't accessible.
                var ok = false;
                var log = string.Empty;
                var failures = new List<string>();
                foreach (var encoder in EncoderChain(profile))
                {
                    job.Encoder = failures.Count == 0 ? encoder.Label : encoder.Label + " (fallback)";
                    job.ProcessedSeconds = 0;
                    _logger.LogInformation("Transcoding {Name} for download with profile {Profile} on {Encoder}", item.Name, profile.Name, encoder.Label);
                    var args = BuildArguments(item, streams, partial, LiveFormat.File, profile, null, encoder);
                    args.InsertRange(0, ["-progress", "pipe:1", "-nostats"]);
                    (ok, log) = await RunToCompletionAsync(args, cancellationToken, line => ParseProgress(job, line)).ConfigureAwait(false);
                    if (ok)
                    {
                        break;
                    }

                    _logger.LogWarning("Transcoding {Name} with {Encoder} failed: {Log}", item.Name, encoder.Label, log);
                    failures.Add(encoder.Label + ": " + log);
                }

                if (failures.Count > 0)
                {
                    job.GpuError = string.Join("\n\n", ok ? failures : failures.Take(failures.Count - 1));
                }

                if (!ok)
                {
                    TryDelete(partial);
                    _failed[target] = DateTime.UtcNow;
                    _jobs.Finish(job, JobState.Failed, log);
                    return false;
                }

                job.OutputBytes = new FileInfo(partial).Length;
                File.Move(partial, target, true);
                _jobs.Finish(job, JobState.Completed);
                _failed.TryRemove(target, out _);
                _logger.LogInformation("Cached download transcode of {Name}", item.Name);
                EnforceCacheLimit();
                return true;
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (OperationCanceledException)
        {
            _jobs.Finish(job, JobState.Cancelled, "Cancelled.");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogError(ex, "Transcoding {Name} failed", item.Name);
            _failed[target] = DateTime.UtcNow;
            _jobs.Finish(job, JobState.Failed, ex.Message);
            return false;
        }
        finally
        {
            _inFlight.TryRemove(target, out _);
        }
    }

    private async Task RemuxIntoCacheAsync(string source, string target)
    {
        var temp = Path.Combine(CacheDir, Guid.NewGuid().ToString("N") + PartialExtension);
        try
        {
            var args = new List<string>
            {
                "-hide_banner", "-nostdin", "-y", "-i", source,
                "-map", "0", "-c", "copy", "-movflags", "+faststart", "-f", "mp4", temp
            };
            if ((await RunToCompletionAsync(args, CancellationToken.None).ConfigureAwait(false)).Ok)
            {
                File.Move(temp, target, true);
                EnforceCacheLimit();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not keep the streamed download in the cache");
        }
        finally
        {
            TryDelete(source);
            TryDelete(temp);
        }
    }

    private async Task<(bool Ok, string Log)> RunToCompletionAsync(List<string> args, CancellationToken cancellationToken, Action<string>? onStdoutLine = null)
    {
        using var process = StartFfmpeg(args, redirectStdout: onStdoutLine is not null);
        var stderr = CollectStderrAsync(process);
        var stdout = onStdoutLine is null ? Task.CompletedTask : Task.Run(
            async () =>
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync(CancellationToken.None).ConfigureAwait(false)) is not null)
                {
                    onStdoutLine(line);
                }
            },
            CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await stdout.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }

        var log = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            _logger.LogError("ffmpeg exited with {Code}: {Log}", process.ExitCode, log);
            return (false, string.IsNullOrWhiteSpace(log) ? "ffmpeg exited with code " + process.ExitCode : log);
        }

        return (true, log);
    }

    private IReadOnlyList<EncoderChoice> EncoderChain(TranscodeProfile profile)
    {
        var options = _serverConfig.GetEncodingOptions();
        return HardwareEncoder.Chain(
            profile,
            Config,
            options.HardwareAccelerationType.ToString(),
            options.EnableHardwareEncoding,
            options.VaapiDevice,
            options.QsvDevice);
    }

    private static TranscodeJob NewJob(BaseItem item, TranscodeProfile profile, string requestedBy, bool live)
    {
        long size = 0;
        try
        {
            size = new FileInfo(item.Path).Length;
        }
        catch (IOException)
        {
            // Unknown size.
        }

        return new TranscodeJob
        {
            ItemId = item.Id,
            ItemName = DisplayName(item),
            Profile = profile.Name,
            RequestedBy = requestedBy,
            Live = live,
            DurationSeconds = item.RunTimeTicks is > 0 ? TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalSeconds : 0,
            SourceBytes = size
        };
    }

    internal static string DisplayName(BaseItem item)
    {
        if (item is Episode episode)
        {
            var code = episode.ParentIndexNumber is not null && episode.IndexNumber is not null
                ? string.Format(CultureInfo.InvariantCulture, " S{0:00}E{1:00}", episode.ParentIndexNumber, episode.IndexNumber)
                : string.Empty;
            return episode.SeriesName + code + " - " + episode.Name;
        }

        return item.ProductionYear is not null ? item.Name + " (" + item.ProductionYear.Value.ToString(CultureInfo.InvariantCulture) + ")" : item.Name;
    }

    /// <summary>
    /// Applies one line of ffmpeg "-progress" output to a job.
    /// </summary>
    /// <param name="job">The job.</param>
    /// <param name="line">A "key=value" line.</param>
    internal static void ParseProgress(TranscodeJob job, string line)
    {
        var eq = line.IndexOf('=', StringComparison.Ordinal);
        if (eq <= 0)
        {
            return;
        }

        var key = line[..eq];
        var value = line[(eq + 1)..].Trim();
        switch (key)
        {
            case "out_time_us":
            case "out_time_ms": // Despite its name, ffmpeg reports microseconds here too.
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0)
                {
                    job.ProcessedSeconds = us / 1_000_000.0;
                }

                break;
            case "total_size":
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes))
                {
                    job.OutputBytes = bytes;
                }

                break;
            case "speed":
                if (double.TryParse(value.TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
                {
                    job.Speed = speed;
                }

                break;
        }
    }

    private Process StartFfmpeg(List<string> args, bool redirectStdout)
    {
        var info = new ProcessStartInfo(_mediaEncoder.EncoderPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = redirectStdout,
            RedirectStandardInput = false
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        _logger.LogDebug("Running {Ffmpeg} {Args}", info.FileName, string.Join(' ', args));
        return Process.Start(info) ?? throw new InvalidOperationException("Could not start ffmpeg");
    }

    private static async Task<string> CollectStderrAsync(Process process)
    {
        // Keep only the tail; ffmpeg can be chatty.
        var lines = new Queue<string>();
        string? line;
        while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            lines.Enqueue(line);
            if (lines.Count > 20)
            {
                lines.Dequeue();
            }
        }

        return string.Join('\n', lines);
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the cleanup task.
        }
    }

    private static bool IsHdr(MediaStream video)
        => string.Equals(video.VideoRange.ToString(), "HDR", StringComparison.OrdinalIgnoreCase);

    private List<MediaStream> GetStreams(BaseItem item) => _mediaSourceManager.GetMediaStreams(item.Id).ToList();

    private static string GetCachePath(BaseItem item, TranscodeProfile profile)
    {
        var cfg = Config;
        var info = new FileInfo(item.Path);
        var key = string.Join(
            '|',
            info.Length,
            info.LastWriteTimeUtc.Ticks,
            profile.MaxHeight,
            profile.VideoBitrateKbps,
            profile.VideoEncoder,
            profile.EncoderPreset,
            profile.AudioBitrateKbps,
            profile.AudioChannels,
            cfg.KeepAllAudioTracks,
            cfg.KeepTextSubtitles,
            cfg.TonemapHdr,
            cfg.ExtraInputArguments,
            cfg.CustomVideoFilter);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12].ToLowerInvariant();
        return Path.Combine(CacheDir, item.Id.ToString("N") + "_" + hash + CacheExtension);
    }

    internal static List<string> BuildArguments(BaseItem item, List<MediaStream> streams, string output, LiveFormat format, TranscodeProfile profile, PluginConfiguration? config = null, EncoderChoice? encoderChoice = null)
    {
        var cfg = config ?? Config;
        var choice = encoderChoice ?? HardwareEncoder.Resolve(profile, cfg, null, false, null, null);
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error" };
        args.AddRange(choice.InitArguments);
        args.AddRange(SplitArguments(cfg.ExtraInputArguments));
        args.Add("-i");
        args.Add(item.Path);

        var video = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        args.AddRange(["-map", video is null ? "0:v:0" : "0:" + video.Index.ToString(CultureInfo.InvariantCulture)]);

        if (cfg.KeepAllAudioTracks)
        {
            args.AddRange(["-map", "0:a?"]);
        }
        else
        {
            var audio = streams.Where(s => s.Type == MediaStreamType.Audio)
                .OrderByDescending(s => s.IsDefault)
                .FirstOrDefault();
            if (audio is not null)
            {
                args.AddRange(["-map", "0:" + audio.Index.ToString(CultureInfo.InvariantCulture)]);
            }
        }

        var subs = format == LiveFormat.MpegTs || !cfg.KeepTextSubtitles
            ? []
            : streams.Where(s => s.Type == MediaStreamType.Subtitle && !s.IsExternal && s.IsTextSubtitleStream).ToList();
        foreach (var sub in subs)
        {
            args.AddRange(["-map", "0:" + sub.Index.ToString(CultureInfo.InvariantCulture)]);
        }

        var height = profile.MaxHeight.ToString(CultureInfo.InvariantCulture);
        string filter;
        if (!string.IsNullOrWhiteSpace(cfg.CustomVideoFilter))
        {
            filter = cfg.CustomVideoFilter.Replace("{height}", height, StringComparison.Ordinal);
        }
        else if (choice.FullPipeline)
        {
            filter = choice.FullPipelineFilter(profile.MaxHeight, cfg.TonemapHdr && video is not null && IsHdr(video));
        }
        else
        {
            filter = "scale=w=-2:h='min(" + height + ",ih)'";
            if (cfg.TonemapHdr && video is not null && IsHdr(video))
            {
                filter += ",zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p";
            }

            filter += choice.FilterSuffix;
        }

        var encoder = choice.Encoder;
        var kbps = Math.Max(100, profile.VideoBitrateKbps);
        args.AddRange(["-vf", filter, "-c:v", encoder]);

        // libx264/libx265 presets (veryfast, …) mean nothing to GPU encoders, so only pass them to the CPU ones.
        if (!choice.IsHardware && !string.IsNullOrWhiteSpace(profile.EncoderPreset))
        {
            args.AddRange(["-preset", profile.EncoderPreset.Trim()]);
        }

        args.AddRange([
            "-b:v", kbps + "k",
            "-maxrate", (kbps * 3 / 2) + "k",
            "-bufsize", (kbps * 2) + "k"
        ]);
        if (encoder.StartsWith("lib", StringComparison.Ordinal))
        {
            args.AddRange(["-pix_fmt", "yuv420p"]);
        }

        if (encoder.Contains("265", StringComparison.Ordinal) || encoder.Contains("hevc", StringComparison.OrdinalIgnoreCase))
        {
            args.AddRange(["-tag:v", "hvc1"]);
        }

        args.AddRange([
            "-c:a", "aac",
            "-b:a", Math.Max(32, profile.AudioBitrateKbps) + "k",
            "-ac", Math.Max(1, profile.AudioChannels).ToString(CultureInfo.InvariantCulture)
        ]);
        if (subs.Count > 0)
        {
            args.AddRange(["-c:s", "mov_text"]);
        }

        args.AddRange(["-map_metadata", "0", "-map_chapters", "0", "-max_muxing_queue_size", "4096"]);

        switch (format)
        {
            case LiveFormat.MpegTs:
                args.AddRange(["-f", "mpegts"]);
                break;
            case LiveFormat.FragmentedMp4:
                args.AddRange(["-movflags", "+frag_keyframe+empty_moov+default_base_moof", "-f", "mp4"]);
                break;
            default:
                args.AddRange(["-movflags", "+faststart", "-f", "mp4"]);
                break;
        }

        args.Add(output);
        return args;
    }

    /// <summary>
    /// Splits a command line into arguments, honouring double quotes.
    /// </summary>
    /// <param name="value">The command line.</param>
    /// <returns>The arguments.</returns>
    internal static List<string> SplitArguments(string? value)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        var current = new StringBuilder();
        var quoted = false;
        var has = false;
        foreach (var c in value)
        {
            if (c == '"')
            {
                quoted = !quoted;
                has = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (has)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    has = false;
                }
            }
            else
            {
                current.Append(c);
                has = true;
            }
        }

        if (has)
        {
            result.Add(current.ToString());
        }

        return result;
    }
}
