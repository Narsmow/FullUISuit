using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.FullUI.Api;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Library;
using Jellyfin.Plugin.FullUI.Recs;
using Jellyfin.Plugin.FullUI.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.FullUI.Tests;

/// <summary>
/// Synthetic households run through the real HomeService (store, kid detection, caches, mapping) with golden assertions per row.
/// Every household's rows are printed to the test output so a human can review them (run with --logger "console;verbosity=detailed").
/// </summary>
public class HouseholdSimulatorTests : IDisposable
{
    private static readonly DateTime Today = DateTime.UtcNow;
    private readonly ITestOutputHelper _out;
    private readonly TempStore _ts = new();
    private int _n = 1;

    public HouseholdSimulatorTests(ITestOutputHelper output) => _out = output;

    public void Dispose() => _ts.Dispose();

    // ---- builders ----------------------------------------------------------------------------------------------------

    private CatalogItem Mv(string name, string[] genres, float rating = 7.0f, int daysOld = 500, int year = 2008, string rated = "PG-13",
        int? tmdb = null, string[]? cast = null, Guid? col = null, string? colName = null, bool art = true) => new()
        {
            Id = Kit.Id(_n++),
            Kind = CatalogKind.Movie,
            Name = name,
            Year = year,
            Rating = rating,
            Genres = genres,
            OfficialRating = rated,
            DateAdded = Today.AddDays(-daysOld),
            TmdbId = tmdb,
            Cast = cast ?? Array.Empty<string>(),
            CollectionId = col,
            CollectionName = colName,
            RuntimeMinutes = 100,
            HasBackdrop = art,
        };

    private CatalogItem Sh(string name, string[] genres, int[] episodes, int[] seasonAdded, int[]? recent = null, float rating = 7.5f, string rated = "TV-14")
    {
        var seasons = episodes.Select((e, i) => new SeasonInfo(i + 1, e, Today.AddDays(-seasonAdded[i]), Today.AddDays(-seasonAdded[i]))).ToList();
        var recentDates = (recent ?? Array.Empty<int>()).Select(d => Today.AddDays(-d)).OrderByDescending(d => d).ToList();
        return new CatalogItem
        {
            Id = Kit.Id(_n++),
            Kind = CatalogKind.Series,
            Name = name,
            Year = 2012,
            Rating = rating,
            Genres = genres,
            OfficialRating = rated,
            DateAdded = Today.AddDays(-seasonAdded.Max()),
            Seasons = seasons,
            RecentEpisodeDates = recentDates,
            LatestEpisodeAdded = recentDates.Count > 0 ? recentDates[0] : seasons.Max(s => s.LastAdded),
            RuntimeMinutes = 45,
            HasBackdrop = true,
        };
    }

    private static PlaySignal Done(Guid u, CatalogItem m, double daysAgo) =>
        new() { UserId = u, ItemId = m.Id, At = Today.AddDays(-daysAgo), Completion = 1, Completed = true };

    private static PlaySignal Part(Guid u, CatalogItem m, double daysAgo, double completion) =>
        new() { UserId = u, ItemId = m.Id, At = Today.AddDays(-daysAgo), Completion = completion, Completed = false };

    private static PlaySignal Ep(Guid u, CatalogItem show, int season, int episode, double daysAgo, double completion = 1) =>
        new() { UserId = u, ItemId = show.Id, IsEpisode = true, Season = season, Episode = episode, At = Today.AddDays(-daysAgo), Completion = completion, Completed = completion >= 0.9 };

    private static IEnumerable<PlaySignal> Season(Guid u, CatalogItem show, int season, double daysAgo)
    {
        var count = show.Seasons.First(s => s.Number == season).Episodes;
        return Enumerable.Range(1, count).Select(e => Ep(u, show, season, e, daysAgo));
    }

    private sealed class SimWatch : IWatchStateSource
    {
        public Dictionary<Guid, Dictionary<Guid, Dictionary<int, int>>> ByUser { get; } = new();

        public IReadOnlyDictionary<Guid, SeriesWatchInfo> SeriesWatch(Guid userId) =>
            ByUser.TryGetValue(userId, out var d) ? d.ToDictionary(kv => kv.Key, kv => new SeriesWatchInfo(kv.Value)) : new Dictionary<Guid, SeriesWatchInfo>();

