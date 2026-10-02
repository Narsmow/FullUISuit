using System.Reflection;
using Jellyfin.Plugin.FullUI.Configuration;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Discovery;
using Jellyfin.Plugin.FullUI.Library;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.FullUI.DiscoveryTests;

public class PathsProxy : DispatchProxy
{
    public string Data { get; set; } = string.Empty;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == "get_DataPath")
        {
            return Data;
        }

        var rt = targetMethod?.ReturnType;
        return rt is { IsValueType: true } && rt != typeof(void) ? Activator.CreateInstance(rt) : null;
    }
}

public sealed class TempStore : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fullui-test-" + Guid.NewGuid().ToString("N"));

    public TempStore()
    {
        Directory.CreateDirectory(_dir);
        var p = DispatchProxy.Create<IApplicationPaths, PathsProxy>();
        ((PathsProxy)(object)p).Data = _dir;
        Store = new PluginStore(p, NullLogger<PluginStore>.Instance);
    }

    public PluginStore Store { get; }

    public void Dispose()
    {
        Store.Dispose();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }
}

public sealed class FakeConfig : IConfigSource
{
    public PluginConfiguration Current { get; set; } = new() { TmdbApiKey = "k", RequestNotifications = true };
}

public sealed class FakeUsers : IUserDirectory
{
    public Dictionary<Guid, string> Names { get; } = new();

    public IReadOnlyList<Guid> UserIds => Names.Keys.ToList();

    public string? NameOf(Guid userId) => Names.GetValueOrDefault(userId);
}

public sealed class FakeCatalog : ICatalog
{
    public List<CatalogItem> Items { get; set; } = new();

    public HashSet<Guid>? Visible { get; set; }

    public IReadOnlyList<CatalogItem> All => Items;

    public IReadOnlySet<Guid> VisibleTo(Guid userId) => Visible ?? Items.Select(i => i.Id).ToHashSet();

    public void Invalidate()
    {
    }

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }
}

public static class Make
{
    public static CatalogItem Item(string name, int? tmdb = null, CatalogKind kind = CatalogKind.Movie, string[]? genres = null, string? overview = null, int? year = null, string[]? tags = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Kind = kind,
            TmdbId = tmdb,
            Genres = genres ?? Array.Empty<string>(),
            Overview = overview,
            Year = year,
            Tags = tags ?? Array.Empty<string>(),
        };
}
