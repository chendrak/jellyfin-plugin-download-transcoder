using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DownloadTranscoder;

/// <summary>
/// State of a transcode job.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<JobState>))]
public enum JobState
{
    /// <summary>Waiting for a free transcode slot.</summary>
    Queued,

    /// <summary>ffmpeg is running.</summary>
    Running,

    /// <summary>Finished; the smaller copy is cached (or was sent, for live jobs).</summary>
    Completed,

    /// <summary>ffmpeg failed.</summary>
    Failed,

    /// <summary>Stopped before finishing.</summary>
    Cancelled
}

/// <summary>
/// One transcode, running or finished.
/// </summary>
public class TranscodeJob
{
    /// <summary>Gets or sets the job id.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the item name, e.g. "For All Mankind S04E01 - Glasnost".</summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>Gets or sets the profile name.</summary>
    public string Profile { get; set; } = string.Empty;

    /// <summary>Gets or sets who asked for it: a device, or the scheduled task.</summary>
    public string RequestedBy { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether this is a live transcode streamed straight to a client.</summary>
    public bool Live { get; set; }

    /// <summary>Gets or sets the state.</summary>
    public JobState State { get; set; } = JobState.Queued;

    /// <summary>Gets or sets when it was queued.</summary>
    public DateTime QueuedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Gets or sets when ffmpeg started.</summary>
    public DateTime? StartedUtc { get; set; }

    /// <summary>Gets or sets when it ended.</summary>
    public DateTime? EndedUtc { get; set; }

    /// <summary>Gets or sets the media duration in seconds.</summary>
    public double DurationSeconds { get; set; }

    /// <summary>Gets or sets how far ffmpeg has got, in seconds of media.</summary>
    public double ProcessedSeconds { get; set; }

    /// <summary>Gets or sets the encoding speed relative to real time (2.0 = twice as fast).</summary>
    public double Speed { get; set; }

    /// <summary>Gets or sets the source file size in bytes.</summary>
    public long SourceBytes { get; set; }

    /// <summary>Gets or sets the output size in bytes so far.</summary>
    public long OutputBytes { get; set; }

    /// <summary>Gets or sets the encoder used, e.g. "GPU (qsv: h264_qsv)" or "CPU (libx264)".</summary>
    public string Encoder { get; set; } = string.Empty;

    /// <summary>Gets or sets the GPU error, when the GPU failed and the CPU took over.</summary>
    public string? GpuError { get; set; }

    /// <summary>Gets or sets a value indicating whether someone pressed Cancel on the activity page.</summary>
    public bool CancelRequested { get; set; }

    /// <summary>Gets or sets the cancellation source for a running job.</summary>
    [JsonIgnore]
    public CancellationTokenSource? Cancellation { get; set; }

    /// <summary>Gets or sets the error, for failed jobs.</summary>
    public string? Error { get; set; }

    /// <summary>Gets the progress in percent, if known.</summary>
    public double? Percent => State == JobState.Completed ? 100
        : DurationSeconds > 0 ? Math.Min(99.9, 100 * ProcessedSeconds / DurationSeconds) : null;

    /// <summary>Gets the estimated seconds left, if known.</summary>
    public double? EtaSeconds => State == JobState.Running && Speed > 0 && DurationSeconds > 0
        ? Math.Max(0, (DurationSeconds - ProcessedSeconds) / Speed) : null;
}

/// <summary>
/// Keeps running jobs and a persisted history of finished ones.
/// </summary>
public sealed class JobTracker
{
    private const int HistorySize = 200;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly ConcurrentDictionary<Guid, TranscodeJob> _active = new();
    private readonly List<TranscodeJob> _history = new();
    private readonly object _lock = new();
    private readonly ILogger<JobTracker> _logger;
    private bool _loaded;

    /// <summary>
    /// Initializes a new instance of the <see cref="JobTracker"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public JobTracker(ILogger<JobTracker> logger)
    {
        _logger = logger;
    }