        public void Invalidate(Guid userId)
        {
        }
    }

    private sealed record Household(
        HomeService Home,
        SimCatalog Catalog,
        Dictionary<string, Guid> Users,
        Dictionary<string, CatalogItem> Titles,
        SimNextUp NextUp);

    private Household Family()
    {
        var titles = new Dictionary<string, CatalogItem>();
        CatalogItem Add(CatalogItem c)
        {
            titles[c.Name] = c;
            return c;
        }

        // ---- library: ~130 titles across tastes, ratings and audiences ---------------------------------------------------
        for (var i = 1; i <= 14; i++)
        {
            Add(Mv($"Action {i:00}", new[] { "Action", "Thriller" }, 6.0f + (i % 5), rated: i % 3 == 0 ? "R" : "PG-13", year: 1995 + i, cast: new[] { "Rex Steele", "Ann Hero" }));
        }

        for (var i = 1; i <= 12; i++)
        {
            Add(Mv($"Space {i:00}", new[] { "Science Fiction", "Adventure" }, 6.5f + (i % 4) * 0.7f, rated: "PG-13", year: 2000 + i, cast: new[] { "Nova Reyes" }));
        }

        for (var i = 1; i <= 12; i++)
        {
            Add(Mv($"Drama {i:00}", new[] { "Drama" }, 6.5f + (i % 4), rated: i % 4 == 0 ? "R" : "PG-13", year: 2001 + i));
        }

        for (var i = 1; i <= 10; i++)
        {
            Add(Mv($"Romance {i:00}", new[] { "Romance", "Comedy" }, 6.0f + (i % 3), rated: "PG-13", year: 2003 + i));
        }

        for (var i = 1; i <= 10; i++)
        {
            Add(Mv($"Laughs {i:00}", new[] { "Comedy" }, 6.0f + (i % 3), rated: "PG-13", year: 2005 + i));
        }

        for (var i = 1; i <= 14; i++)
        {
            Add(Mv($"Cartoon {i:00}", new[] { "Animation", "Family" }, 7.0f + (i % 3) * 0.5f, rated: i % 2 == 0 ? "G" : "PG", year: 2010 + (i % 8), daysOld: i < 3 ? 6 : 400));
        }

        for (var i = 1; i <= 8; i++)
        {
            Add(Mv($"Gore {i:00}", new[] { "Horror" }, 5.5f + (i % 3), rated: "R", year: 2010 + i));
        }

        for (var i = 1; i <= 8; i++)
        {
            Add(Mv($"Doc {i:00}", new[] { "Documentary" }, 7.4f + (i % 3) * 0.4f, rated: "TV-PG", year: 2015 + (i % 6)));
        }

        for (var i = 1; i <= 4; i++)
        {
            Add(Mv($"Hidden Jewel {i}", new[] { "Drama", "Mystery" }, 9.0f, rated: "PG-13", year: 1990 + i));
        }

        var saga = Guid.NewGuid();
        Add(Mv("Star Saga I", new[] { "Science Fiction", "Adventure" }, 8.0f, year: 1999, col: saga, colName: "Star Saga"));
        Add(Mv("Star Saga II", new[] { "Science Fiction", "Adventure" }, 7.8f, year: 2002, col: saga, colName: "Star Saga"));
        Add(Mv("Star Saga III", new[] { "Science Fiction", "Adventure" }, 7.1f, year: 2005, col: saga, colName: "Star Saga"));
        Add(Mv("Dune (1080p)", new[] { "Science Fiction", "Adventure" }, 8.0f, year: 2021, tmdb: 438631, art: false));
        Add(Mv("Dune (4K)", new[] { "Science Fiction", "Adventure" }, 8.0f, year: 2021, tmdb: 438631, art: true));

        Add(Sh("The Finished Saga", new[] { "Drama", "Mystery" }, new[] { 6, 6 }, new[] { 800, 700 }, rating: 8.6f));
        Add(Sh("Endless Soap", new[] { "Drama", "Romance" }, new[] { 20, 20, 20, 20, 20 }, new[] { 2000, 1500, 1000, 500, 300 }, rating: 6.9f));
        Add(Sh("Dropped Show", new[] { "Action", "Thriller" }, new[] { 10 }, new[] { 600 }, rating: 7.0f, rated: "TV-MA"));
        Add(Sh("Binge Show", new[] { "Science Fiction", "Thriller" }, new[] { 8, 8, 8 }, new[] { 700, 400, 100 }, rating: 8.3f));
        Add(Sh("Weekly Hit", new[] { "Drama", "Comedy" }, new[] { 10, 8 }, new[] { 900, 60 }, new[] { 3, 10, 17 }, rating: 8.1f));
        Add(Sh("Comeback Show", new[] { "Science Fiction", "Drama" }, new[] { 8, 8, 8 }, new[] { 900, 600, 8 }, new[] { 8, 9, 10 }, rating: 8.4f));
        Add(Sh("Kid Cartoon Show", new[] { "Animation", "Family" }, new[] { 10, 10, 10 }, new[] { 900, 600, 300 }, rating: 7.8f, rated: "TV-Y7"));
        Add(Sh("Nature Hour", new[] { "Documentary" }, new[] { 6, 6 }, new[] { 500, 200 }, rating: 8.8f, rated: "TV-G"));

        var catalog = new SimCatalog { Items = titles.Values.ToList() };
        var users = new[] { "Dad", "Mom", "Teen", "Kid", "Grandpa" }.ToDictionary(n => n, _ => Guid.NewGuid());

        // Kid: sees only family-friendly titles (what a Jellyfin parental cap does).
        catalog.VisibleByUser[users["Kid"]] = catalog.Items.Where(c => c.OfficialRating is "G" or "PG" or "TV-Y7" or "TV-G").Select(c => c.Id).ToHashSet();

        CatalogItem T(string n) => titles[n];

        var dad = users["Dad"];
        var mom = users["Mom"];
        var teen = users["Teen"];
        var kid = users["Kid"];
        var grandpa = users["Grandpa"];
        var signals = new List<PlaySignal>();

        // Dad: action + sci-fi, finished a whole series long ago, started and dropped two shows, started a collection.
        for (var i = 1; i <= 7; i++)
        {
            signals.Add(Done(dad, T($"Action {i:00}"), 10 + (i * 6)));
        }

        signals.AddRange(new[] { "Space 01", "Space 02", "Space 03" }.Select((n, i) => Done(dad, T(n), 20 + (i * 9))));
        signals.Add(Done(dad, T("Star Saga I"), 15));
        signals.Add(Done(dad, T("Dune (1080p)"), 30));
        signals.AddRange(Season(dad, T("The Finished Saga"), 1, 70));
        signals.AddRange(Season(dad, T("The Finished Saga"), 2, 60));
        signals.Add(Ep(dad, T("Endless Soap"), 1, 1, 120));            // one episode of a 100-episode soap
        signals.AddRange(new[] { Ep(dad, T("Dropped Show"), 1, 1, 100), Ep(dad, T("Dropped Show"), 1, 2, 99) });
        signals.AddRange(Season(dad, T("Comeback Show"), 1, 200));
        signals.AddRange(Season(dad, T("Comeback Show"), 2, 190));    // finished S1+S2; S3 arrived 8 days ago
        signals.Add(Part(dad, T("Space 05"), 2, 0.3));

        // Mom: drama/romance/comedy, caught up on a weekly show except the newest episode, mid-way through the soap.
        for (var i = 1; i <= 6; i++)
        {
            signals.Add(Done(mom, T($"Drama {i:00}"), 8 + (i * 7)));
        }

        signals.AddRange(new[] { "Romance 01", "Romance 02", "Romance 03", "Laughs 01" }.Select((n, i) => Done(mom, T(n), 14 + (i * 10))));
        signals.AddRange(Season(mom, T("Weekly Hit"), 1, 80));
        signals.AddRange(Enumerable.Range(1, 7).Select(e => Ep(mom, T("Weekly Hit"), 2, e, 20 - e)));   // S2 has 8 episodes; the last arrived 3 days ago
        signals.AddRange(Enumerable.Range(1, 12).Select(e => Ep(mom, T("Endless Soap"), 1, e, 12 - (e / 2.0))));
        signals.Add(Ep(mom, T("Endless Soap"), 1, 13, 1, 0.45));
        signals.Add(Done(mom, T("Gore 01"), 30));                      // watched it, hated it: rated down below

        // Teen: binge-watches sci-fi/thriller shows, little else.
        signals.AddRange(Season(teen, T("Binge Show"), 1, 9));
        signals.AddRange(Season(teen, T("Binge Show"), 2, 6));
        signals.AddRange(Enumerable.Range(1, 5).Select(e => Ep(teen, T("Binge Show"), 3, e, 2 - (e * 0.1))));
        signals.Add(Ep(teen, T("Binge Show"), 3, 6, 0.1, 0.35));
        signals.AddRange(new[] { "Space 02", "Space 04", "Action 02", "Gore 02", "Gore 03" }.Select((n, i) => Done(teen, T(n), 3 + i)));

        // Kid: cartoons, rewatches one a lot; the most-watched titles on the server this week are the kid's.
        foreach (var n in new[] { "Cartoon 01", "Cartoon 02", "Cartoon 03", "Cartoon 04", "Cartoon 05" })
        {
            signals.Add(Done(kid, T(n), 2));
            signals.Add(Done(kid, T(n), 1));
        }

        signals.AddRange(Season(kid, T("Kid Cartoon Show"), 1, 3));
        signals.AddRange(Enumerable.Range(1, 4).Select(e => Ep(kid, T("Kid Cartoon Show"), 2, e, 1)));

        // Grandpa: documentaries and quiet dramas.
        signals.AddRange(Enumerable.Range(1, 5).Select(i => Done(grandpa, T($"Doc {i:00}"), 5 + (i * 4))));
        signals.AddRange(new[] { "Drama 01", "Drama 02", "Hidden Jewel 1" }.Select((n, i) => Done(grandpa, T(n), 12 + (i * 8))));
        signals.AddRange(Season(grandpa, T("Nature Hour"), 1, 25));
        signals.AddRange(Season(grandpa, T("Nature Hour"), 2, 20));

        _ts.Store.Write(d =>
        {
            d.Signals.AddRange(signals);
            d.Ratings[StoreData.UserItemKey(mom, T("Gore 01").Id)] = -1;
            d.Ratings[StoreData.UserItemKey(dad, T("Action 01").Id)] = 2;
            d.Ratings[StoreData.UserItemKey(dad, T("Drama 03").Id)] = -1;
            d.MyList.Add(StoreData.UserItemKey(dad, T("Star Saga II").Id));
            d.MyList.Add(StoreData.UserItemKey(mom, T("Romance 05").Id));
            d.MyList.Add(StoreData.UserItemKey(mom, T("Gore 04").Id));          // in her list but rated down below
            d.Ratings[StoreData.UserItemKey(mom, T("Gore 04").Id)] = -1;
        });

        var watch = new SimWatch();
        void Watched(Guid u, CatalogItem show, IEnumerable<PlaySignal> sigs)
        {
            var seasons = sigs.Where(s => s.UserId == u && s.ItemId == show.Id && s.Completed).GroupBy(s => s.Season!.Value)
                .ToDictionary(g => g.Key, g => g.Select(s => s.Episode).Distinct().Count());
            if (!watch.ByUser.TryGetValue(u, out var m))
            {
                watch.ByUser[u] = m = new Dictionary<Guid, Dictionary<int, int>>();
            }

            m[show.Id] = seasons;
        }

        foreach (var pair in signals.Where(s => s.IsEpisode).Select(s => (s.UserId, s.ItemId)).Distinct())
        {
            Watched(pair.UserId, titles.Values.First(t => t.Id == pair.ItemId), signals);
        }

        var nextUp = new SimNextUp();
        nextUp.ByUser[dad] = new() { new NextUpEntry(T("Comeback Show").Id, 3, 1), new NextUpEntry(T("Dropped Show").Id, 1, 3), new NextUpEntry(T("Endless Soap").Id, 1, 2) };
        nextUp.ByUser[mom] = new() { new NextUpEntry(T("Weekly Hit").Id, 2, 8), new NextUpEntry(T("Endless Soap").Id, 1, 13) };
        nextUp.ByUser[teen] = new() { new NextUpEntry(T("Binge Show").Id, 3, 6) };
        nextUp.ByUser[kid] = new() { new NextUpEntry(T("Kid Cartoon Show").Id, 2, 5) };

        var home = new HomeService(_ts.Store, catalog, NullLogger<HomeService>.Instance, new SimConfig(), nextUp, watch);
        return new Household(home, catalog, users, titles, nextUp);
    }

