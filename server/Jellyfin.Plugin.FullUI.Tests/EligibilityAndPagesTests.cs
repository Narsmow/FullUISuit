using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Library;
using Jellyfin.Plugin.FullUI.Recs;
using Xunit;
using static Jellyfin.Plugin.FullUI.Tests.Kit;

namespace Jellyfin.Plugin.FullUI.Tests;

/// <summary>One eligibility rule for every row (audit item 2) and page generation (item 3).</summary>
public class EligibilityAndPagesTests
{
    /// <summary>A library where everything that could put a title in a row is true for title #1: popular, new, highly rated, in My List, partly watched.</summary>
    private static (List<CatalogItem> Catalog, CatalogItem Target, List<PlaySignal> Signals) Setup()
    {
        var target = Movie(1, "Target", new[] { "Action" }, rating: 9.1f, daysOld: 2);
        var catalog = Many(10, 60, "Pad", "Action");
        catalog.Add(target);
        var others = catalog.Take(6).ToList();
        var signals = new List<PlaySignal>();
        foreach (var u in new[] { U2, U3, U4 })
        {
            signals.Add(Play(u, target, 1));
            signals.AddRange(others.Select(o => Play(u, o, 1)));
        }

        return (catalog, target, signals);
    }

    private static void AssertNowhere(IReadOnlyList<RecRow> rows, CatalogItem item, string why) =>
        Assert.True(!AllIds(rows).Contains(item.Id), $"{item.Name} must not appear in any row ({why}); found in: {string.Join(",", rows.Where(r => r.Items.Any(i => i.Item.Id == item.Id)).Select(r => r.Id))}");

    [Fact]
    public void ThumbsDownTitle_AppearsInNoRow()
    {
        var (catalog, target, signals) = Setup();
        signals.Add(Play(Viewer, target, 1, 0.4, false));   // also resumable
        var rows = Build(Input(Viewer, catalog, signals,
            ratings: new() { [Key(Viewer, target)] = -1 }, myList: new() { Key(Viewer, target) }));

        Assert.NotEmpty(rows);
        AssertNowhere(rows, target, "thumbs down");
    }

    [Fact]
    public void ThumbsDown_StillShowsPopularTitlesToTheOthers()
    {
        // The control for the test above: the same data without the rating puts the title in the charts.
        var (catalog, target, signals) = Setup();
        var rows = Build(Input(Viewer, catalog, signals));
        Assert.Contains(Row(rows, "top10-movies")!.Items, i => i.Item.Id == target.Id);
        Assert.Contains(Row(rows, "trending")!.Items, i => i.Item.Id == target.Id);
        Assert.Contains(Row(rows, "recent")!.Items, i => i.Item.Id == target.Id);
    }

    [Fact]
    public void FinishedTitle_IsOnlyOfferedInWatchAgain()
    {
        var (catalog, target, signals) = Setup();
        var older = catalog.Skip(40).Take(2).ToList();   // two more finished titles so Watch Again has its three entries
        signals.AddRange(new[] { Play(Viewer, target, 60) }.Concat(older.Select(o => Play(Viewer, o, 70))));

        var rows = Build(Input(Viewer, catalog, signals, myList: new() { Key(Viewer, target) }));

        foreach (var r in rows.Where(r => r.Id != "again"))
        {
            Assert.DoesNotContain(r.Items, i => i.Item.Id == target.Id);
        }

        Assert.Contains(Row(rows, "again")!.Items, i => i.Item.Id == target.Id);
    }

    [Fact]
    public void HiddenTitle_LeavesContinueWatchingOnly()
    {
        // Streaming-app convention: "Remove from row" on Continue Watching removes it from that row, not from the user's list or the library.
        var (catalog, target, signals) = Setup();
        signals.Add(Play(Viewer, target, 1, 0.4, false));
        var rows = Build(Input(Viewer, catalog, signals, myList: new() { Key(Viewer, target) }, hidden: new[] { target.Id }));

        Assert.DoesNotContain(Row(rows, "continue")?.Items ?? Array.Empty<Jellyfin.Plugin.FullUI.Recs.RankedItem>(), i => i.Item.Id == target.Id);
        Assert.Contains(Row(rows, "mylist")!.Items, i => i.Item.Id == target.Id);
    }

    [Fact]
    public void NonVisibleTitle_AppearsInNoRow_EvenInMyListAndContinue()
    {
        var (catalog, target, signals) = Setup();
        signals.Add(Play(Viewer, target, 1, 0.4, false));
        var visible = catalog.Where(c => c.Id != target.Id).Select(c => c.Id).ToHashSet();
        var rows = Build(Input(Viewer, catalog, signals, myList: new() { Key(Viewer, target) }, visible: visible));
        AssertNowhere(rows, target, "not visible to this user");
    }

    [Fact]
    public void ResumableMovie_IsStillContinued()
    {
        var (catalog, target, signals) = Setup();
        var rows = Build(Input(Viewer, catalog, signals.Append(Play(Viewer, target, 1, 0.4, false))));
        Assert.Equal(target.Id, Row(rows, "continue")!.Items.Single().Item.Id);
    }

