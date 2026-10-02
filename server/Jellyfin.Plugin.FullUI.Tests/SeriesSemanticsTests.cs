using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Events;
using Jellyfin.Plugin.FullUI.Library;
using Jellyfin.Plugin.FullUI.Recs;
using Xunit;
using static Jellyfin.Plugin.FullUI.Tests.Kit;

namespace Jellyfin.Plugin.FullUI.Tests;

/// <summary>Series are not movies: one finished episode must not mean "I finished the show" (audit item 1).</summary>
public class SeriesSemanticsTests
{
    private static List<CatalogItem> Filler() => Many(100, 60, "Fill", "Drama");

    [Fact]
    public void OneFinishedEpisode_DoesNotMakeTheSeriesCompleted_Seed_OrWatchAgain()
    {
        // Old behaviour: one episode at >=90% set Completed on the whole series: it became a "Because you watched" seed
        // and, 30 days later, a Watch Again entry. A 10-episode series with 1 episode watched is neither.
        var show = Show(1, "Long Show", new[] { "Drama" }, new[] { 10 });
        var finishedMovies = Many(2, 3, "Done", "Drama");
        var catalog = Filler().Append(show).Concat(finishedMovies).ToList();
        var signals = finishedMovies.Select(m => Play(Viewer, m, 60)).Append(Ep(Viewer, show, 1, 1, 40)).ToList();

        var rows = Build(Input(Viewer, catalog, signals));

        Assert.DoesNotContain(rows, r => r.Id == $"because-{show.Id:N}");
        var again = Row(rows, "again")!;
        Assert.DoesNotContain(again.Items, i => i.Item.Id == show.Id);
        Assert.Equal(3, again.Items.Count);
        var weight = RecEngine.ItemWeights(Input(Viewer, catalog, signals), Viewer)[show.Id];
        Assert.True(weight < 0.2, $"one of ten episodes should be a weak signal, was {weight}");
    }

    [Fact]
    public void CaughtUpSeries_IsFinished_ASeed_AndWatchAgain()
    {
        var show = Show(1, "Short Show", new[] { "Drama" }, new[] { 5, 5 });
        var others = Many(10, 30, "Dra", "Drama");
        var movies = Many(2, 2, "M", "Drama");
        var catalog = others.Append(show).Concat(movies).ToList();
        var signals = WatchSeasons(Viewer, show, 60, 1, 2).Concat(movies.Select(m => Play(Viewer, m, 70))).ToList();

        var rows = Build(Input(Viewer, catalog, signals));

        Assert.Contains(Row(rows, "again")!.Items, i => i.Item.Id == show.Id);
        Assert.Contains(rows, r => r.Id == $"because-{show.Id:N}");
        Assert.DoesNotContain(rows.Where(r => r.Type is "toppicks" or "genre"), r => r.Items.Any(i => i.Item.Id == show.Id));
        Assert.Equal(Math.Pow(0.5, 60 / RecEngine.HalfLifeDays), RecEngine.ItemWeights(Input(Viewer, catalog, signals), Viewer)[show.Id], 2);   // full weight, decayed
    }

    [Fact]
    public void SeriesAffinity_ScalesWithTheShareOfEpisodesWatched()
    {
        var show = Show(1, "Show", new[] { "Drama" }, new[] { 10 });
        double W(int watched) => RecEngine.ItemWeights(
            Input(Viewer, new[] { show }, Enumerable.Range(1, watched).Select(e => Ep(Viewer, show, 1, e, 0))), Viewer)[show.Id];

        Assert.Equal(0.2, W(2), 2);
        Assert.Equal(0.8, W(8), 2);
        Assert.True(W(8) > 3 * W(2));
    }

