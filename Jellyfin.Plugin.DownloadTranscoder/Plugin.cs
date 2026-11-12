using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.DownloadTranscoder.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.DownloadTranscoder;

/// <summary>
/// Transcodes media served by the download endpoint so offline copies are smaller.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        DefaultCachePath = System.IO.Path.Combine(applicationPaths.CachePath, "download-transcoder");
    }

    /// <inheritdoc />
    public override string Name => "Download Transcoder";

    /// <inheritdoc />
    public override string Description => "Serves smaller, transcoded files when clients download media.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("f008c7ae-97d2-4148-8d02-e281034b764d");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Gets the cache folder used when none is configured.
    /// </summary>
    public string DefaultCachePath { get; }

    /// <summary>
    /// Gets the cache folder that is in effect.
    /// </summary>
    public string CachePath => string.IsNullOrWhiteSpace(Configuration.CachePath) ? DefaultCachePath : Configuration.CachePath;

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                DisplayName = "Download Transcoder",
                EnableInMainMenu = true,
                MenuIcon = "video_settings",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            },
            new PluginPageInfo
            {
                Name = "DownloadTranscoderActivity",
                DisplayName = "Download Activity",
                EnableInMainMenu = true,
                MenuIcon = "download",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.activityPage.html", GetType().Namespace)
            }
        ];
    }
}