    private static string HistoryFile => Path.Combine(
        Plugin.Instance?.CachePath ?? Path.Combine(Path.GetTempPath(), "download-transcoder"),
        "history.json");

    /// <summary>
    /// Registers a new job.
    /// </summary>
    /// <param name="job">The job.</param>
    /// <returns>The same job.</returns>
    public TranscodeJob Add(TranscodeJob job)
    {
        _active[job.Id] = job;
        return job;
    }

    /// <summary>
    /// Moves a job to the history.
    /// </summary>
    /// <param name="job">The job.</param>
    /// <param name="state">Final state.</param>
    /// <param name="error">Error text for failures.</param>
    public void Finish(TranscodeJob job, JobState state, string? error = null)
    {
        job.State = state;
        job.Error = error;
        job.EndedUtc = DateTime.UtcNow;
        job.Cancellation?.Dispose();
        job.Cancellation = null;
        _active.TryRemove(job.Id, out _);
        lock (_lock)
        {
            EnsureLoaded();
            _history.Insert(0, job);
            if (_history.Count > HistorySize)
            {
                _history.RemoveRange(HistorySize, _history.Count - HistorySize);
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(HistoryFile)!);
                File.WriteAllText(HistoryFile, JsonSerializer.Serialize(_history, JsonOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not save transcode history");
            }
        }
    }

    /// <summary>
    /// Stops a running or queued job.
    /// </summary>
    /// <param name="id">The job id.</param>
    /// <returns>False when no such job is running.</returns>
    public bool Cancel(Guid id)
    {
        if (!_active.TryGetValue(id, out var job) || job.Cancellation is null)
        {
            return false;
        }

        job.CancelRequested = true;
        job.Cancellation.Cancel();
        return true;
    }

    /// <summary>
    /// Finds a finished job.
    /// </summary>
    /// <param name="id">The job id.</param>
    /// <returns>The job or null.</returns>
    public TranscodeJob? FindInHistory(Guid id)
    {
        lock (_lock)
        {
            EnsureLoaded();
            return _history.FirstOrDefault(j => j.Id == id);
        }
    }

    /// <summary>
    /// Gets running and queued jobs, oldest first.
    /// </summary>
    /// <returns>The jobs.</returns>
    public IReadOnlyList<TranscodeJob> Active() => _active.Values.OrderBy(j => j.QueuedUtc).ToList();

    /// <summary>
    /// Gets finished jobs, newest first.
    /// </summary>
    /// <returns>The jobs.</returns>
    public IReadOnlyList<TranscodeJob> History()
    {
        lock (_lock)
        {
            EnsureLoaded();
            return _history.ToList();
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        try
        {
            if (File.Exists(HistoryFile))
            {
                _history.AddRange(JsonSerializer.Deserialize<List<TranscodeJob>>(File.ReadAllText(HistoryFile), JsonOptions) ?? []);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read transcode history");
        }
    }
}

/// <summary>
/// The outcome of one GPU test run.
/// </summary>
public class GpuTestResult
{
    /// <summary>Gets or sets the test clip, e.g. "4K HEVC 10-bit".</summary>
    public string Sample { get; set; } = string.Empty;

    /// <summary>Gets or sets the encoder description.</summary>
    public string Encoder { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether this used the GPU.</summary>
    public bool IsHardware { get; set; }

    /// <summary>Gets or sets a value indicating whether decode and scaling also ran on the GPU.</summary>
    public bool FullPipeline { get; set; }

    /// <summary>Gets or sets a value indicating whether it worked.</summary>
    public bool Ok { get; set; }

    /// <summary>Gets or sets how long it took.</summary>
    public double Seconds { get; set; }

    /// <summary>Gets or sets the speed relative to real time.</summary>
    public double Speed { get; set; }

    /// <summary>Gets or sets the ffmpeg error.</summary>
    public string? Error { get; set; }
}
