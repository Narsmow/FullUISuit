using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Events;
using Jellyfin.Plugin.FullUI.Library;
using Jellyfin.Plugin.FullUI.Services;
using Jellyfin.Plugin.FullUI.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.FullUI;

/// <summary>Registers the core (store, catalog, events, home) services. Jellyfin loads every registrator in the assembly.</summary>
public class CoreServicesRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<PluginStore>();
        serviceCollection.AddSingleton<ICatalog, JellyfinCatalog>();
        serviceCollection.AddSingleton<HomeService>();
        serviceCollection.AddSingleton<EventTracker>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<EventTracker>());
        serviceCollection.AddSingleton<IScheduledTask, RebuildRecsTask>();
    }
}
