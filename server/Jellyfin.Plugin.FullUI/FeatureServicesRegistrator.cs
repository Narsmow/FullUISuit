using Jellyfin.Plugin.FullUI.Discovery;
using Jellyfin.Plugin.FullUI.Metrics;
using Jellyfin.Plugin.FullUI.Ops;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jellyfin.Plugin.FullUI;

/// <summary>
/// Registers onboarding, hidden titles, search suggestions, interaction metrics and the health / backup tools.
/// Interfaces the recommendation engine may want to replace or consume (<see cref="IHiddenItems"/>,
/// <see cref="IRowEngagementProvider"/>, <see cref="ICastIndex"/>, <see cref="ITrendingProvider"/>) are registered here with
/// TryAdd for the replaceable ones, so an engine-side implementation registered earlier wins.
/// </summary>
public class FeatureServicesRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Onboarding, hidden titles, search
        serviceCollection.AddSingleton<OnboardingService>();
        serviceCollection.AddSingleton<HiddenItemsService>();
        serviceCollection.TryAddSingleton<IHiddenItems>(sp => sp.GetRequiredService<HiddenItemsService>());
        serviceCollection.TryAddSingleton<IHomeInvalidator, HomeServiceInvalidator>();
        serviceCollection.AddSingleton<ICastIndex, CatalogCastIndex>();
        serviceCollection.AddSingleton<SuggestService>();

        // Measurement
        serviceCollection.AddSingleton<InteractionLog>();
        serviceCollection.AddSingleton<EventRateLimiter>();
        serviceCollection.AddSingleton<MetricsService>();
        serviceCollection.TryAddSingleton<IRowEngagementProvider>(sp => sp.GetRequiredService<MetricsService>());

        // Operations
        serviceCollection.AddSingleton<ITaskRunLog, TaskRunLog>();
        serviceCollection.AddSingleton<HealthService>();
        serviceCollection.AddSingleton<DataPortability>();
        serviceCollection.AddHostedService<TaskRunRecorderService>();
    }
}
