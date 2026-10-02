using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.FullUI.Api;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Library;
using Jellyfin.Plugin.FullUI.Recs;
using Jellyfin.Plugin.FullUI.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Jellyfin.Plugin.FullUI.Tests.Kit;

namespace Jellyfin.Plugin.FullUI.Tests;

/// <summary>Data quality (audit item 6) and the explanations on cards (item 7).</summary>
public class DataQualityAndExplainTests
{
    [Fact]
    public void DuplicateEditions_CollapseToOneCard_AndAggregateTheirSignals()
    {
        var hd = Movie(1, "Heat (1080p)", new[] { "Crime" }, tmdb: 949);
        var uhd = Movie(2, "Heat (4K)", new[] { "Crime" }, tmdb: 949, backdrop: true);
        var catalog = Many(10, 30, "Pad", "Crime").Concat(new[] { hd, uhd }).ToList();
        // Three people watched Heat, split over the two editions: one chart entry, three viewers.
        var signals = new[] { Play(U1, hd, 1), Play(U2, uhd, 1), Play(U3, uhd, 1) };

        var rows = Build(Input(Viewer, catalog, signals));

        var all = AllIds(rows).ToList();
        Assert.True(all.Count(id => id == hd.Id || id == uhd.Id) == rows.Count(r => r.Items.Any(i => i.Item.Id == hd.Id || i.Item.Id == uhd.Id)), "at most one edition per row");
        foreach (var r in rows)
        {
            Assert.True(r.Items.Count(i => i.Item.TmdbId == 949) <= 1, $"{r.Id} lists two editions of the same title");
        }

        Assert.Equal(uhd.Id, rows.SelectMany(r => r.Items).First(i => i.Item.TmdbId == 949).Item.Id);   // the edition with the better metadata / more plays
    }

    [Fact]
    public void ViewerWhoWatchedOneEdition_IsNotOfferedTheOther()
    {
        var hd = Movie(1, "Heat (1080p)", new[] { "Crime" }, tmdb: 949);
        var uhd = Movie(2, "Heat (4K)", new[] { "Crime" }, tmdb: 949);
        var catalog = Many(10, 30, "Pad", "Crime").Concat(new[] { hd, uhd }).ToList();

        var rows = Build(Input(Viewer, catalog, new[] { Play(Viewer, hd, 3) }));

        Assert.DoesNotContain(AllIds(rows), id => id == hd.Id || id == uhd.Id);
    }

    [Fact]
    public void WhenOnlyOneEditionIsVisible_ThatOneIsShown()
    {
        var hd = Movie(1, "Heat (1080p)", new[] { "Crime" }, tmdb: 949, backdrop: true);
        var uhd = Movie(2, "Heat (4K)", new[] { "Crime" }, tmdb: 949);
        var catalog = Many(10, 30, "Pad", "Crime").Concat(new[] { hd, uhd }).ToList();
        var visible = catalog.Where(c => c.Id != hd.Id).Select(c => c.Id).ToHashSet();

        var rows = Build(Input(Viewer, catalog, visible: visible, signals: new[] { Play(U1, hd, 1), Play(U2, hd, 1), Play(U3, hd, 1) }));

        var shown = AllIds(rows).Where(id => id == hd.Id || id == uhd.Id).Distinct().ToList();
        Assert.Equal(new[] { uhd.Id }, shown);
    }

    [Fact]
    public void ARatingOnOneEdition_AppliesToTheTitle()
    {
        var hd = Movie(1, "Heat (1080p)", new[] { "Crime" }, tmdb: 949);
        var uhd = Movie(2, "Heat (4K)", new[] { "Crime" }, tmdb: 949);
        var catalog = Many(10, 30, "Pad", "Crime").Concat(new[] { hd, uhd }).ToList();

        var rows = Build(Input(Viewer, catalog, ratings: new() { [Key(Viewer, hd)] = -1 }));

        Assert.DoesNotContain(AllIds(rows), id => id == hd.Id || id == uhd.Id);
    }

    [Fact]
    public void NextInCollection_ListsTheUnwatchedMovies_OldestFirst()
    {
        var col = Guid.NewGuid();
        var m1 = Movie(1, "Saga One", new[] { "Action" }, year: 2001, collection: col, collectionName: "The Saga");
        var m3 = Movie(3, "Saga Three", new[] { "Action" }, year: 2005, collection: col, collectionName: "The Saga");
        var m2 = Movie(2, "Saga Two", new[] { "Action" }, year: 2003, collection: col, collectionName: "The Saga");
        var untouched = Movie(4, "Other Saga A", new[] { "Action" }, year: 2000, collection: Guid.NewGuid(), collectionName: "Other");
        var untouched2 = Movie(5, "Other Saga B", new[] { "Action" }, year: 2002, collection: untouched.CollectionId, collectionName: "Other");
        var catalog = Many(10, 30, "Pad", "Action").Concat(new[] { m1, m2, m3, untouched, untouched2 }).ToList();

        var rows = Build(Input(Viewer, catalog, new[] { Play(Viewer, m1, 10) }));

        var row = rows.Single(r => r.Type == "collection");
        Assert.Equal("Next in The Saga", row.Title);
        Assert.Equal(new[] { m2.Id, m3.Id }, row.Items.Select(i => i.Item.Id).ToArray());
        Assert.Equal("Next in The Saga", row.Items[0].Reason);
        Assert.Equal(1, rows.Count(r => r.Type == "collection"));   // a collection nobody started gets no row
    }