    // ---- reporting -----------------------------------------------------------------------------------------------------

    private void Dump(string who, HomeResponse home)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== {who} ({home.Rows.Count} rows) ===");
        foreach (var row in home.Rows)
        {
            sb.AppendLine($"[{row.Type}] {row.Title}  ({row.Items.Count} items)");
            foreach (var c in row.Items.Take(row.Type is "continue" or "collection" or "newseasons" or "mylist" or "again" ? 20 : 6))
            {
                var extra = new List<string>();
                if (c.Rank is int r)
                {
                    extra.Add($"#{r}");
                }

                if (c.SeriesLabel is not null)
                {
                    extra.Add(c.SeriesLabel);
                }

                if (c.Progress is double p)
                {
                    extra.Add($"{p:P0}");
                }

                if (c.MinutesLeft is int m)
                {
                    extra.Add($"{m} min left");
                }

                if (c.MatchPercent is int mp)
                {
                    extra.Add($"match {mp}%");
                }

                sb.AppendLine($"     - {c.Name} {(extra.Count > 0 ? "(" + string.Join(", ", extra) + ")" : string.Empty)}{(c.Reason is null ? string.Empty : "  <- " + c.Reason)}");
            }
        }

        _out.WriteLine(sb.ToString());
    }

    private HomeResponse Home(Household h, string who)
    {
        var home = h.Home.GetHomeStrict(h.Users[who]);
        Dump(who, home);
        return home;
    }

    private static IEnumerable<string> Names(HomeResponse home) => home.Rows.SelectMany(r => r.Items).Select(i => i.Name);

    private static HomeRow? Row(HomeResponse home, string id) => home.Rows.FirstOrDefault(r => r.Id == id);

    private static IEnumerable<string> NamesIn(HomeResponse home, params string[] excludedRowIds) =>
        home.Rows.Where(r => !excludedRowIds.Contains(r.Id)).SelectMany(r => r.Items).Select(i => i.Name);

    // ---- the family ------------------------------------------------------------------------------------------------------

    [Fact]
    public void EveryMember_GetsAWellFormedHome()
    {
        var h = Family();
        foreach (var who in h.Users.Keys)
        {
            var home = Home(h, who);
            var u = h.Users[who];

            Assert.NotEmpty(home.Rows);
            Assert.Equal(home.Rows.Count, home.Rows.Select(r => r.Id).Distinct().Count());
            Assert.All(home.Rows.Where(r => r.Type != "comingsoon"), r => Assert.NotEmpty(r.Items));
            Assert.All(home.Rows, r => Assert.Equal(r.Items.Count, r.Items.Select(i => i.Id).Distinct().Count()));
            var visible = h.Catalog.VisibleTo(u);
            Assert.All(home.Rows.SelectMany(r => r.Items), i => Assert.Contains(Guid.Parse(i.Id), visible));
            Assert.All(home.Rows.SelectMany(r => r.Items), i =>
            {
                Assert.True(i.MatchPercent is null or (>= 1 and <= 99));
                Assert.True(i.MyRating >= 0, $"{who} thumbed {i.Name} down and it is still in a row");
            });
        }
    }

    [Fact]
    public void NoThumbsDownTitle_AppearsAnywhere_EvenInMyList()
    {
        var h = Family();
        var mom = Home(h, "Mom");
        var dad = Home(h, "Dad");

        Assert.DoesNotContain("Gore 01", Names(mom));
        Assert.DoesNotContain("Gore 04", Names(mom));            // rated down while sitting in her list
        Assert.DoesNotContain("Drama 03", Names(dad));
        Assert.DoesNotContain("Gore 04", Row(mom, "mylist")?.Items.Select(i => i.Name) ?? Array.Empty<string>());
    }

    [Fact]
    public void TheKid_SeesOnlyFamilyTitles_AndTheKidDoesNotShapeEveryoneElsesCharts()
    {
        var h = Family();
        var kid = Home(h, "Kid");
        var dad = Home(h, "Dad");
        var grandpa = Home(h, "Grandpa");

        Assert.All(kid.Rows.SelectMany(r => r.Items), i => Assert.Contains(i.Rated, new string?[] { "G", "PG", "TV-Y7", "TV-G" }));
        Assert.DoesNotContain(Names(kid), n => n.StartsWith("Gore") || n.StartsWith("Action"));

        // Cartoons 01-05 were watched twice a day by the kid only: they must not top the adults' Top 10 or Trending.
        var kidOnly = new[] { "Cartoon 01", "Cartoon 02", "Cartoon 03", "Cartoon 04", "Cartoon 05" };
        foreach (var adult in new[] { dad, grandpa })
        {
            Assert.DoesNotContain(Row(adult, "top10-movies")?.Items.Select(i => i.Name) ?? Array.Empty<string>(), n => kidOnly.Contains(n));
            Assert.DoesNotContain(Row(adult, "trending")?.Items.Select(i => i.Name) ?? Array.Empty<string>(), n => kidOnly.Contains(n));
        }

        // The kid's own page never offers what the kid already finished, in charts or anywhere else but Continue / Watch Again.
        Assert.DoesNotContain(NamesIn(kid, "continue", "again"), n => kidOnly.Contains(n));
    }

    [Fact]
    public void SeriesLogic_FinishedSagaDroppedShowAndLongRunner()
    {
        var h = Family();
        var dad = Home(h, "Dad");

        // A finished series: only in Watch Again (if at all), never offered again elsewhere.
        Assert.DoesNotContain("The Finished Saga", NamesIn(dad, "again"));
        Assert.Contains(Row(dad, "again")?.Items.Select(i => i.Name) ?? Array.Empty<string>(), n => n == "The Finished Saga");

        // One episode of a 100-episode soap is not a finished show: not in Watch Again, not a Because-you-watched row.
        Assert.DoesNotContain("Endless Soap", Row(dad, "again")?.Items.Select(i => i.Name) ?? Array.Empty<string>());
        Assert.DoesNotContain(dad.Rows, r => r.Id.StartsWith("because-", StringComparison.Ordinal) && r.Title.Contains("Endless Soap"));

        // The dropped shows are gone everywhere, including the Next Up entries Jellyfin keeps listing.
        Assert.DoesNotContain("Dropped Show", Names(dad));
        Assert.DoesNotContain("Endless Soap", Names(dad));

        // A new season of a show Dad finished the earlier seasons of.
        var fresh = Row(dad, "newseasons");
        Assert.NotNull(fresh);
        Assert.Equal("Comeback Show", fresh!.Items[0].Name);
        Assert.Equal("New season of Comeback Show", fresh.Items[0].Reason);
        Assert.Equal("S3:E1", Row(dad, "continue")!.Items.First(i => i.Name == "Comeback Show").SeriesLabel);
    }

    [Fact]
    public void MomIsCaughtUpExceptTheNewestEpisode_AndMidwayThroughTheSoap()
    {
        var h = Family();
        var mom = Home(h, "Mom");

        var weekly = Row(mom, "newseasons")?.Items.FirstOrDefault(i => i.Name == "Weekly Hit");
        Assert.NotNull(weekly);
        Assert.Equal("New episodes of Weekly Hit", weekly!.Reason);

        var cont = Row(mom, "continue")!.Items;
        var soap = cont.Single(i => i.Name == "Endless Soap");
        Assert.Equal("S1:E13", soap.SeriesLabel);
        Assert.InRange(soap.Progress!.Value, 0.44, 0.46);
        Assert.Equal(25, soap.MinutesLeft);   // 45 minute episodes, 45% watched
        Assert.Contains(cont, i => i.Name == "Weekly Hit" && i.SeriesLabel == "S2:E8");
    }

    [Fact]
    public void TheBingeWatcher_GetsEpisodeLabelsAndSeriesRows()
    {
        var h = Family();
        var teen = Home(h, "Teen");

        var cont = Row(teen, "continue")!.Items;
        var binge = cont.Single(i => i.Name == "Binge Show");
        Assert.Equal("S3:E6", binge.SeriesLabel);
        Assert.InRange(binge.Progress!.Value, 0.34, 0.36);
        Assert.True(teen.Rows.Count >= 8);
        Assert.NotNull(Row(teen, "toppicks"));
    }

    [Fact]
    public void DuplicateEditionsAndCollections()
    {
        var h = Family();
        var dad = Home(h, "Dad");
        var grandpa = Home(h, "Grandpa");

        // Dad watched Dune (1080p): neither edition is offered to him again. Others see at most one edition per row.
        Assert.DoesNotContain(NamesIn(dad, "again"), n => n.StartsWith("Dune"));
        Assert.Single(Row(dad, "again")!.Items, i => i.Name.StartsWith("Dune"));
        foreach (var row in grandpa.Rows)
        {
            Assert.True(row.Items.Count(i => i.Name.StartsWith("Dune")) <= 1, $"{row.Id} shows both Dune editions");
        }

        var next = Row(dad, "collection-" + h.Titles["Star Saga I"].CollectionId!.Value.ToString("N"));
        Assert.NotNull(next);
        Assert.Equal("Next in Star Saga", next!.Title);
        Assert.Equal(new[] { "Star Saga II", "Star Saga III" }, next.Items.Select(i => i.Name).ToArray());
    }

    [Fact]
    public void GrandpaGetsDocumentariesAndQuietDramas()
    {
        var h = Family();
        var home = Home(h, "Grandpa");
        var picks = Row(home, "toppicks")!.Items.Take(8).Select(i => i.Name).ToList();
        Assert.True(picks.Count(n => n.StartsWith("Doc") || n.StartsWith("Drama") || n.StartsWith("Hidden") || n == "Nature Hour") >= 3, string.Join(", ", picks));
        Assert.DoesNotContain("Nature Hour", NamesIn(home, "again"));    // he is caught up: finished, so not offered again
    }

    [Fact]
    public void TheSameHouseholdBuildsTheSameHomeTwice()
    {
        var h = Family();
        foreach (var who in h.Users.Keys)
        {
            var a = h.Home.GetHomeStrict(h.Users[who]);
            h.Home.Invalidate();
            var b = h.Home.GetHomeStrict(h.Users[who]);
            Assert.Equal(a.Rows.Select(r => r.Id + ":" + string.Join(",", r.Items.Select(i => i.Id))), b.Rows.Select(r => r.Id + ":" + string.Join(",", r.Items.Select(i => i.Id))));
        }
    }

    // ---- degenerate libraries ------------------------------------------------------------------------------------------

    [Fact]
    public void TinyLibrary_KeepsItsRows()
    {
        var items = new List<CatalogItem>
        {
            Mv("Tiny A", new[] { "Drama" }), Mv("Tiny B", new[] { "Drama" }), Mv("Tiny C", new[] { "Drama" }), Mv("Tiny D", new[] { "Comedy" }),
            Mv("Tiny E", new[] { "Comedy" }), Mv("Tiny F", new[] { "Drama" }),
        };
        var me = Guid.NewGuid();
        _ts.Store.Write(d => d.Signals.Add(Done(me, items[0], 5)));
        var svc = new HomeService(_ts.Store, new SimCatalog { Items = items }, NullLogger<HomeService>.Instance, new SimConfig());

        var home = svc.GetHomeStrict(me);
        Dump("Tiny library", home);

        var picks = Row(home, "toppicks");
        Assert.NotNull(picks);
        Assert.True(picks!.Items.Count >= 3);
        Assert.DoesNotContain("Tiny A", Names(home));    // watched
        Assert.All(home.Rows, r => Assert.NotEmpty(r.Items));

        var stranger = svc.GetHomeStrict(Guid.NewGuid());
        Dump("Tiny library, brand new user", stranger);
        Assert.NotNull(Row(stranger, "toppicks"));
        Assert.Equal("Popular on Home", Row(stranger, "toppicks")!.Title);
    }

    [Fact]
    public void EmptyLibrary_GivesAnEmptyHome_NotAnError()
    {
        var svc = new HomeService(_ts.Store, new SimCatalog(), NullLogger<HomeService>.Instance, new SimConfig());
        var home = svc.GetHomeStrict(Guid.NewGuid());
        Dump("Empty library", home);
        Assert.Empty(home.Rows);
    }

    [Fact]
    public void ABrandNewUser_InTheFamilyLibrary_GetsPopularityRowsAndNoPersonalisedOnes()
    {
        var h = Family();
        var newcomer = Guid.NewGuid();
        var home = h.Home.GetHomeStrict(newcomer);
        Dump("Newcomer (no history)", home);

        Assert.Equal("Popular on Home", Row(home, "toppicks")!.Title);
        Assert.DoesNotContain(home.Rows, r => r.Type is "continue" or "because" or "genre" or "mylist" or "again" or "collection");
        Assert.All(home.Rows.SelectMany(r => r.Items), i => Assert.Null(i.MatchPercent));
    }
}
