using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Library;
using Jellyfin.Plugin.FullUI.Recs;
using Jellyfin.Plugin.FullUI.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using static Jellyfin.Plugin.FullUI.Tests.Kit;

namespace Jellyfin.Plugin.FullUI.Tests;

/// <summary>Benchmark-style check (audit item 8): a big library and a busy household still build a Home in sane time.</summary>
public class PerformanceTests
{
    private readonly ITestOutputHelper _out;

    public PerformanceTests(ITestOutputHelper output) => _out = output;

    private static readonly string[] Genres =
    {
        "Action", "Adventure", "Animation", "Comedy", "Crime", "Documentary", "Drama", "Family", "Fantasy", "History", "Horror", "Music",
        "Mystery", "Romance", "Science Fiction", "Thriller", "War", "Western", "Sport", "Biography",
    };

    public static (List<CatalogItem> Catalog, List<PlaySignal> Signals, Guid[] Users) BigLibrary(int titles, int users, int signalsPerUser, int seed = 7)
    {
        var rnd = new Random(seed);
        var catalog = new List<CatalogItem>(titles);
        for (var i = 1; i <= titles; i++)
        {
            var genres = Enumerable.Range(0, 1 + rnd.Next(3)).Select(_ => Genres[rnd.Next(Genres.Length)]).Distinct().ToArray();
            var cast = Enumerable.Range(0, 4).Select(_ => "Actor " + rnd.Next(3000)).ToArray();
            var item = i % 4 == 0
                ? Show(i, $"Show {i:00000}", genres, new[] { 8 + rnd.Next(5), 8 + rnd.Next(5) }, new[] { 300 + rnd.Next(500), rnd.Next(60) }, rating: 4 + (float)(rnd.NextDouble() * 5.5), daysOld: 100 + rnd.Next(2000), tmdb: i % 50 == 0 ? 90000 + i : null)
                : Movie(i, $"Movie {i:00000}", genres, rating: 4 + (float)(rnd.NextDouble() * 5.5), daysOld: rnd.Next(3000), year: 1960 + rnd.Next(65), tmdb: i % 50 == 1 ? 80000 + (i % 100) : null,
                    cast: cast, directors: new[] { "Director " + rnd.Next(800) }, collection: i % 40 == 1 ? Id(1_000_000 + (i / 40)) : null, collectionName: "Collection");
            catalog.Add(item);
        }

        var userIds = Enumerable.Range(0, users).Select(u => Id(9000 + u)).ToArray();
        var signals = new List<PlaySignal>();
        foreach (var u in userIds)
        {
            var favourite = Genres[rnd.Next(Genres.Length)];
            for (var s = 0; s < signalsPerUser; s++)
            {
                var item = catalog[rnd.Next(catalog.Count)];
                if (rnd.NextDouble() < 0.5)
                {
                    item = catalog.Where(c => c.Genres.Contains(favourite)).ElementAtOrDefault(rnd.Next(2000)) ?? item;
                }

                var days = rnd.Next(1, 400);
                signals.Add(item.Kind == CatalogKind.Series
                    ? Ep(u, item, 1, 1 + rnd.Next(8), days)
                    : Play(u, item, days, rnd.NextDouble() < 0.8 ? 1 : 0.4));
            }
        }

        return (catalog, signals, userIds);
    }

    [Fact]
    public void TwentyThousandTitles_TenUsers_BuildsAHomeInSaneTime()
    {
        var (catalog, signals, users) = BigLibrary(20_000, 10, 300);
        var cache = new UserWeightsCache();
        RecInput For(Guid u) => Input(u, catalog, signals, cache: cache, fingerprint: "bench");

        var sw = Stopwatch.StartNew();
        var first = RecEngine.Build(For(users[0]));
        var coldMs = sw.ElapsedMilliseconds;     // includes the one-off catalog index (IDF, vectors, duplicate groups)
        sw.Restart();
        var second = RecEngine.Build(For(users[1]));
        var warmMs = sw.ElapsedMilliseconds;
        sw.Restart();
        var again = RecEngine.Build(For(users[0]));
        var repeatMs = sw.ElapsedMilliseconds;

        _out.WriteLine($"20,000 titles / 10 users / {signals.Count} signals: first Home {coldMs} ms (incl. catalog index), next user {warmMs} ms, repeat {repeatMs} ms; rows: {first.Count}/{second.Count}");

        Assert.NotEmpty(first);
        Assert.Equal(first.Select(r => r.Id), again.Select(r => r.Id));
        Assert.True(coldMs < 20_000, $"first build took {coldMs} ms");
        Assert.True(warmMs < 8_000, $"warm build took {warmMs} ms");
        Assert.True(repeatMs < 8_000, $"repeat build took {repeatMs} ms");
    }

    [Fact]
    public void HomeService_ItemLookupIsADictionaryLookup_NotALinearScan()
    {
        using var ts = new TempStore();
        var (catalog, _, _) = BigLibrary(20_000, 1, 1);
        var sim = new SimCatalog { Items = catalog };
        var svc = new HomeService(ts.Store, sim, NullLogger<HomeService>.Instance, new SimConfig());
        var user = Guid.NewGuid();
        var warm = svc.GetItemStrict(user, catalog[0].Id);   // builds the index and the user's home once
        Assert.NotNull(warm);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 2000; i++)
        {
            Assert.NotNull(svc.GetItemStrict(user, catalog[19_999 - (i % 100)].Id));
        }

        _out.WriteLine($"2000 item lookups in a 20,000 title library: {sw.ElapsedMilliseconds} ms");
        Assert.True(sw.ElapsedMilliseconds < 5_000);
    }
}