    [Fact]
    public void BecauseAndGenreRows_AreAllocatedBeforeTopPicksConsumesEverything()
    {
        // 24 look-alike candidates: the old Top Picks took 20 of them and nothing was left for Because-you-watched.
        var seeds = Many(1, 3, "Seed", "Action", "Thriller");
        var pool = Many(10, 24, "Pool", "Action", "Thriller");
        var catalog = seeds.Concat(pool).ToList();
        var rows = Build(Input(Viewer, catalog, seeds.Select(s => Play(Viewer, s, 5))));

        var because = rows.Where(r => r.Type == "because").ToList();
        var picks = Row(rows, "toppicks")!;
        Assert.NotEmpty(because);
        Assert.All(because, r => Assert.True(r.Items.Count >= RecEngine.MinRowFor(24)));
        Assert.True(picks.Items.Count >= 8, $"Top Picks keeps a solid share, had {picks.Items.Count}");
        Assert.Equal(24, picks.Items.Count + because.Sum(r => r.Items.Count) + rows.Where(r => r.Type == "genre").Sum(r => r.Items.Count));
    }

    [Fact]
    public void RecommendationRows_ShareNoTitle()
    {
        var catalog = new List<CatalogItem>();
        catalog.AddRange(Many(1, 3, "Seed", "Action", "Thriller"));
        catalog.AddRange(Many(10, 80, "Act", "Action", "Thriller"));
        catalog.AddRange(Many(100, 60, "Com", "Comedy"));
        catalog.AddRange(Enumerable.Range(200, 20).Select(i => Movie(i, $"Gem{i}", new[] { "Drama" }, rating: 8.9f)));
        var rows = Build(Input(Viewer, catalog, catalog.Take(3).Select(c => Play(Viewer, c, 5))));

        var ids = rows.Where(r => r.Type is "toppicks" or "because" or "genre" or "hidden" or "collection").SelectMany(r => r.Items.Select(i => i.Item.Id)).ToList();
        Assert.True(ids.Count > 40);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void ExplorationSlot_IsDeterministicWithinADay_AndRotatesAcrossDays()
    {
        var catalog = new List<CatalogItem>();
        catalog.AddRange(Many(1, 4, "Seed", "Action"));
        catalog.AddRange(Many(10, 60, "Act", "Action"));
        catalog.AddRange(Enumerable.Range(100, 80).Select(i => Movie(i, $"Other{i}", new[] { i % 2 == 0 ? "Comedy" : "Drama" }, rating: 7.5f)));
        var signals = catalog.Take(4).Select(c => Play(Viewer, c, 5)).ToList();

        string? Explore(int day)
        {
            var rows = Build(Input(Viewer, catalog, signals, now: Now.AddDays(day)));
            return Row(rows, "toppicks")!.Items.SingleOrDefault(i => i.Reason == "Something different today")?.Item.Name;
        }

        Assert.NotNull(Explore(0));
        Assert.Equal(Explore(0), Explore(0));
        Assert.True(Enumerable.Range(0, 8).Select(Explore).Distinct().Count() >= 3, "the exploration pick should change from day to day");
        var item = Row(Build(Input(Viewer, catalog, signals)), "toppicks")!.Items.Single(i => i.Reason == "Something different today").Item;
        Assert.DoesNotContain("Action", item.Genres);   // something outside the user's usual taste
    }

    [Fact]
    public void SmallLibrary_KeepsItsRows_AndChartsKeepTheirOwnMinimum()
    {
        var catalog = Many(1, 9, "M", "Action");
        var rows = Build(Input(Viewer, catalog, new[] { Play(Viewer, catalog[0], 3) }));
        Assert.NotNull(Row(rows, "toppicks"));
        Assert.NotNull(Row(rows, "recent"));

        Assert.Equal(3, RecEngine.MinRowFor(10));
        Assert.Equal(4, RecEngine.MinRowFor(30));
        Assert.Equal(5, RecEngine.MinRowFor(500));

        var chart = Many(20, 3, "C", "Action");
        var others = new[] { U1, U2 };
        var chartRows = Build(Input(Viewer, chart, others.SelectMany(u => chart.Select(c => Play(u, c, 1)))));
        Assert.Equal(3, Row(chartRows, "top10-movies")!.Items.Count);   // three entries are enough for a chart
    }

    [Fact]
    public void RowOrder_FollowsTheContract()
    {
        var catalog = new List<CatalogItem>();
        catalog.AddRange(Enumerable.Range(1, 120).Select(i => Movie(i, $"Act{i:000}", new[] { "Action", "Thriller" }, rating: i > 100 ? 8.2f : 6.5f, daysOld: i < 8 ? 3 : 300)));
        catalog.AddRange(Enumerable.Range(300, 30).Select(i => Movie(i, $"Com{i}", new[] { "Comedy" })));
        var col = Guid.NewGuid();
        catalog.Add(Movie(500, "Saga 1", new[] { "Action" }, year: 2001, collection: col, collectionName: "Saga"));
        catalog.Add(Movie(501, "Saga 2", new[] { "Action" }, year: 2003, collection: col, collectionName: "Saga"));
        var signals = new List<PlaySignal> { Play(Viewer, catalog[0], 40), Play(Viewer, catalog[1], 35), Play(Viewer, catalog[2], 33), Play(Viewer, catalog.First(c => c.Id == Id(500)), 20) };
        foreach (var u in new[] { U2, U3 })
        {
            signals.AddRange(catalog.Where(c => c.Name.StartsWith("Com")).Take(4).Select(c => Play(u, c, 1)));
        }

        var rows = Build(Input(Viewer, catalog, signals));
        var order = rows.Select(r => r.Order).ToList();
        Assert.Equal(order.OrderBy(x => x).ToList(), order);
        Assert.Equal(RecEngine.OrderCollection, rows.First(r => r.Type == "collection").Order);
        Assert.True(rows.ToList().FindIndex(r => r.Type == "collection") < rows.ToList().FindIndex(r => r.Type == "because"));
    }
}