    [Fact]
    public void Specials_AreIgnored_EverywhereTheyCouldCount()
    {
        // Season 0 is behind-the-scenes material: no signal is recorded, stored ones are ignored, and it never adds episodes to a series.
        Assert.Null(EventTracker.BuildSignal(Viewer, Id(5), Id(1), true, 1000, 990, true, Now, season: 0, episode: 3));
        var regular = EventTracker.BuildSignal(Viewer, Id(5), Id(1), true, 1000, 990, true, Now, season: 2, episode: 3)!;
        Assert.Equal(2, regular.Season);
        Assert.Equal(3, regular.Episode);

        var show = Show(1, "Show", new[] { "Drama" }, new[] { 4 });
        var special = Ep(Viewer, show, 0, 1, 1);
        Assert.Empty(RecEngine.ItemWeights(Input(Viewer, new[] { show }, new[] { special }), Viewer));

        var agg = JellyfinCatalog.AggregateEpisodes(new[]
        {
            new JellyfinCatalog.EpisodeRow(show.Id, 0, Now), new JellyfinCatalog.EpisodeRow(show.Id, 1, Now.AddDays(-5)),
            new JellyfinCatalog.EpisodeRow(show.Id, 1, Now.AddDays(-4)), new JellyfinCatalog.EpisodeRow(show.Id, 2, Now.AddDays(-1)),
        });
        Assert.Equal(new[] { 2, 1 }, agg[show.Id].Seasons.Select(s => s.Episodes).ToArray());
        Assert.Equal(Now.AddDays(-1), agg[show.Id].Latest);   // the special added "now" does not count as a new episode
    }

    [Fact]
    public void OldSignalsWithoutSeasonAndEpisode_StillLoad_AndStillWork()
    {
        const string legacy = "{\"UserId\":\"11111111-1111-1111-1111-111111111111\",\"ItemId\":\"22222222-2222-2222-2222-222222222222\",\"IsEpisode\":true,\"At\":\"2026-05-30T00:00:00Z\",\"Completion\":1,\"Completed\":true}";
        var s = JsonSerializer.Deserialize<PlaySignal>(legacy)!;
        Assert.Null(s.Season);
        Assert.Null(s.Episode);

        var show = Show(1, "Show", new[] { "Drama" }, new[] { 10 });
        s.ItemId = show.Id;
        s.UserId = Viewer;
        var w = RecEngine.ItemWeights(Input(Viewer, new[] { show }, new[] { s }), Viewer);
        Assert.InRange(w[show.Id], 0.1, 0.3);   // counts as one watched episode
    }

    [Fact]
    public void NewSeason_NeedsASeasonAfterTheHighestWatched_AddedRecently()
    {
        var fresh = Show(1, "Fresh Season", new[] { "Drama" }, new[] { 8, 8, 8 }, seasonAddedDaysAgo: new[] { 500, 300, 10 }, recentEpisodeDaysAgo: new[] { 10, 11, 12 });
        var stale = Show(2, "Old Season Two", new[] { "Drama" }, new[] { 8, 8 }, seasonAddedDaysAgo: new[] { 500, 300 });
        var behind = Show(3, "Still On Season One", new[] { "Drama" }, new[] { 8, 8 }, seasonAddedDaysAgo: new[] { 500, 20 });
        var catalog = Filler().Concat(new[] { fresh, stale, behind }).ToList();
        var signals = WatchSeasons(Viewer, fresh, 30, 1, 2)
            .Concat(WatchSeasons(Viewer, stale, 30, 1))
            .Concat(Enumerable.Range(1, 3).Select(e => Ep(Viewer, behind, 1, e, 3)))   // only a few episodes of season 1
            .ToList();

        var row = Row(Build(Input(Viewer, catalog, signals)), "newseasons")!;

        Assert.Equal(new[] { "Fresh Season" }, row.Items.Select(i => i.Item.Name).ToArray());
        Assert.Equal("New season of Fresh Season", row.Items[0].Reason);
    }

