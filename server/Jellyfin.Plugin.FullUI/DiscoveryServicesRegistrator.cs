using Jellyfin.Plugin.FullUI.Ai;
using Jellyfin.Plugin.FullUI.Discovery;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.FullUI;

/// <summary>Registers discovery / requests / AI services. PluginStore and ICatalog are registered by the engine registrator.</summary>
public class DiscoveryServicesRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IConfigSource, PluginConfigSource>();
        serviceCollection.AddSingleton<IUserDirectory, JellyfinUserDirectory>();
        serviceCollection.AddSingleton<ITmdbClient, TmdbClient>();
        serviceCollection.AddSingleton<IRatingScorer, JellyfinRatingScorer>();
        serviceCollection.AddSingleton<ComingSoonService>();
        serviceCollection.AddSingleton<VoteService>();
        serviceCollection.AddSingleton<RequestService>();
        serviceCollection.AddSingleton<IOllamaClient, OllamaClient>();
        serviceCollection.AddSingleton<EmbeddingIndexer>();
        serviceCollection.AddSingleton<RowTitleGenerator>();
        serviceCollection.AddSingleton<NlSearch>();
        serviceCollection.AddHostedService<RequestSyncHostedService>();
    }
}
