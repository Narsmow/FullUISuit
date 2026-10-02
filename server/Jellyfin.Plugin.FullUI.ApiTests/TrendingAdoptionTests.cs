using Jellyfin.Plugin.FullUI.Configuration;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Discovery;
using Jellyfin.Plugin.FullUI.DiscoveryTests;
using Jellyfin.Plugin.FullUI.Library;
using Jellyfin.Plugin.FullUI.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.FullUI.ApiTests;

/// <summary>New &amp; Popular is fed by the engine's own Trending / Top 10 rows (per-user eligible), with the signal provider as the fallback.</summary>
public class TrendingAdoptionTests : IDisposable
{
    private static readonly Guid Me = Http.User;
    private static readonly Guid[] Others = { Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"), Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003") };

    private readonly TempStore _ts = new();
    private readonly FakeCatalog _catalog = new();
    private readonly FakeConfig _config = new() { Current = new PluginConfiguration { ServerName = "Srv" } };
    private readonly FakeUsers _users = new();
    private readonly List<CatalogItem> _movies;

    public TrendingAdoptionTests()
    {
        _movies = Enumerable.Range(1, 6).Select(i => Make.Item("Movie " + i, i, genres: new[] { "Drama" }, rating: 7f + i / 10f)).ToList();
        _catalog.Items.AddRange(_movies);
        var at = DateTime.UtcNow.AddDays(-1);
        _ts.Store.Write(d =>
        {
            foreach (var u in Others)
            {
                foreach (var m in _movies)
                {
                    d.Signals.Add(new PlaySignal { UserId = u, ItemId = m.Id, At = at, Completion = 1, Completed = true });
                }
            }

            // I finished movie 1 and thumbed movie 2 down: the engine must not chart either for me.
            d.Signals.Add(new PlaySignal { UserId = Me, ItemId = _movies[0].Id, At = at, Completion = 1, Completed = true });
            d.Ratings[StoreData.UserItemKey(Me, _movies[1].Id)] = -1;
        });
    }

    public void Dispose() => _ts.Dispose();

    private HomeService Home() => new(_ts.Store, _catalog, NullLogger<HomeService>.Instance, _config);

    private SignalTrendingProvider Signals() => new(_ts.Store, _catalog, _config, _users);

    private EngineTrendingProvider Engine(HomeService? home = null, ITrendingProvider? fallback = null)
        => new(home ?? Home(), fallback ?? Signals(), NullLogger<EngineTrendingProvider>.Instance);

    private sealed class FixedProvider : ITrendingProvider
    {
        public List<Guid> Ids { get; } = new();

        public IReadOnlyList<Guid> Trending(Guid userId, int take) => Ids;

        public IReadOnlyList<Guid> TopTen(Guid userId, CatalogKind kind) => Ids;
    }

    [Fact]
    public void Top_ten_comes_from_the_engine_and_is_eligible_for_this_user()
    {
        var ids = Engine().TopTen(Me, CatalogKind.Movie);

        Assert.NotEmpty(ids);
        Assert.DoesNotContain(_movies[0].Id, ids);   // finished by me
        Assert.DoesNotContain(_movies[1].Id, ids);   // thumbed down by me
        Assert.Equal(4, ids.Count);

        // The signal-only default still charts the finished one, which is exactly what the engine fixes.
        Assert.Contains(_movies[0].Id, Signals().TopTen(Me, CatalogKind.Movie));
    }

    [Fact]
    public void Trending_comes_from_the_engines_trending_row()
    {
        var ids = Engine().Trending(Me, 20);
        var home = Home().GetHomeStrict(Me);
        var row = home.Rows.FirstOrDefault(r => r.Id == "trending");

        Assert.NotNull(row);
        Assert.Equal(row!.Items.Select(c => c.Id).ToArray(), ids.Select(i => i.ToString("N")).ToArray());
        Assert.DoesNotContain(_movies[1].Id, ids);
    }

    [Fact]
    public void The_new_and_popular_page_shows_the_engines_lists_with_ranks()
    {
        var page = new NewPopularService(_ts.Store, _catalog, Engine()).Build(Me, tmdbConfigured: false, DateTime.UtcNow);

        Assert.DoesNotContain(page.Top10Movies, c => c.Id == _movies[0].Id.ToString("N"));
        Assert.Equal(Enumerable.Range(1, page.Top10Movies.Count).Cast<int?>().ToArray(), page.Top10Movies.Select(c => c.Rank).ToArray());
        Assert.NotEmpty(page.EveryonesWatching);
        Assert.Empty(page.Top10Shows);   // no shows in this library
    }

    [Fact]
    public void When_the_engine_fails_the_fallback_answers()
    {
        var fallback = new FixedProvider();
        fallback.Ids.Add(_movies[2].Id);
        var broken = new HomeService(_ts.Store, new ThrowingCatalog(), NullLogger<HomeService>.Instance, _config);
        var provider = Engine(broken, fallback);

        Assert.Equal(new[] { _movies[2].Id }, provider.Trending(Me, 20));
        Assert.Equal(new[] { _movies[2].Id }, provider.TopTen(Me, CatalogKind.Movie));
    }

    [Fact]
    public void When_the_household_is_too_small_for_an_engine_chart_the_fallback_answers()
    {
        // No shows in the library: the engine has no top10-shows row, so the signal provider decides (an empty list here).
        var fallback = new FixedProvider();
        fallback.Ids.Add(Guid.NewGuid());

        Assert.Equal(fallback.Ids, Engine(fallback: fallback).TopTen(Me, CatalogKind.Series));
    }
}