    [Fact]
    public void AnEpisodeAddedToAShowTheUserIsFarBehindOn_IsNotNewEpisodes()
    {
        // Old behaviour: "any episode added in the last 14 days" put the show in New Episodes for everybody who ever touched it.
        var show = Show(1, "Long Runner", new[] { "Drama" }, new[] { 20 }, recentEpisodeDaysAgo: new[] { 2 });
        var catalog = Filler().Append(show).ToList();
        var behind = Enumerable.Range(1, 3).Select(e => Ep(Viewer, show, 1, e, 3)).ToList();
        var caughtUp = Enumerable.Range(1, 19).Select(e => Ep(Viewer, show, 1, e, 3)).ToList();

        Assert.Null(Row(Build(Input(Viewer, catalog, behind)), "newseasons"));
        var row = Row(Build(Input(Viewer, catalog, caughtUp)), "newseasons")!;
        Assert.Equal(show.Id, row.Items.Single().Item.Id);
        Assert.Equal("New episodes of Long Runner", row.Items[0].Reason);
    }

    [Fact]
    public void WatchedEpisodesFromJellyfin_CountEvenWithoutStoredSignals()
    {
        // Backfill keeps only three signals per title; Jellyfin knows the rest.
        var show = Show(1, "Binged", new[] { "Drama" }, new[] { 10 });
        var done = Many(2, 2, "Done", "Drama");
        var catalog = Filler().Append(show).Concat(done).ToList();
        var signals = new[] { Ep(Viewer, show, 1, 10, 60) }.Concat(done.Select(m => Play(Viewer, m, 61))).ToList();
        var watch = new Dictionary<Guid, SeriesWatchInfo> { [show.Id] = new SeriesWatchInfo(new Dictionary<int, int> { [1] = 10 }) };

        var rows = Build(Input(Viewer, catalog, signals, watch: watch));

        Assert.Contains(Row(rows, "again")!.Items, i => i.Item.Id == show.Id);
        Assert.DoesNotContain(rows.Where(r => r.Type is "toppicks" or "recent"), r => r.Items.Any(i => i.Item.Id == show.Id));   // finished: not offered again
        var without = Build(Input(Viewer, catalog, signals));
        Assert.DoesNotContain(Row(without, "again")?.Items ?? new List<RankedItem>(), i => i.Item.Id == show.Id);   // one stored episode of ten is not "finished"
    }

    [Fact]
    public void DroppedShow_IsNotContinuedNorRecommended_ButARecentOneIs()
    {
        var dropped = Show(1, "Abandoned", new[] { "Drama" }, new[] { 10 });
        var active = Show(2, "Active", new[] { "Drama" }, new[] { 10 });
        var catalog = Filler().Concat(new[] { dropped, active }).ToList();
        var signals = new[] { Ep(Viewer, dropped, 1, 1, 90), Ep(Viewer, active, 1, 1, 3) };

        var rows = Build(Input(Viewer, catalog, signals, nextUp: new[] { dropped.Id, active.Id }));

        var cont = Row(rows, "continue")!;
        Assert.Equal(new[] { active.Id }, cont.Items.Select(i => i.Item.Id).ToArray());
        Assert.DoesNotContain(AllIds(rows), id => id == dropped.Id);
        Assert.True(RecEngine.ItemWeights(Input(Viewer, catalog, signals), Viewer)[dropped.Id] < 0);
    }

    [Fact]
    public void ShowRatedUpOrInMyList_IsNeverCalledDropped()
    {
        var show = Show(1, "Slow Burn", new[] { "Drama" }, new[] { 10 });
        var catalog = Filler().Append(show).ToList();
        var signals = new[] { Ep(Viewer, show, 1, 1, 120) };

        var loved = Build(Input(Viewer, catalog, signals, ratings: new() { [Key(Viewer, show)] = 2 }, nextUp: new[] { show.Id }));
        var listed = Build(Input(Viewer, catalog, signals, myList: new() { Key(Viewer, show) }, nextUp: new[] { show.Id }));

        Assert.Contains(Row(loved, "continue")?.Items ?? new List<RankedItem>(), i => i.Item.Id == show.Id);
        Assert.Contains(Row(listed, "continue")?.Items ?? new List<RankedItem>(), i => i.Item.Id == show.Id);
    }
}
