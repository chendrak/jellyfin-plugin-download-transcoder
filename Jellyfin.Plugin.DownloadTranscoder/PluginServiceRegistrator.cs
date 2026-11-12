using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.DownloadTranscoder;

/// <summary>
/// Registers the plugin's services and the download result filter.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<TranscodeService>();
        serviceCollection.AddSingleton<DownloadResultFilter>();
        serviceCollection.Configure<MvcOptions>(options => options.Filters.AddService<DownloadResultFilter>());
    }
}
