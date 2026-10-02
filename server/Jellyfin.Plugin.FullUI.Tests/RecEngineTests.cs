using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Library;
using Jellyfin.Plugin.FullUI.Recs;
using Xunit;

namespace Jellyfin.Plugin.FullUI.Tests;

public class RecEngineTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static Guid Id(int n) => new(n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1);

    private static CatalogItem Movie(int n, string name, string[] genres, float? rating = 6.5f, int daysOld = 400) => new()
    {
        Id = Id(n),
        Kind = CatalogKind.Movie,
        Name = name,
        Year = 2005,
        Rating = rating,
        Genres = genres,
        DateAdded = Now.AddDays(-daysOld),
    };

    private static CatalogItem Show(int n, string name, string[] genres, float? rating = 6.5f, int daysOld = 400, int? episodeDaysOld = null) => new()
    {
        Id = Id(n),
        Kind = CatalogKind.Series,
        Name = name,
        Year = 2010,
        Rating = rating,
        Genres = genres,
        DateAdded = Now.AddDays(-daysOld),
        LatestEpisodeAdded = episodeDaysOld is int d ? Now.AddDays(-d) : null,
    };

    private static PlaySignal Play(Guid user, CatalogItem item, double daysAgo = 1, double completion = 1, bool? completed = null, bool episode = false) => new()
    {
        UserId = user,
        ItemId = item.Id,
        IsEpisode = episode,
        At = Now.AddDays(-daysAgo),
        Completion = completion,
        Completed = completed ?? completion >= 0.9,
    };

    private static RecInput Input(Guid user, IEnumerable<CatalogItem> catalog, IEnumerable<PlaySignal>? signals = null,
        Dictionary<string, int>? ratings = null, HashSet<string>? myList = null, IEnumerable<Guid>? excluded = null,
        int window = 7, Dictionary<string, string>? titles = null)
    {
        var cat = catalog.ToList();
        return new RecInput
        {
            UserId = user,
            Catalog = cat,
            Visible = cat.Select(c => c.Id).ToHashSet(),
            Signals = (signals ?? Array.Empty<PlaySignal>()).ToList(),
            Ratings = ratings ?? new Dictionary<string, int>(),
            MyList = myList ?? new HashSet<string>(),
            Now = Now,
            ServerName = "Srv",
            TopTenWindowDays = window,
            ExcludedUsers = (excluded ?? Array.Empty<Guid>()).ToHashSet(),
            RowTitles = titles ?? new Dictionary<string, string>(),
        };
    }

    private static List<CatalogItem> Many(int start, int count, string prefix, params string[] genres) =>
        Enumerable.Range(start, count).Select(i => Movie(i, $"{prefix}{i:00}", genres)).ToList();

    private static RecRow? Row(IReadOnlyList<RecRow> rows, string id) => rows.FirstOrDefault(r => r.Id == id);

    private static readonly Guid U1 = Id(9001), U2 = Id(9002), U3 = Id(9003), U4 = Id(9004);

    [Fact]
    public void TopPicks_ExcludeWatchedTitles()
    {
        var action = Many(1, 12, "Act", "Action");
        var watched = action.Take(3).ToList();
        var input = Input(U1, action, watched.Select(w => Play(U1, w)));

        var picks = Row(RecEngine.Build(input), "toppicks");

        Assert.NotNull(picks);
        Assert.Equal("Top Picks for You", picks!.Title);
        Assert.DoesNotContain(picks.Items, i => watched.Any(w => w.Id == i.Item.Id));
        Assert.Equal(9, picks.Items.Count);
    }

    [Fact]
    public void ThumbsDown_LowersSimilarTitles()
    {
        var a = Movie(1, "A", new[] { "Action", "Comedy" });
        var x = Movie(2, "X", new[] { "Action" });
        var y = Movie(3, "Y", new[] { "Action" });
        var z = Movie(4, "Z", new[] { "Comedy" });
        var pad = Many(10, 6, "Pad", "Drama");
        var catalog = new[] { a, x, y, z }.Concat(pad).ToList();
        var signals = new[] { Play(U1, a) };

        var neutral = Row(RecEngine.Build(Input(U1, catalog, signals)), "toppicks")!;
        var down = Row(RecEngine.Build(Input(U1, catalog, signals,
            ratings: new() { [StoreData.UserItemKey(U1, x.Id)] = -1 })), "toppicks")!;

        int Pos(RecRow r, CatalogItem c) => r.Items.ToList().FindIndex(i => i.Item.Id == c.Id);
        Assert.DoesNotContain(down.Items, i => i.Item.Id == x.Id);          // the rated title itself is gone
        Assert.True(Pos(down, z) < Pos(down, y), "comedy should now beat the action sibling");
        // Without the thumbs-down, Y (an Action sibling of the watched title) is not behind Z (a Comedy one)...
        Assert.True(Pos(neutral, y) < Pos(neutral, z), "precondition: Y should outrank Z before the thumbs-down");
        // ...with it, the Action title X is gone and the whole Action genre is pushed down, so Z overtakes Y.
        Assert.True(Pos(down, z) < Pos(down, y), "thumbs-down on an Action title must push the other Action title behind the Comedy one");
    }

    [Fact]
    public void Love_BoostsSimilarTitles()
    {
        var a = Movie(1, "A", new[] { "Horror" });
        var h = Movie(2, "H", new[] { "Horror" });
        var c = Movie(3, "Aaa Comedy", new[] { "Comedy" });
        var pad = Many(10, 6, "Pad", "Drama");
        var catalog = new[] { a, h, c }.Concat(pad).ToList();

        var rows = RecEngine.Build(Input(U1, catalog, new[] { Play(U1, a, completion: 0.3, completed: false) },
            ratings: new() { [StoreData.UserItemKey(U1, a.Id)] = 2 }));

        var picks = Row(rows, "toppicks")!;
        Assert.Equal(h.Id, picks.Items[0].Item.Id);
    }

    [Fact]
    public void TopTen_CountsDistinctUsers_ExcludesUsersAndOldPlays()
    {
        var movies = Many(1, 6, "M", "Action");
        var m = movies;
        var signals = new List<PlaySignal>
        {
            // m0: 3 distinct users
            Play(U1, m[0], 1), Play(U2, m[0], 1), Play(U3, m[0], 2),
            // m1: 2 users (one rewatched many times)
            Play(U1, m[1], 1), Play(U1, m[1], 2), Play(U1, m[1], 3), Play(U2, m[1], 1),
            // m2: 1 user, 2 plays
            Play(U1, m[2], 1), Play(U1, m[2], 2),
            // m3: popular but outside the 7 day window
            Play(U1, m[3], 10), Play(U2, m[3], 11), Play(U3, m[3], 12), Play(U4, m[3], 20),
            // m4: only played by an excluded user
            Play(U4, m[4], 1),
            // m5: partial play does not count as completed
            Play(U1, m[5], 1, completion: 0.5, completed: false),
        };

        var rows = RecEngine.Build(Input(U2, movies, signals, excluded: new[] { U4 }));

        var top = Row(rows, "top10-movies");
        Assert.NotNull(top);
        Assert.Equal(new[] { m[0].Id, m[1].Id, m[2].Id }, top!.Items.Select(i => i.Item.Id));
        Assert.Equal(new int?[] { 1, 2, 3 }, top.Items.Select(i => i.Rank));
        Assert.Contains("#1 in Movies", top.Items[0].Badges);
        Assert.Equal("Top 10 Movies on Srv This Week", top.Title);
        Assert.Equal("top10", top.Type);
    }

    [Fact]
    public void TopTen_WindowIsConfigurable()
    {
        var movies = Many(1, 3, "M", "Action");
        var signals = movies.Select(mv => Play(U1, mv, 10)).ToList();

        Assert.Null(Row(RecEngine.Build(Input(U1, movies, signals, window: 7)), "top10-movies"));
        Assert.NotNull(Row(RecEngine.Build(Input(U1, movies, signals, window: 14)), "top10-movies"));
    }

    [Fact]
    public void TopTen_NeedsThreeEntries_AndSplitsMoviesFromShows()
    {
        var movies = Many(1, 2, "M", "Action");
        var shows = Enumerable.Range(20, 3).Select(i => Show(i, $"S{i}", new[] { "Drama" })).ToList();
        var catalog = movies.Concat(shows).ToList();
        var signals = catalog.Select(c => Play(U1, c, 1, episode: c.Kind == CatalogKind.Series)).ToList();

        var rows = RecEngine.Build(Input(U2, catalog, signals));

        Assert.Null(Row(rows, "top10-movies"));          // only 2 real entries
        var shows10 = Row(rows, "top10-shows");
        Assert.NotNull(shows10);
        Assert.Contains("#1 in Shows", shows10!.Items[0].Badges);
    }

    [Fact]
    public void TopTen_OnlyContainsTitlesVisibleToUser()
    {
        var movies = Many(1, 5, "M", "Action");
        var signals = movies.Select(mv => Play(U1, mv, 1)).ToList();
        var input = Input(U2, movies, signals);
        var hidden = movies[0].Id;
        var visible = movies.Where(mv => mv.Id != hidden).Select(mv => mv.Id).ToHashSet();
        input = new RecInput
        {
            UserId = input.UserId, Catalog = input.Catalog, Visible = visible, Signals = input.Signals, Now = Now, ServerName = "Srv",
        };

        var all = RecEngine.Build(input).SelectMany(r => r.Items);
        Assert.DoesNotContain(all, i => i.Item.Id == hidden);
    }

    [Fact]
    public void Recommendations_AreDeduplicatedAcrossRows()
    {
        var catalog = new List<CatalogItem>();
        catalog.AddRange(Many(1, 3, "Seed", "Action", "Thriller"));
        catalog.AddRange(Many(10, 30, "Act", "Action", "Thriller"));
        catalog.AddRange(Many(50, 30, "Com", "Comedy"));
        catalog.AddRange(Enumerable.Range(100, 10).Select(i => Movie(i, $"Gem{i}", new[] { "Drama" }, rating: 8.5f)));
        var signals = catalog.Take(3).Select(c => Play(U1, c, 5)).ToList();

        var rows = RecEngine.Build(Input(U1, catalog, signals));

        var recRows = rows.Where(r => r.Type is "toppicks" or "because" or "genre" or "hidden").ToList();
        Assert.True(recRows.Count >= 2);
        var ids = recRows.SelectMany(r => r.Items.Select(i => i.Item.Id)).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(rows, r => Assert.True(r.Items.Count >= 1));
    }

    [Fact]
    public void RowsWithFewerThanFiveItems_AreDropped()
    {
        var catalog = Many(1, 4, "Act", "Action");   // after one watched, only 3 candidates
        var rows = RecEngine.Build(Input(U1, catalog, new[] { Play(U1, catalog[0]) }));

        Assert.Null(Row(rows, "toppicks"));
        Assert.Null(Row(rows, "recent"));
    }

    [Fact]
    public void ColdStart_FallsBackToPopularityAndQuality()
    {
        var catalog = Many(1, 8, "M", "Action");
        catalog[5] = Movie(6, "Crowd Favourite", new[] { "Action" }, rating: 9f);
        var signals = new List<PlaySignal> { Play(U2, catalog[5], 2), Play(U3, catalog[5], 3), Play(U2, catalog[1], 3) };

        var rows = RecEngine.Build(Input(U1, catalog, signals));

        var picks = Row(rows, "toppicks");
        Assert.NotNull(picks);
        Assert.Equal("Popular on Srv", picks!.Title);
        Assert.Equal(catalog[5].Id, picks.Items[0].Item.Id);
        Assert.Null(Row(rows, "genre-action"));
        Assert.DoesNotContain(rows, r => r.Type == "because");
    }

    [Fact]
    public void Badges_AreComputedFromAgeAndRating()
    {
        var now = Now;
        var fresh = Movie(1, "Fresh", new[] { "Action" }, rating: 8.4f, daysOld: 3);
        var oldShow = Show(2, "Returning", new[] { "Drama" }, rating: 7f, daysOld: 200, episodeDaysOld: 2);
        var newShow = Show(3, "Brand New", new[] { "Drama" }, daysOld: 2, episodeDaysOld: 1);
        var plain = Movie(4, "Plain", new[] { "Drama" }, rating: 7.9f, daysOld: 100);

        Assert.Equal(new[] { "Recently Added", "Top Rated" }, RecEngine.Badges(fresh, now));
        Assert.Equal(new[] { "New Episodes" }, RecEngine.Badges(oldShow, now));
        Assert.Equal(new[] { "Recently Added" }, RecEngine.Badges(newShow, now));   // series younger than 30d are not "new episodes"
        Assert.Empty(RecEngine.Badges(plain, now));
        Assert.Equal(new[] { "#3 in Shows", "New Episodes" }, RecEngine.Badges(oldShow, now, 3));
    }

    [Fact]
    public void Rows_AreInContractOrder()
    {
        var catalog = new List<CatalogItem>();
        catalog.AddRange(Enumerable.Range(1, 120).Select(i => Movie(i, $"Act{i:00}", new[] { "Action", "Thriller" }, rating: i > 100 ? 8.2f : 6.5f, daysOld: i < 8 ? 3 : 300)));
        catalog.AddRange(Enumerable.Range(300, 30).Select(i => Movie(i, $"Com{i}", new[] { "Comedy" })));
        catalog.AddRange(Enumerable.Range(200, 6).Select(i => Show(i, $"Show{i}", new[] { "Drama" }, daysOld: 120, episodeDaysOld: 3)));
        var signals = new List<PlaySignal>
        {
            Play(U1, catalog[0], 40), Play(U1, catalog[1], 35), Play(U1, catalog[2], 33),
            Play(U1, catalog[3], 1, completion: 0.4, completed: false),
            Play(U1, catalog.First(c => c.Id == Id(200)), 2, episode: true),
        };
        foreach (var u in new[] { U2, U3 })
        {
            foreach (var c in catalog.Where(c => c.Name.StartsWith("Com")).Take(4))
            {
                signals.Add(Play(u, c, 1));
            }

            foreach (var s in catalog.Where(c => c.Kind == CatalogKind.Series).Take(4))
            {
                signals.Add(Play(u, s, 1, episode: true));
            }
        }

        var mylist = new HashSet<string> { StoreData.UserItemKey(U1, catalog[130].Id) };
        var rows = RecEngine.Build(Input(U1, catalog, signals, myList: mylist));

        var types = rows.Select(r => r.Order).ToList();
        Assert.Equal(types.OrderBy(x => x).ToList(), types);

        // The exact shape docs/api-contract.md promises (hidden gems/Coming Soon are absent for this data; Coming Soon is added by HomeService).
        Assert.Equal(
            new[] { "continue", "toppicks", "top10", "because", "because", "because", "trending", "mylist", "genre", "genre", "genre", "top10", "newseasons", "recent", "again" },
            rows.Select(r => r.Type).ToArray());
        var ids = rows.Select(r => r.Id).ToList();
        Assert.Equal("top10-movies", ids[2]);
        Assert.Equal("top10-shows", ids[11]);
        Assert.All(rows.Where(r => r.Type == "because"), r => Assert.StartsWith("because-", r.Id));
        Assert.All(rows.Where(r => r.Type == "genre"), r => Assert.StartsWith("genre-", r.Id));
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(new[] { "genre-action", "genre-comedy", "genre-thriller" }, ids.Where(i => i.StartsWith("genre-", StringComparison.Ordinal)).OrderBy(i => i, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void GenreRows_UseTitleOverride()
    {
        var catalog = new List<CatalogItem>();
        catalog.AddRange(Many(1, 3, "Seed", "Thriller"));
        catalog.AddRange(Many(10, 130, "Thr", "Thriller"));
        catalog.AddRange(Many(200, 8, "Dra", "Drama"));
        var signals = catalog.Take(3).Select(c => Play(U1, c, 5)).ToList();

        var rows = RecEngine.Build(Input(U1, catalog, signals, titles: new() { ["Thriller"] = "Edge-of-Seat Thrillers" }));
        var plain = RecEngine.Build(Input(U1, catalog, signals));

        Assert.Contains(rows, r => r.Type == "genre" && r.Title == "Edge-of-Seat Thrillers");
        Assert.Contains(plain, r => r.Type == "genre" && r.Title == "Thriller Picks for You");
    }

    [Fact]
    public void CollaborativeFiltering_RequiresTwoUsersWithSignals()
    {
        // D sorts before Y by name; with identical features only CF can put Y first.
        var x = Movie(1, "X", new[] { "Action" });
        var y = Movie(2, "Y", new[] { "Action" });
        var d = Movie(3, "D", new[] { "Action" });
        var pad = Many(10, 6, "Pad", "Action");
        var catalog = new[] { x, y, d }.Concat(pad).ToList();

        var solo = new[] { Play(U1, x) };
        var duo = solo.Concat(new[] { Play(U2, x), Play(U2, y) }).ToList();

        Assert.False(RecEngine.IsCfEnabled(Input(U1, catalog, solo)));
        Assert.True(RecEngine.IsCfEnabled(Input(U1, catalog, duo)));

        var soloPicks = Row(RecEngine.Build(Input(U1, catalog, solo)), "toppicks")!;
        var duoPicks = Row(RecEngine.Build(Input(U1, catalog, duo)), "toppicks")!;
        Assert.NotEqual(y.Id, soloPicks.Items[0].Item.Id);
        Assert.Equal(y.Id, duoPicks.Items[0].Item.Id);
    }

    [Fact]
    public void Weights_DecayWithHalfLife()
    {
        var m = Movie(1, "M", new[] { "Action" });
        var fresh = RecEngine.ItemWeights(Input(U1, new[] { m }, new[] { Play(U1, m, 0) }), U1)[m.Id];
        var half = RecEngine.ItemWeights(Input(U1, new[] { m }, new[] { Play(U1, m, RecEngine.HalfLifeDays) }), U1)[m.Id];
        var old = RecEngine.ItemWeights(Input(U1, new[] { m }, new[] { Play(U1, m, 360) }), U1)[m.Id];

        Assert.Equal(1.0, fresh, 3);
        Assert.Equal(0.5, half, 3);
        Assert.True(old < 0.1);
    }

    [Fact]
    public void Weights_PartialAbandonRewatchAndRatings()
    {
        var m = Movie(1, "M", new[] { "Action" });
        double W(IEnumerable<PlaySignal> s, Dictionary<string, int>? r = null, HashSet<string>? l = null) =>
            RecEngine.ItemWeights(Input(U1, new[] { m }, s, r, l), U1)[m.Id];

        Assert.Equal(0.4, W(new[] { Play(U1, m, 0, 0.5, false) }), 3);                       // completion * 0.8
        Assert.Equal(-0.3, W(new[] { Play(U1, m, 0, 0.05, false) }), 3);                     // movie abandon
        Assert.Equal(1.3, W(new[] { Play(U1, m, 0), Play(U1, m, 0) }), 3);                    // rewatch bonus
        var key = StoreData.UserItemKey(U1, m.Id);
        Assert.Equal(-3, W(new[] { Play(U1, m, 0) }, new() { [key] = -1 }), 3);               // thumbs-down overrides
        Assert.Equal(3, W(new[] { Play(U1, m, 0) }, new() { [key] = 2 }), 3);
        Assert.Equal(1.5, W(Array.Empty<PlaySignal>(), new() { [key] = 1 }), 3);
        Assert.Equal(1.0, W(Array.Empty<PlaySignal>(), null, new HashSet<string> { key }), 3); // My List +1
    }

    [Fact]
    public void ContinueWatching_ShowsLatestUnfinishedWithProgress()
    {
        var catalog = Many(1, 4, "M", "Action");
        var signals = new[]
        {
            Play(U1, catalog[0], 3, 0.4, false),
            Play(U1, catalog[1], 5, 0.5, false),
            Play(U1, catalog[1], 1, 1.0),                 // finished later: not in continue
            Play(U1, catalog[2], 2, 0.98, true),
        };

        var cont = Row(RecEngine.Build(Input(U1, catalog, signals)), "continue")!;

        Assert.Single(cont.Items);
        Assert.Equal(catalog[0].Id, cont.Items[0].Item.Id);
        Assert.Equal(0.4, cont.Items[0].Progress!.Value, 3);
    }

    [Fact]
    public void WatchAgain_AndNewEpisodes_AppearForWatchedTitles()
    {
        var movies = Many(1, 3, "M", "Action");
        var show = Show(20, "Returning", new[] { "Drama" }, daysOld: 300, episodeDaysOld: 2);
        var catalog = movies.Append(show).ToList();
        var signals = movies.Select(m => Play(U1, m, 60)).Append(Play(U1, show, 5, episode: true)).ToList();

        var rows = RecEngine.Build(Input(U1, catalog, signals));

        Assert.Equal(3, Row(rows, "again")!.Items.Count);
        var ns = Row(rows, "newseasons")!;
        Assert.Equal(show.Id, ns.Items[0].Item.Id);
        Assert.Contains("New Episodes", ns.Items[0].Badges);
    }

    [Fact]
    public void EmptyInputs_ProduceNoRowsInsteadOfThrowing()
    {
        Assert.Empty(RecEngine.Build(Input(U1, Array.Empty<CatalogItem>())));

        // Signals/ratings that point at titles no longer in the library are ignored.
        var ghost = Movie(99, "Gone", new[] { "Action" });
        var rows = RecEngine.Build(Input(U1, Array.Empty<CatalogItem>(), new[] { Play(U1, ghost) },
            ratings: new() { [StoreData.UserItemKey(U1, ghost.Id)] = 2, ["garbage"] = 1 },
            myList: new HashSet<string> { "also|garbage", StoreData.UserItemKey(U1, ghost.Id) }));
        Assert.Empty(rows);
    }

    [Fact]
    public void UserWithNothingVisible_GetsNoRows()
    {
        var catalog = Many(1, 8, "M", "Action");
        var input = Input(Id(5555), catalog, catalog.Select(c => Play(U1, c)));
        input = new RecInput { UserId = input.UserId, Catalog = input.Catalog, Visible = new HashSet<Guid>(), Signals = input.Signals, Now = Now };

        Assert.Empty(RecEngine.Build(input));   // nothing visible -> nothing to show
    }

    [Fact]
    public void UnknownUser_GetsPopularityOnlyAndNothingHidden()
    {
        var catalog = Many(1, 9, "M", "Action");
        var hidden = catalog[7];                        // exists, but this user may not see it (library access / parental rating)
        var popular = catalog[2];
        var signals = new[] { U1, U2, U3 }.Select(u => Play(u, popular, 2))
            .Concat(new[] { Play(U1, catalog[3], 2), Play(U1, hidden, 2), Play(U2, hidden, 2), Play(U3, hidden, 2) })
            .ToList();
        var input = new RecInput
        {
            UserId = Id(5555),                           // never played anything
            Catalog = catalog,
            Visible = catalog.Where(c => c.Id != hidden.Id).Select(c => c.Id).ToHashSet(),
            Signals = signals,
            Now = Now,
            ServerName = "Srv",
        };

        var rows = RecEngine.Build(input);

        var picks = Row(rows, "toppicks")!;
        Assert.Equal("Popular on Srv", picks.Title);                 // cold start = popularity, not personalisation
        Assert.Equal(popular.Id, picks.Items[0].Item.Id);            // the most-played title leads
        Assert.DoesNotContain(rows.SelectMany(r => r.Items), i => i.Item.Id == hidden.Id);   // no row leaks an invisible title
        Assert.DoesNotContain(rows, r => r.Type is "continue" or "because" or "genre" or "mylist" or "again");
    }

    [Fact]
    public void BuildSignal_RollsEpisodesUpToSeries_AndHandlesMissingData()
    {
        var series = Id(7);
        var ep = Jellyfin.Plugin.FullUI.Events.EventTracker.BuildSignal(U1, Id(8), series, true, 1000, 950, false, Now)!;
        Assert.Equal(series, ep.ItemId);
        Assert.True(ep.IsEpisode);
        Assert.True(ep.Completed);                    // >= 0.9 counts as completed

        Assert.Null(Jellyfin.Plugin.FullUI.Events.EventTracker.BuildSignal(U1, Id(8), null, true, 1000, 950, false, Now));   // orphan episode

        var movie = Jellyfin.Plugin.FullUI.Events.EventTracker.BuildSignal(U1, Id(9), null, false, null, null, true, Now)!;
        Assert.Equal(1.0, movie.Completion);          // unknown runtime but played to completion
        var short_ = Jellyfin.Plugin.FullUI.Events.EventTracker.BuildSignal(U1, Id(9), null, false, 1000, 50, false, Now)!;
        Assert.Equal(0.05, short_.Completion, 3);
        Assert.False(short_.Completed);
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?t=3", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://vimeo.com/12345", null)]
    [InlineData("not a url", null)]
    [InlineData("", null)]
    public void YouTubeKeys_AreParsedLeniently(string url, string? expected) =>
        Assert.Equal(expected, Jellyfin.Plugin.FullUI.Library.JellyfinCatalog.ParseYouTubeKey(url));
}
