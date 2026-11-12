using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.DownloadTranscoder.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DownloadTranscoder;

/// <summary>
/// Swaps the original file returned by <c>GET /Items/{itemId}/Download</c> for a transcoded one.
/// Runs after Jellyfin's own controller, so authentication, the download permission and
/// the download activity log are all handled by Jellyfin as usual.
/// </summary>
public sealed class DownloadResultFilter : IAsyncResultFilter
{
    private const string ClientClaim = "Jellyfin-Client";

    private readonly ILibraryManager _libraryManager;
    private readonly TranscodeService _transcodeService;
    private readonly ILogger<DownloadResultFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadResultFilter"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="transcodeService">Transcode service.</param>
    /// <param name="logger">Logger.</param>
    public DownloadResultFilter(ILibraryManager libraryManager, TranscodeService transcodeService, ILogger<DownloadResultFilter> logger)
    {
        _libraryManager = libraryManager;
        _transcodeService = transcodeService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        try
        {
            Replace(context);
        }
        catch (Exception ex)
        {
            // Never break downloads: fall back to the original file.
            _logger.LogError(ex, "Download Transcoder failed; sending the original file");
        }

        await next().ConfigureAwait(false);
    }

    private void Replace(ResultExecutingContext context)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled
            || context.Result is not PhysicalFileResult
            || context.ActionDescriptor is not ControllerActionDescriptor action
            || action.ControllerName != "Library"
            || action.ActionName != "GetDownload")
        {
            return;
        }

        var request = context.HttpContext.Request;
        if (IsTrue(request.Query["original"]) || IsTrue(request.Query["static"]))
        {
            return;
        }

        var client = context.HttpContext.User.FindFirst(ClientClaim)?.Value ?? string.Empty;
        if (!ClientAllowed(cfg, client))
        {
            return;
        }

        if (!Guid.TryParse(Convert.ToString(context.RouteData.Values["itemId"], CultureInfo.InvariantCulture), out var itemId))
        {
            return;
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null || !_transcodeService.ShouldTranscode(item))
        {
            return;
        }

        var cached = _transcodeService.TryGetCached(item);
        if (cached is not null)
        {
            _logger.LogInformation("Serving cached transcode of {Name} to {Client}", item.Name, client);
            context.Result = new PhysicalFileResult(cached, "video/mp4")
            {
                FileDownloadName = TranscodeService.DownloadName(item, ".mp4"),
                EnableRangeProcessing = true
            };
            return;
        }

        switch (cfg.OnDemand)
        {
            case OnDemandMode.Live:
                _logger.LogInformation("Transcoding {Name} live for {Client}", item.Name, client);
                context.Result = new LiveTranscodeResult(_transcodeService, item);
                break;
            case OnDemandMode.Queue:
                _logger.LogInformation("Queued {Name} for transcoding; asked {Client} to retry later", item.Name, client);
                _transcodeService.Queue(item);
                context.HttpContext.Response.Headers.RetryAfter = "600";
                context.Result = new ObjectResult("A smaller copy is being prepared. Try the download again in a few minutes.")
                {
                    StatusCode = StatusCodes.Status503ServiceUnavailable
                };
                break;
            default:
                break;
        }
    }

    private static bool ClientAllowed(PluginConfiguration cfg, string client)
    {
        static bool Matches(string list, string client)
            => list.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(c => string.Equals(c, client, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(cfg.ExcludedClients) && Matches(cfg.ExcludedClients, client))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(cfg.IncludedClients) || Matches(cfg.IncludedClients, client);
    }

    private static bool IsTrue(string? value)
        => value is not null && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));

    private sealed class LiveTranscodeResult : IActionResult
    {
        private readonly TranscodeService _service;
        private readonly MediaBrowser.Controller.Entities.BaseItem _item;

        public LiveTranscodeResult(TranscodeService service, MediaBrowser.Controller.Entities.BaseItem item)
        {
            _service = service;
            _item = item;
        }

        public Task ExecuteResultAsync(ActionContext context) => _service.StreamLiveAsync(_item, context.HttpContext);
    }
}
