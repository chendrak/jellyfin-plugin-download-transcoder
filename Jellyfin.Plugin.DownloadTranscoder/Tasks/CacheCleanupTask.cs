using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.DownloadTranscoder.Tasks;

/// <summary>
/// Keeps the transcode cache under its size limit.
/// </summary>
public class CacheCleanupTask : IScheduledTask
{
    private readonly TranscodeService _transcodeService;

    /// <summary>
    /// Initializes a new instance of the <see cref="CacheCleanupTask"/> class.
    /// </summary>
    /// <param name="transcodeService">Transcode service.</param>
    public CacheCleanupTask(TranscodeService transcodeService)
    {
        _transcodeService = transcodeService;
    }

    /// <inheritdoc />
    public string Name => "Clean download transcode cache";

    /// <inheritdoc />
    public string Key => "DownloadTranscoderCacheCleanup";

    /// <inheritdoc />
    public string Description => "Removes the least recently downloaded transcodes when the cache is over its size limit.";

    /// <inheritdoc />
    public string Category => "Download Transcoder";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _transcodeService.EnforceCacheLimit();
        progress.Report(100);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return [new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(24).Ticks }];
    }
}
