using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.FullUI;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // The TMDB v3 key travels in the query string, so keep request URLs out of HttpClientFactory's own logging.
        serviceCollection.AddHttpClient("FullUI").RemoveAllLoggers();

        // Hooking into the web page needs the File Transformation plugin to be constructed, which has not happened
        // yet while services are being registered. The hosted service does it after startup, with retries.
        serviceCollection.AddHostedService<WebInjectionHostedService>();
    }
}