    [Fact]
    public void TitlesWithoutGenresOrRatings_AreNotSilentlyDropped()
    {
        var bare = Movie(1, "Bare Title", Array.Empty<string>(), rating: null, daysOld: 3);
        var catalog = Many(10, 30, "Pad", "Drama").Append(bare).ToList();

        var rows = Build(Input(Viewer, catalog, new[] { Play(Viewer, catalog[0], 5) }));

        Assert.Contains(Row(rows, "recent")!.Items, i => i.Item.Id == bare.Id);
        var cold = Build(Input(Viewer, catalog));
        Assert.Contains(AllIds(cold), id => id == bare.Id);
    }

    [Fact]
    public void HiddenGems_NeedARatingAboveTheLibraryMedian_AndAnUnwatchedTitle_EvenWithTwoUsers()
    {
        // Old rule: rating >= 7.5 and at most one watcher: it listed 7.6-rated titles in a library full of 9s and 8s.
        var strong = Enumerable.Range(1, 30).Select(i => Movie(i, $"Strong{i:00}", new[] { "Drama" }, rating: 8.0f + (i % 10 / 10f))).ToList();
        var okay = Enumerable.Range(100, 10).Select(i => Movie(i, $"Okay{i}", new[] { "Drama" }, rating: 7.6f)).ToList();
        var catalog = strong.Concat(okay).ToList();
        var signals = new[] { Play(U2, strong[0], 40), Play(Viewer, strong[1], 40) };

        var rows = Build(Input(Viewer, catalog, signals));
        var gems = Row(rows, "hidden");

        Assert.NotNull(gems);
        var median = catalog.Select(c => c.Rating!.Value).OrderBy(r => r).ElementAt(catalog.Count / 2);
        Assert.All(gems!.Items, i => Assert.True(i.Item.Rating > median));
        Assert.DoesNotContain(gems.Items, i => i.Item.Id == strong[1].Id);   // already watched by this user
        Assert.DoesNotContain(gems.Items, i => okay.Any(o => o.Id == i.Item.Id));
    }

    // ---- match %, reason, labels ---------------------------------------------------------------------------------

    [Fact]
    public void MatchPercent_IsAPercentileOfTheRankerScore_AndNullOnColdStart()
    {
        var seed = Movie(1, "Seed", new[] { "Action", "Thriller" });
        var near = Many(10, 25, "Near", "Action", "Thriller");
        var far = Many(100, 25, "Far", "Romance");
        var catalog = near.Concat(far).Append(seed).ToList();

        var rows = Build(Input(Viewer, catalog, new[] { Play(Viewer, seed, 3) }));
        var picks = Row(rows, "toppicks")!;

        Assert.All(rows.SelectMany(r => r.Items), i => Assert.True(i.Match is null or (>= 1 and <= 99)));
        Assert.NotNull(picks.Items[0].Match);
        var matches = picks.Items.Select(i => i.Match!.Value).ToList();
        Assert.True(matches[0] >= 90, $"best match should be near the top, was {matches[0]}");
        var farItem = rows.SelectMany(r => r.Items).First(i => i.Item.Name.StartsWith("Far"));
        Assert.True(farItem.Match < matches[0]);

        var cold = Build(Input(Viewer, catalog));
        Assert.All(cold.SelectMany(r => r.Items), i => Assert.Null(i.Match));
    }

