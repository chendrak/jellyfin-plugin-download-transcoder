using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.DownloadTranscoder.Configuration;

/// <summary>
/// What to do when a download is requested and no transcoded copy is cached yet.
/// </summary>
public enum OnDemandMode
{
    /// <summary>
    /// Transcode while sending, and keep the result for next time.
    /// </summary>
    Live = 0,

    /// <summary>
    /// Send the original file. Only pre-transcoded copies are served.
    /// </summary>
    Original = 1,

    /// <summary>
    /// Answer 503 "try again later" and transcode in the background.
    /// </summary>
    Queue = 2
}

/// <summary>
/// Plugin settings.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether downloads are transcoded at all.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether every audio track is kept (otherwise only the default one).
    /// </summary>
    public bool KeepAllAudioTracks { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether text subtitles are embedded in the output.
    /// </summary>
    public bool KeepTextSubtitles { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether HDR sources are tone mapped to SDR.
    /// </summary>
    public bool TonemapHdr { get; set; } = true;

    /// <summary>
    /// Gets or sets extra ffmpeg arguments placed before the input, e.g. hardware decoding flags.
    /// </summary>
    public string ExtraInputArguments { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a custom video filter chain replacing the built-in scale/tonemap filter. Use {height} for the max height.
    /// </summary>
    public string CustomVideoFilter { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether files already at or below the target are sent untouched.
    /// </summary>
    public bool SkipIfAlreadySmall { get; set; } = true;

    /// <summary>
    /// Gets or sets the quality profiles. The first matching profile wins; see <see cref="TranscodeProfile"/>.
    /// </summary>
    public TranscodeProfile[] Profiles { get; set; } = TranscodeProfile.Defaults();

    /// <summary>
    /// Gets or sets explicit device to profile assignments, which override detection.
    /// </summary>
    public DeviceAssignment[] DeviceAssignments { get; set; } = Array.Empty<DeviceAssignment>();

    /// <summary>
    /// Gets or sets what happens when nothing is cached yet.
    /// </summary>
    public OnDemandMode OnDemand { get; set; } = OnDemandMode.Queue;

    /// <summary>
    /// Gets or sets the container used while transcoding live: "mp4" (fragmented) or "mpegts".
    /// </summary>
    public string LiveContainer { get; set; } = "mp4";

    /// <summary>
    /// Gets or sets the cache folder. Empty uses Jellyfin's cache folder.
    /// </summary>
    public string CachePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the maximum cache size in GB. 0 disables caching.
    /// </summary>
    public int MaxCacheSizeGb { get; set; } = 50;

    /// <summary>
    /// Gets or sets the number of transcodes that may run at the same time (live downloads excluded).
    /// </summary>
    public int MaxConcurrentTranscodes { get; set; } = 1;

    /// <summary>
    /// Gets or sets the libraries whose newest items are pre-transcoded by the scheduled task.
    /// </summary>
    public string[] PreTranscodeLibraryIds { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the profile the scheduled task pre-transcodes with.
    /// </summary>
    public string PreTranscodeProfile { get; set; } = "Phone";

    /// <summary>
    /// Gets or sets how many of the newest items per library the scheduled task pre-transcodes.
    /// </summary>
    public int PreTranscodeNewestItems { get; set; } = 20;
}
