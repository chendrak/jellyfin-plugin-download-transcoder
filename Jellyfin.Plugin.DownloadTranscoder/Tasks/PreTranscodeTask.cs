using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DownloadTranscoder.Tasks;

/// <summary>
/// Pre-transcodes the newest items of the chosen libraries so downloads are instant and seekable.
/// </summary>
public class PreTranscodeTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly TranscodeService _transcodeService;
    private readonly ILogger<PreTranscodeTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PreTranscodeTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="transcodeService">Transcode service.</param>
    /// <param name="logger">Logger.</param>
    public PreTranscodeTask(ILibraryManager libraryManager, TranscodeService transcodeService, ILogger<PreTranscodeTask> logger)
    {
        _libraryManager = libraryManager;
        _transcodeService = transcodeService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Pre-transcode downloads";

    /// <inheritdoc />
    public string Key => "DownloadTranscoderPreTranscode";

    /// <inheritdoc />
    public string Description => "Prepares smaller download copies of the newest items in the libraries chosen in the Download Transcoder settings.";

    /// <inheritdoc />
    public string Category => "Download Transcoder";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled || cfg.MaxCacheSizeGb <= 0)
        {
            return;
        }

        var profile = DeviceDetector.FindByName(cfg, cfg.PreTranscodeProfile);
        if (profile is null)
        {
            _logger.LogWarning("Pre-transcode profile {Profile} does not exist", cfg.PreTranscodeProfile);
            return;
        }

        var items = new List<BaseItem>();
        foreach (var libraryId in cfg.PreTranscodeLibraryIds)
        {
            if (!Guid.TryParse(libraryId, out var id))
            {
                continue;
            }

            items.AddRange(_libraryManager.GetItemList(new InternalItemsQuery
            {
                AncestorIds = [id],
                Recursive = true,
                IsVirtualItem = false,
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video, BaseItemKind.MusicVideo],
                OrderBy = [(ItemSortBy.DateCreated, Jellyfin.Database.Implementations.Enums.SortOrder.Descending)],
                Limit = Math.Max(1, cfg.PreTranscodeNewestItems)
            }));
        }

        var todo = items.Where(i => _transcodeService.ShouldTranscode(i, profile) && _transcodeService.TryGetCached(i, profile) is null).ToList();
        _logger.LogInformation("Pre-transcoding {Count} items for download", todo.Count);
        for (var i = 0; i < todo.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _transcodeService.TranscodeToCacheAsync(todo[i], profile, cancellationToken).ConfigureAwait(false);
            progress.Report(100.0 * (i + 1) / todo.Count);
        }
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(3).Ticks }
        ];
    }
}
