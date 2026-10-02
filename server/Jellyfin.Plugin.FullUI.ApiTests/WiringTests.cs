using System.Reflection;
using Jellyfin.Plugin.FullUI.Api;
using Jellyfin.Plugin.FullUI.Discovery;
using Jellyfin.Plugin.FullUI.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullUI.ApiTests;

/// <summary>
/// Builds the plugin's real service graph (every registrator in the assembly, exactly as Jellyfin runs them) against stubbed
/// Jellyfin services, so a missing registration or a constructor that cannot be satisfied fails here and not on a user's server.
/// </summary>
public sealed class DependencyInjectionTests : IDisposable
{
    private readonly DiskStore _disk = new();

    public void Dispose() => _disk.Dispose();

    private ServiceProvider Build()
    {
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddSingleton(Stub.Make<IApplicationPaths>(("get_DataPath", _ => _disk.Dir)));
        sc.AddSingleton(Stub.Make<ILibraryManager>());
        sc.AddSingleton(Stub.Make<IUserManager>());
        sc.AddSingleton(Stub.Make<ISessionManager>());
        sc.AddSingleton(Stub.Make<IUserDataManager>());
        sc.AddSingleton(Stub.Make<IPlaylistManager>());
        sc.AddSingleton(Stub.Make<ITVSeriesManager>());
        var host = Stub.Make<IServerApplicationHost>();
        foreach (var t in PluginTypes<IPluginServiceRegistrator>())
        {
            ((IPluginServiceRegistrator)Activator.CreateInstance(t)!).RegisterServices(sc, host);
        }

        return sc.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static IEnumerable<Type> PluginTypes<T>()
        => typeof(Plugin).Assembly.GetTypes().Where(t => typeof(T).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);

    [Fact]
    public void EveryRegistratorRuns_AndTheWholeGraphValidates()
    {
        Assert.NotEmpty(PluginTypes<IPluginServiceRegistrator>());
        using var sp = Build(); // ValidateOnBuild throws on any unresolvable constructor
        Assert.NotNull(sp);
    }

    [Fact]
    public void EveryHostedServiceResolves()
    {
        using var sp = Build();
        var names = sp.GetServices<IHostedService>().Select(h => h.GetType().Name).ToList();
        Assert.Contains("EventTracker", names);
        Assert.Contains("PlaybackBackfillService", names);
        Assert.Contains("RequestSyncHostedService", names);
        Assert.Contains("WebInjectionHostedService", names);
    }

    [Fact]
    public void EveryScheduledTaskCanBeBuiltFromTheContainer_WithUniqueKeys()
    {
        using var sp = Build();
        var tasks = PluginTypes<IScheduledTask>()
            .Select(t => (IScheduledTask)ActivatorUtilities.CreateInstance(sp, t))
            .ToList();

        Assert.Equal(5, tasks.Count);
        Assert.Equal(tasks.Count, tasks.Select(t => t.Key).Distinct().Count());
        Assert.All(tasks, t => Assert.False(string.IsNullOrWhiteSpace(t.Name)));
    }

    [Fact]
    public void ScheduledTasks_AreNotAlsoRegisteredInTheContainer()
    {
        // Jellyfin finds IScheduledTask implementations by scanning the assembly. Registering one here as well would list it twice.
        using var sp = Build();
        Assert.Empty(sp.GetServices<IScheduledTask>());
    }

    [Fact]
    public void EveryControllerCanBeBuiltFromTheContainer()
    {
        using var sp = Build();
        var controllers = PluginTypes<ControllerBase>().ToList();
        Assert.True(controllers.Count >= 4);
        foreach (var t in controllers)
        {
            Assert.NotNull(ActivatorUtilities.CreateInstance(sp, t));
        }
    }

    [Fact]
    public void PlaylistProviderIsRegistered_SoTheSettingDoesSomething()
    {
        using var sp = Build();
        Assert.IsType<HomePlaylistRowsProvider>(sp.GetRequiredService<IHomeRowsProvider>());
    }

    [Fact]
    public void HomeServiceIsASingleton_SoItsCacheIsShared()
    {
        using var sp = Build();
        Assert.Same(sp.GetRequiredService<HomeService>(), sp.GetRequiredService<HomeService>());
    }
}

/// <summary>Every endpoint must state its audience. A new action without [Authorize] must fail the build, not ship open.</summary>
public class AuthorizationTests
{
    private static IEnumerable<(Type Controller, MethodInfo Action)> Actions()
        => typeof(Plugin).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes().OfType<HttpMethodAttribute>().Any())
                .Select(m => (t, m)));

    private static bool HasAuthorize(Type c, MethodInfo m)
        => m.GetCustomAttributes<AuthorizeAttribute>(true).Any() || c.GetCustomAttributes<AuthorizeAttribute>(true).Any();

    [Fact]
    public void EveryAction_IsAuthorizedOrExplicitlyAnonymous()
    {
        var actions = Actions().ToList();
        Assert.True(actions.Count >= 15, "reflection found too few actions; the test is not looking at the controllers");

        var open = actions
            .Where(a => !HasAuthorize(a.Controller, a.Action) && !a.Action.GetCustomAttributes<AllowAnonymousAttribute>(true).Any())
            .Select(a => $"{a.Controller.Name}.{a.Action.Name}")
            .ToList();
        Assert.Empty(open);
    }

    [Fact]
    public void OnlyTheStaticBundleIsAnonymous()
    {
        var anonymous = Actions()
            .Where(a => a.Action.GetCustomAttributes<AllowAnonymousAttribute>(true).Any() || a.Controller.GetCustomAttributes<AllowAnonymousAttribute>(true).Any())
            .Select(a => $"{a.Controller.Name}.{a.Action.Name}")
            .ToList();
        Assert.Equal(new[] { "FullUIController.WebAsset" }, anonymous);
    }

    [Fact]
    public void AdminAndSettingsEndpoints_RequireElevation()
    {
        string? Policy(Type c, MethodInfo m)
            => m.GetCustomAttributes<AuthorizeAttribute>(true).Select(a => a.Policy).FirstOrDefault(p => p is not null)
               ?? c.GetCustomAttributes<AuthorizeAttribute>(true).Select(a => a.Policy).FirstOrDefault(p => p is not null);

        var admin = new[] { "Tmdb/Test", "Ollama/Test", "Admin/Requests", "Admin/Requests/Status", "Admin/Requests.csv", "Admin/Rebuild", "Admin/Injection" };
        foreach (var route in admin)
        {
            var match = Actions().Single(a => a.Action.GetCustomAttributes<HttpMethodAttribute>().Any(h => h.Template == route));
            Assert.Equal("RequiresElevation", Policy(match.Controller, match.Action));
        }
    }

    [Fact]
    public void PerUserEndpoints_DoNotAcceptAUserIdFromTheClient()
    {
        // The user always comes from the auth token. A parameter or request field named like a user id would let one user act as another.
        var parameters = Actions().SelectMany(a => a.Action.GetParameters()).ToList();
        Assert.DoesNotContain(parameters, p => p.Name!.Contains("userid", StringComparison.OrdinalIgnoreCase));
        var dtos = new[] { typeof(RateRequest), typeof(MyListRequest), typeof(VoteRequest), typeof(MarkReadRequest) };
        Assert.All(dtos, t => Assert.DoesNotContain(t.GetProperties(), p => p.Name.Contains("UserId", StringComparison.OrdinalIgnoreCase)));
    }
}
