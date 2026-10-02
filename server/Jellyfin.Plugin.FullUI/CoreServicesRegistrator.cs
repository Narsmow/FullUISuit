using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Discovery;
using Jellyfin.Plugin.FullUI.Events;
using Jellyfin.Plugin.FullUI.Library;
using Jellyfin.Plugin.FullUI.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.FullUI;

/// <summary>
/// Registers the core (store, catalog, events, home) services. Jellyfin loads every registrator in the assembly.
/// Scheduled tasks are NOT registered here: Jellyfin discovers every <c>IScheduledTask</c> in the assembly itself
/// (registering one as well would list it twice).
/// </summary>
public class CoreServicesRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<PluginStore>();
        serviceCollection.AddSingleton<ICatalog, JellyfinCatalog>();
        serviceCollection.AddSingleton<INextUpSource, JellyfinNextUpSource>();
        serviceCollection.AddSingleton<IWatchStateSource, JellyfinWatchStateSource>();
        serviceCollection.AddSingleton<HomeService>();
        serviceCollection.AddSingleton<IHomeRowsProvider, HomePlaylistRowsProvider>();
        serviceCollection.AddSingleton<EventTracker>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<EventTracker>());
        serviceCollection.AddHostedService<PlaybackBackfillService>();
    }
}
