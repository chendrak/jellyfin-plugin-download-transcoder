using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Queries;
using MediaBrowser.Controller.Library;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Devices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DownloadTranscoder.Api;

/// <summary>
/// Admin API for the settings and activity pages.
/// </summary>
[ApiController]
[Route("DownloadTranscoder")]
[Authorize(Policy = Policies.RequiresElevation)]
public class DownloadTranscoderController : ControllerBase
{
    private readonly IDeviceManager _deviceManager;
    private readonly JobTracker _jobs;
    private readonly ILibraryManager _libraryManager;
    private readonly IServerConfigurationManager _serverConfig;
    private readonly TranscodeService _transcodeService;

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadTranscoderController"/> class.
    /// </summary>
    /// <param name="deviceManager">Device manager.</param>
    /// <param name="jobs">Job tracker.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="serverConfig">Server configuration.</param>
    /// <param name="transcodeService">Transcode service.</param>
    public DownloadTranscoderController(IDeviceManager deviceManager, JobTracker jobs, ILibraryManager libraryManager, IServerConfigurationManager serverConfig, TranscodeService transcodeService)
    {
        _transcodeService = transcodeService;
        _serverConfig = serverConfig;
        _deviceManager = deviceManager;
        _jobs = jobs;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Reports Jellyfin's hardware acceleration settings, for the settings page.
    /// </summary>
    /// <returns>The status.</returns>
    [HttpGet("Status")]
    public ActionResult<StatusResponse> GetStatus()
    {
        var options = _serverConfig.GetEncodingOptions();
        return Ok(new StatusResponse
        {
            HardwareAccelerationType = options.HardwareAccelerationType.ToString(),
            HardwareEncodingEnabled = options.EnableHardwareEncoding
        });
    }

    /// <summary>
    /// Lists running and recent transcodes.
    /// </summary>
    /// <returns>Active jobs and history.</returns>
    [HttpGet("Jobs")]
    public ActionResult<JobsResponse> GetJobs()
        => Ok(new JobsResponse { Active = _jobs.Active(), History = _jobs.History() });

    /// <summary>
    /// Cancels a running or queued transcode.
    /// </summary>
    /// <param name="id">Job id.</param>
    /// <returns>No content, or 404 when it isn't running.</returns>
    [HttpPost("Jobs/{id}/Cancel")]
    public ActionResult CancelJob([FromRoute] Guid id) => _jobs.Cancel(id) ? NoContent() : NotFound();

    /// <summary>
    /// Transcodes a finished, failed or cancelled job's item again with the same profile.
    /// </summary>
    /// <param name="id">Job id from the history.</param>
    /// <returns>No content, or 404 when the job, item or profile is gone.</returns>
    [HttpPost("Jobs/{id}/Retry")]
    public ActionResult RetryJob([FromRoute] Guid id)
    {
        var job = _jobs.FindInHistory(id);
        var item = job is null ? null : _libraryManager.GetItemById(job.ItemId);
        var profile = job is null ? null : DeviceDetector.FindByName(Plugin.Instance!.Configuration, job.Profile);
        if (item is null || profile is null)
        {
            return NotFound();
        }

        _transcodeService.Retry(item, profile);
        return NoContent();
    }

    /// <summary>
    /// Runs a short test transcode through each encoder (full GPU, GPU encode only, CPU).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The results.</returns>
    [HttpPost("TestGpu")]
    public async Task<ActionResult<IReadOnlyList<GpuTestResult>>> TestGpu(CancellationToken cancellationToken)
        => Ok(await _transcodeService.TestGpuAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Lists the cached smaller copies.
    /// </summary>
    /// <returns>The cache entries.</returns>
    [HttpGet("Cache")]
    public ActionResult<CacheResponse> GetCache()
    {
        var plugin = Plugin.Instance!;
        var dir = new DirectoryInfo(plugin.CachePath);
        var files = dir.Exists ? dir.EnumerateFiles("*.mp4").ToList() : [];
        var entries = files.Select(f =>
        {
            var id = Guid.TryParse(f.Name.Split('_')[0], out var g) ? g : Guid.Empty;
            var item = id == Guid.Empty ? null : _libraryManager.GetItemById(id);
            return new CacheEntry
            {
                FileName = f.Name,
                ItemName = item is null ? "(removed from library)" : TranscodeService.DisplayName(item),
                Bytes = f.Length,
                CreatedUtc = f.LastWriteTimeUtc,
                LastUsedUtc = f.LastAccessTimeUtc > f.LastWriteTimeUtc ? f.LastAccessTimeUtc : f.LastWriteTimeUtc
            };
        }).OrderByDescending(e => e.LastUsedUtc).ToList();
        return Ok(new CacheResponse
        {
            Path = plugin.CachePath,
            LimitBytes = (long)plugin.Configuration.MaxCacheSizeGb * 1024 * 1024 * 1024,
            TotalBytes = entries.Sum(e => e.Bytes),
            Entries = entries
        });
    }

    /// <summary>
    /// Deletes one cached copy.
    /// </summary>
    /// <param name="fileName">The cache file name.</param>
    /// <returns>No content.</returns>
    [HttpDelete("Cache/{fileName}")]
    public ActionResult DeleteCacheEntry([FromRoute] string fileName)
    {
        if (fileName.Contains('/', StringComparison.Ordinal) || fileName.Contains('\\', StringComparison.Ordinal) || !fileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest();
        }

        var path = Path.Combine(Plugin.Instance!.CachePath, fileName);
        if (!System.IO.File.Exists(path))
        {
            return NotFound();
        }

        System.IO.File.Delete(path);
        return NoContent();
    }

    /// <summary>
    /// Lists known devices with their detected kind and the profile their downloads use.
    /// </summary>
    /// <returns>The devices.</returns>
    [HttpGet("Devices")]
    public ActionResult<IEnumerable<DeviceProfileInfo>> GetDevices()
    {
        var cfg = Plugin.Instance!.Configuration;
        var devices = _deviceManager.GetDeviceInfos(new DeviceQuery()).Items;
        return Ok(devices
            .Where(d => d.Id is not null)
            .OrderByDescending(d => d.DateLastActivity)
            .Select(d => new DeviceProfileInfo
            {
                Id = d.Id!,
                Name = string.IsNullOrEmpty(d.CustomName) ? d.Name ?? string.Empty : d.CustomName,
                AppName = d.AppName ?? string.Empty,
                LastUserName = d.LastUserName ?? string.Empty,
                DetectedKind = DeviceDetector.Detect(d.AppName, d.Name, null).ToString(),
                Profile = Describe(DeviceDetector.SelectProfile(cfg, d.Id, d.AppName, d.Name, null)),
                AutoProfile = Describe(DeviceDetector.SelectProfile(cfg, null, d.AppName, d.Name, null))
            })
            .ToList());
    }

    private static string Describe(Configuration.TranscodeProfile? profile)
        => profile is null ? "None (original file)"
            : profile.Transcode ? profile.Name
            : profile.Name + " (original file)";
}

/// <summary>
/// A device and the profile it gets.
/// </summary>
public class DeviceProfileInfo
{
    /// <summary>Gets or sets the device id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the device name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the app name.</summary>
    public string AppName { get; set; } = string.Empty;

    /// <summary>Gets or sets the last user.</summary>
    public string LastUserName { get; set; } = string.Empty;

    /// <summary>Gets or sets the detected kind.</summary>
    public string DetectedKind { get; set; } = string.Empty;

    /// <summary>Gets or sets the profile that applies.</summary>
    public string Profile { get; set; } = string.Empty;

    /// <summary>Gets or sets the profile automatic detection would pick.</summary>
    public string AutoProfile { get; set; } = string.Empty;
}

/// <summary>
/// Running and finished jobs.
/// </summary>
public class JobsResponse
{
    /// <summary>Gets or sets the running and queued jobs.</summary>
    public IReadOnlyList<TranscodeJob> Active { get; set; } = [];

    /// <summary>Gets or sets the finished jobs, newest first.</summary>
    public IReadOnlyList<TranscodeJob> History { get; set; } = [];
}

/// <summary>
/// The transcode cache.
/// </summary>
public class CacheResponse
{
    /// <summary>Gets or sets the cache folder.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the size limit in bytes.</summary>
    public long LimitBytes { get; set; }

    /// <summary>Gets or sets the total size in bytes.</summary>
    public long TotalBytes { get; set; }

    /// <summary>Gets or sets the cached files.</summary>
    public IReadOnlyList<CacheEntry> Entries { get; set; } = [];
}

/// <summary>
/// One cached file.
/// </summary>
public class CacheEntry
{
    /// <summary>Gets or sets the file name.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Gets or sets the item name.</summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>Gets or sets the size in bytes.</summary>
    public long Bytes { get; set; }

    /// <summary>Gets or sets when it was made.</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>Gets or sets when it was last downloaded.</summary>
    public DateTime LastUsedUtc { get; set; }
}

/// <summary>
/// Jellyfin's hardware acceleration settings.
/// </summary>
public class StatusResponse
{
    /// <summary>Gets or sets the hardware acceleration type, e.g. "qsv" or "none".</summary>
    public string HardwareAccelerationType { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether hardware encoding is enabled in Jellyfin.</summary>
    public bool HardwareEncodingEnabled { get; set; }
}