    [Fact]
    public void Reasons_ArePlainEnglish_PerRow()
    {
        var seed = Movie(1, "Heat", new[] { "Crime", "Thriller" });
        var show = Show(2, "Dark", new[] { "Drama" }, new[] { 8, 8 }, seasonAddedDaysAgo: new[] { 400, 5 }, recentEpisodeDaysAgo: new[] { 5 });
        var catalog = Many(10, 40, "Crime", "Crime", "Thriller").Concat(Many(60, 20, "Dra", "Drama")).Concat(new[] { seed, show }).ToList();
        var signals = new List<PlaySignal> { Play(Viewer, seed, 5) };
        signals.AddRange(WatchSeasons(Viewer, show, 30, 1));
        foreach (var u in new[] { U1, U2 })
        {
            signals.AddRange(catalog.Skip(40).Take(5).Select(c => Play(u, c, 1)));
        }

        var rows = Build(Input(Viewer, catalog, signals));

        Assert.Equal("Because you watched Heat", Row(rows, $"because-{seed.Id:N}")!.Items[0].Reason);
        Assert.Equal("New season of Dark", Row(rows, "newseasons")!.Items[0].Reason);
        Assert.Equal("Popular in your household", Row(rows, "trending")!.Items[0].Reason);
        Assert.All(rows.Where(r => r.Type == "genre").SelectMany(r => r.Items), i => Assert.Matches("^(Top rated in|Because you like) ", i.Reason));
        Assert.Contains(Row(rows, "toppicks")!.Items, i => i.Reason is not null);
    }

    [Fact]
    public void ContinueWatching_CarriesEpisodeLabelAndMinutesLeft()
    {
        var movie = Movie(1, "Long Film", new[] { "Drama" }, runtime: 100);
        var show = Show(2, "Show", new[] { "Drama" }, new[] { 10, 10 }, runtime: 50);
        var next = Show(3, "Next Up Show", new[] { "Drama" }, new[] { 10 }, runtime: 40);
        var catalog = Many(10, 20, "Pad", "Drama").Concat(new[] { movie, show, next }).ToList();
        var signals = new List<PlaySignal>
        {
            Play(Viewer, movie, 1, 0.25, false),
            Ep(Viewer, show, 2, 5, 2, 0.4),
            Ep(Viewer, next, 1, 3, 1),
        };

        var rows = Build(Input(Viewer, catalog, signals, nextUp: new[] { next.Id },
            nextUpEpisodes: new() { [next.Id] = new NextUpEpisode(1, 4) }));
        var cont = Row(rows, "continue")!.Items;

        var m = cont.Single(i => i.Item.Id == movie.Id);
        Assert.Null(m.SeriesLabel);
        Assert.Equal(75, m.MinutesLeft);
        var s = cont.Single(i => i.Item.Id == show.Id);
        Assert.Equal("S2:E5", s.SeriesLabel);
        Assert.Equal(30, s.MinutesLeft);
        var n = cont.Single(i => i.Item.Id == next.Id);
        Assert.Equal("S1:E4", n.SeriesLabel);
        Assert.Null(n.MinutesLeft);
    }

    [Fact]
    public void CardMapper_CopiesTheExplanations_AndKeepsThemInRange()
    {
        var item = Movie(1, "A", new[] { "Drama" }, runtime: 90);
        var card = CardMapper.ToCard(item, Array.Empty<string>(), null, 0.5, 0, false, new StoreData(), 140, "Because you watched B", "S1:E2", 45);
        Assert.Equal(99, card.MatchPercent);
        Assert.Equal("Because you watched B", card.Reason);
        Assert.Equal("S1:E2", card.SeriesLabel);
        Assert.Equal(45, card.MinutesLeft);

        var plain = CardMapper.ToCard(item, Array.Empty<string>(), null, null, 0, false, new StoreData());
        Assert.Null(plain.MatchPercent);
        Assert.Null(plain.Reason);
        Assert.Null(CardMapper.ToCard(item, Array.Empty<string>(), null, null, 0, false, new StoreData(), 0, " ", "", 0).Reason);
    }

    [Fact]
    public void HomeService_ReturnsCardsWithExplanations_AndItemLookupKeepsThem()
    {
        using var ts = new TempStore();
        var seed = Movie(1, "Seed", new[] { "Action" }, runtime: 100);
        var catalog = new SimCatalog { Items = Many(10, 30, "Act", "Action").Append(seed).ToList() };
        var me = Guid.NewGuid();
        ts.Store.Write(d => d.Signals.Add(new PlaySignal { UserId = me, ItemId = seed.Id, At = DateTime.UtcNow.AddDays(-3), Completion = 1, Completed = true }));
        var svc = new HomeService(ts.Store, catalog, NullLogger<HomeService>.Instance, new SimConfig());

        var home = svc.GetHomeStrict(me);
        var card = home.Rows.First(r => r.Id.StartsWith("because-", StringComparison.Ordinal)).Items[0];

        Assert.Equal("Because you watched Seed", home.Rows.First(r => r.Id.StartsWith("because-", StringComparison.Ordinal)).Title);
        Assert.InRange(card.MatchPercent!.Value, 1, 99);
        Assert.Equal("Because you watched Seed", card.Reason);
        var again = svc.GetItemStrict(me, Guid.Parse(card.Id));
        Assert.Equal(card.MatchPercent, again!.MatchPercent);
        Assert.Equal(card.Reason, again.Reason);
    }
}
