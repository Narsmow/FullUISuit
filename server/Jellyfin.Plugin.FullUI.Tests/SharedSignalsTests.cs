using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Library;
using Jellyfin.Plugin.FullUI.Recs;
using Xunit;
using static Jellyfin.Plugin.FullUI.Tests.Kit;

namespace Jellyfin.Plugin.FullUI.Tests;

/// <summary>Collaborative filtering and shared charts (audit item 5).</summary>
public class SharedSignalsTests
{
    [Fact]
    public void ExcludedUsers_AreIgnoredByCollaborativeFiltering()
    {
        // D sorts before Y by name; with identical features only CF can put Y first - and only if U2's data is used.
        var x = Movie(1, "X", new[] { "Action" });
        var y = Movie(2, "Y", new[] { "Action" });
        var d = Movie(3, "D", new[] { "Action" });
        var catalog = new[] { x, y, d }.Concat(Many(10, 6, "Pad", "Action")).ToList();
        var signals = new[] { Play(U1, x), Play(U2, x), Play(U2, y) };

        var used = Row(Build(Input(U1, catalog, signals)), "toppicks")!;
        var ignored = Row(Build(Input(U1, catalog, signals, excluded: new[] { U2 })), "toppicks")!;

        Assert.Equal(y.Id, used.Items[0].Item.Id);
        Assert.NotEqual(y.Id, ignored.Items[0].Item.Id);
        Assert.False(RecEngine.IsCfEnabled(Input(U1, catalog, signals, excluded: new[] { U2 })));
    }

    [Fact]
    public void AnotherUser_NeedsEnoughOverlap_AndSmallOverlapIsShrunk()
    {
        var a = Movie(1, "A", new[] { "Action" });
        var b = Movie(2, "B", new[] { "Action" });
        var viaOne = Movie(3, "Via One", new[] { "Action" });       // U2 shares only A with the viewer
        var viaTwo = Movie(4, "Zzz Via Two", new[] { "Action" });   // U3 shares A and B
        var catalog = new[] { a, b, viaOne, viaTwo }.Concat(Many(10, 6, "Pad", "Action")).ToList();
        var signals = new[]
        {
            Play(U1, a), Play(U1, b),
            Play(U2, a), Play(U2, viaOne),
            Play(U3, a), Play(U3, b), Play(U3, viaTwo),
        };

        var picks = Row(Build(Input(U1, catalog, signals)), "toppicks")!;

        int Pos(CatalogItem c) => picks.Items.ToList().FindIndex(i => i.Item.Id == c.Id);
        Assert.True(Pos(viaTwo) < Pos(viaOne), "the user with the larger overlap decides, even though the alphabet says otherwise");
    }

    [Fact]
    public void WeightsOfOtherUsers_AreCachedBetweenRequests_AndRecomputedWhenDataChanges()
    {
        var catalog = Many(1, 20, "M", "Action");
        var signals = catalog.Take(5).SelectMany(c => new[] { Play(U1, c), Play(U2, c), Play(U3, c) }).ToList();
        var cache = new UserWeightsCache();

        Build(Input(U1, catalog, signals, cache: cache, fingerprint: "v1"));
        var afterFirst = cache.Misses;
        Build(Input(U1, catalog, signals, cache: cache, fingerprint: "v1"));
        Assert.Equal(afterFirst, cache.Misses);                 // nothing recomputed
        Assert.True(afterFirst >= 2);

        Build(Input(U1, catalog, signals, cache: cache, fingerprint: "v2"));
        Assert.True(cache.Misses > afterFirst);                 // new data: recomputed
    }

    [Fact]
    public void KidSignals_StayOutOfChartsTrendingAndPopularity_ForOtherViewers()
    {
        var movies = Many(1, 8, "M", "Family");
        // Only the kid watched the first three; the adults watched the last three.
        var signals = movies.Take(3).Select(m => Play(U3, m, 1)).Concat(movies.Skip(5).SelectMany(m => new[] { Play(U1, m, 1), Play(U2, m, 1) })).ToList();

        var withKid = Build(Input(Viewer, movies, signals));
        var kidExcluded = Build(Input(Viewer, movies, signals, kids: new[] { U3 }));

        var kidTitles = movies.Take(3).Select(m => m.Id).ToHashSet();
        Assert.Contains(Row(withKid, "top10-movies")!.Items, i => kidTitles.Contains(i.Item.Id));
        Assert.DoesNotContain(Row(kidExcluded, "top10-movies")!.Items, i => kidTitles.Contains(i.Item.Id));
        Assert.DoesNotContain(Row(kidExcluded, "trending")?.Items ?? new List<RankedItem>(), i => kidTitles.Contains(i.Item.Id));
        Assert.False(RecEngine.IsCfEnabled(Input(U1, movies, new[] { Play(U1, movies[0]), Play(U3, movies[0]) }, kids: new[] { U3 })));
    }

    [Fact]
    public void AKidViewer_StillSeesWhatOtherKidsWatch()
    {
        var movies = Many(1, 8, "M", "Family");
        var signals = movies.Take(4).SelectMany(m => new[] { Play(U3, m, 1), Play(U4, m, 1) }).ToList();
        var rows = Build(Input(Viewer, movies, signals, kids: new[] { Viewer, U3, U4 }));
        Assert.Equal(4, Row(rows, "top10-movies")!.Items.Count);
    }

    [Fact]
    public void KidDetector_FlagsUsersWhoCannotSeeMatureTitles_Only()
    {
        var mature = Enumerable.Range(1, 6).Select(i => Movie(i, $"Mature{i}", new[] { "Thriller" }, rated: i % 2 == 0 ? "R" : "TV-MA")).ToList();
        var family = Enumerable.Range(10, 6).Select(i => Movie(i, $"Family{i}", new[] { "Family" }, rated: "PG")).ToList();
        var catalog = mature.Concat(family).ToList();

        Assert.True(KidDetector.IsRestrictive(catalog, family.Select(f => f.Id).ToHashSet()));
        Assert.False(KidDetector.IsRestrictive(catalog, catalog.Select(c => c.Id).ToHashSet()));
        Assert.False(KidDetector.IsRestrictive(catalog, mature.Take(3).Concat(family).Select(c => c.Id).ToHashSet()));

        // A family-only library has no mature titles to hide, so nobody can be told apart.
        Assert.False(KidDetector.IsRestrictive(family, family.Take(1).Select(f => f.Id).ToHashSet()));
    }
}
