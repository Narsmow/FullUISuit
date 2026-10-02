using Jellyfin.Plugin.FullUI.Ai;
using Jellyfin.Plugin.FullUI.Discovery;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        serviceCollection.AddSingleton<ReminderService>();
        serviceCollection.AddSingleton<NewPopularService>();
        // New & Popular uses the engine's own Trending / Top 10 rows for the user, with the signal-based provider as the fallback.
        // TryAdd: a different ITrendingProvider registered by another component still wins.
        serviceCollection.AddSingleton<SignalTrendingProvider>();
        serviceCollection.TryAddSingleton<ITrendingProvider>(sp => new EngineTrendingProvider(
            sp.GetRequiredService<Services.HomeService>(),
            sp.GetRequiredService<SignalTrendingProvider>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<EngineTrendingProvider>>()));
        serviceCollection.AddSingleton<IOllamaClient, OllamaClient>();
        serviceCollection.AddSingleton<EmbeddingIndexer>();
        serviceCollection.AddSingleton<RowTitleGenerator>();
        serviceCollection.AddSingleton<NlSearch>();
        serviceCollection.AddHostedService<RequestSyncHostedService>();
    }
}
