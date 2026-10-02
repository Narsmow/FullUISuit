using System.Text.Json;
using Jellyfin.Plugin.FullUI.Api;
using Jellyfin.Plugin.FullUI.Configuration;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.DiscoveryTests;
using Jellyfin.Plugin.FullUI.Library;
using Jellyfin.Plugin.FullUI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.FullUI.ApiTests;

public class ItemDetailsTests : IDisposable
{
    private static readonly Guid Me = Http.User;

    private readonly TempStore _ts = new();
    private readonly FakeCatalog _catalog = new();
    private readonly FakeConfig _config = new() { Current = new PluginConfiguration { TmdbApiKey = "key", ServerName = "Srv" } };
    private readonly ScriptedSource _source = new();

    public void Dispose() => _ts.Dispose();

    private sealed class ScriptedSource : ITitleDetailsSource
    {
        public Dictionary<Guid, TitleData> Data { get; } = new();

        public Exception? Throw { get; set; }

        public Action? OnLoad { get; set; }

        public int Calls { get; private set; }

        public TitleData? Load(Guid userId, Guid itemId)
        {
            Calls++;
            OnLoad?.Invoke();
            if (Throw is not null)
            {
                throw Throw;
            }

            return Data.GetValueOrDefault(itemId);
        }
    }

    private sealed class FakeNext : INextUpSource
    {
        public List<NextUpEntry> Entries { get; } = new();

        public IReadOnlyList<Guid> NextUpSeries(Guid userId) => Entries.Select(e => e.SeriesId).ToList();

        public IReadOnlyList<NextUpEntry> NextUpEntries(Guid userId) => Entries;
    }

    private readonly FakeNext _next = new();

    private HomeService Home() => new(_ts.Store, _catalog, NullLogger<HomeService>.Instance, _config, _next);

    private ItemDetailsService Service() => new(Home(), _source, _ts.Store, _config, NullLogger<ItemDetailsService>.Instance);

    private ItemDetailsController Controller(Guid? user = null) => new ItemDetailsController(Service(), NullLogger<ItemDetailsController>.Instance).As(user ?? Me);

    private static EpisodeData Ep(int season, int number, bool played = false, double? progress = null, int n = 0)
        => new(new Guid(season * 1000 + number + n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 7), season, number, $"S{season}E{number}", null, 40, progress, played, true, new DateTime(2024, 1, 1).AddDays(number));

    private static SeasonData Season(int number, params EpisodeData[] eps)
        => new(new Guid(90000 + number, 0, 0, 0, 0, 0, 0, 0, 0, 0, 9), number, number == 0 ? "Specials" : $"Season {number}", eps);

    private CatalogItem AddShow(string name = "The Show", int tmdb = 5)
    {
        var show = Make.Item(name, tmdb, CatalogKind.Series, new[] { "Drama" });
        _catalog.Items.Add(show);
        return show;
    }

    private async Task<JsonElement> Json(IActionResult result)
    {
        var (status, body, _) = await Http.Render(result);
        Assert.Equal(200, status);
        return JsonDocument.Parse(body).RootElement;
    }

    [Fact]
    public async Task A_series_with_seasons_and_specials_lists_specials_last_and_counts_what_was_watched()
    {
        var show = AddShow();
        _source.Data[show.Id] = new TitleData(
            "A tagline",
            new[]
            {
                Season(0, Ep(0, 1)),
                Season(2, Ep(2, 2, progress: 0.4), Ep(2, 1, played: true), Ep(2, 3)),
                Season(1, Ep(1, 1, played: true), Ep(1, 2, played: true)),
            },
            Array.Empty<PersonData>(),
            Array.Empty<TrailerData>());
        _next.Entries.Add(new NextUpEntry(show.Id, 2, 2));

        var root = await Json(Controller().Details(show.Id));

        Assert.Equal("A tagline", root.GetProperty("tagline").GetString());
        var seasons = root.GetProperty("seasons").EnumerateArray().ToList();
        Assert.Equal(new[] { 1, 2, 0 }, seasons.Select(s => s.GetProperty("number").GetInt32()).ToArray());
        Assert.Equal(new[] { 2, 3, 1 }, seasons.Select(s => s.GetProperty("episodeCount").GetInt32()).ToArray());
        Assert.Equal(new[] { 2, 1, 0 }, seasons.Select(s => s.GetProperty("watchedCount").GetInt32()).ToArray());
        var s2 = seasons[1].GetProperty("episodes").EnumerateArray().ToList();
        Assert.Equal(new[] { 1, 2, 3 }, s2.Select(e => e.GetProperty("number").GetInt32()).ToArray());   // sorted by episode number
        Assert.True(s2[0].GetProperty("played").GetBoolean());
        Assert.Equal(JsonValueKind.Null, s2[0].GetProperty("progress").ValueKind);                       // finished: no progress bar
        Assert.Equal(0.4, s2[1].GetProperty("progress").GetDouble());                                   // partly watched
        Assert.False(s2[1].GetProperty("played").GetBoolean());
        Assert.Equal("2024-01-02", s2[0].GetProperty("airDate").GetString());
        var next = root.GetProperty("nextUp");
        Assert.Equal(2, next.GetProperty("seasonNumber").GetInt32());
        Assert.Equal(2, next.GetProperty("number").GetInt32());
        Assert.Equal(0.4, next.GetProperty("progress").GetDouble());
        Http.AssertCamelCase(root.GetRawText());
    }

    [Fact]
    public void NextUp_falls_back_to_the_partly_watched_episode_then_the_first_unwatched_one_and_is_null_when_all_is_watched()
    {
        var show = AddShow();
        var partial = new TitleData(null, new[] { Season(1, Ep(1, 1, played: true), Ep(1, 2, progress: 0.2), Ep(1, 3)) }, Array.Empty<PersonData>(), Array.Empty<TrailerData>());
        _source.Data[show.Id] = partial;
        Assert.Equal(2, Service().Get(Me, show.Id)!.NextUp!.Number);                      // no Jellyfin entry: resume the partial one

        _source.Data[show.Id] = new TitleData(null, new[] { Season(0, Ep(0, 1)), Season(1, Ep(1, 1, played: true), Ep(1, 2)) }, Array.Empty<PersonData>(), Array.Empty<TrailerData>());
        Assert.Equal(2, Service().Get(Me, show.Id)!.NextUp!.Number);                      // first unwatched regular episode

        _source.Data[show.Id] = new TitleData(null, new[] { Season(1, Ep(1, 1, played: true)), Season(0, Ep(0, 1)) }, Array.Empty<PersonData>(), Array.Empty<TrailerData>());
        Assert.Null(Service().Get(Me, show.Id)!.NextUp);                                  // specials never count; everything else is watched
    }

    [Fact]
    public async Task A_movie_has_no_seasons_and_no_next_up_but_has_people_and_trailers()
    {
        var movie = Make.Item("Movie", 11, genres: new[] { "Drama" });
        _catalog.Items.Add(movie);
        _source.Data[movie.Id] = new TitleData(
            null,
            new[] { Season(1, Ep(1, 1)) },   // a movie must never show episodes even if the source returned some
            new[]
            {
                new PersonData("Jane Doe", "Dr. Smith", "Actor", Guid.NewGuid(), true),
                new PersonData("Sam Writer", null, "Writer", null, true),
            },
            new[] { new TrailerData("abcdefghijk", "Official Trailer") });

        var root = await Json(Controller().Details(movie.Id));

        Assert.Empty(root.GetProperty("seasons").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("nextUp").ValueKind);
        var people = root.GetProperty("people").EnumerateArray().ToList();
        Assert.Equal("Dr. Smith", people[0].GetProperty("role").GetString());
        Assert.True(people[0].GetProperty("hasImage").GetBoolean());
        Assert.Equal(JsonValueKind.Null, people[1].GetProperty("id").ValueKind);
        Assert.False(people[1].GetProperty("hasImage").GetBoolean());                    // no id, so no image to ask for
        Assert.Equal("abcdefghijk", root.GetProperty("trailers")[0].GetProperty("key").GetString());
        Assert.Equal(movie.Id.ToString("N"), root.GetProperty("item").GetProperty("id").GetString());
    }

    [Fact]
    public void People_are_capped_at_twenty()
    {
        var movie = Make.Item("Movie", 11);
        _catalog.Items.Add(movie);
        _source.Data[movie.Id] = new TitleData(null, Array.Empty<SeasonData>(),
            Enumerable.Range(0, 40).Select(i => new PersonData("Actor " + i, null, "Actor", Guid.NewGuid(), false)).ToList(), Array.Empty<TrailerData>());

        Assert.Equal(20, Service().Get(Me, movie.Id)!.People.Count);
    }

    [Fact]
    public void A_title_the_user_cannot_see_and_an_unknown_title_are_404_and_the_source_is_never_asked()
    {
        var hidden = Make.Item("Hidden", 1);
        var shown = Make.Item("Shown", 2);
        _catalog.Items.AddRange(new[] { hidden, shown });
        _catalog.Visible = new HashSet<Guid> { shown.Id };
        _source.Data[hidden.Id] = new TitleData(null, Array.Empty<SeasonData>(), Array.Empty<PersonData>(), Array.Empty<TrailerData>());

        Assert.IsType<NotFoundResult>(Controller().Details(hidden.Id));
        Assert.IsType<NotFoundResult>(Controller().Details(Guid.NewGuid()));
        Assert.Equal(0, _source.Calls);
    }

    [Fact]
    public void A_title_Jellyfin_no_longer_returns_is_404()
    {
        var movie = Make.Item("Gone", 1);
        _catalog.Items.Add(movie);
        Assert.IsType<NotFoundResult>(Controller().Details(movie.Id));
    }

    [Fact]
    public void Without_a_signed_in_user_it_is_401()
    {
        Assert.IsType<UnauthorizedResult>(Controller(Guid.Empty).Details(Guid.NewGuid()));
    }

    [Fact]
    public async Task A_Jellyfin_failure_is_a_friendly_5xx_without_any_exception_text()
    {
        var show = AddShow();
        _source.Throw = new InvalidOperationException("secret library failure at C:\\media\\show");

        var (status, body, type) = await Http.Render(Controller().Details(show.Id));

        Assert.Equal(500, status);
        Assert.Equal("application/problem+json", type);
        Assert.DoesNotContain("secret", body);
        Assert.DoesNotContain("media", body);
        Assert.Contains("try again", body);
    }

    [Fact]
    public void A_broken_library_catalog_is_also_a_friendly_5xx()
    {
        var c = new ItemDetailsController(
            new ItemDetailsService(new HomeService(_ts.Store, new ThrowingCatalog(), NullLogger<HomeService>.Instance, _config), _source, _ts.Store, _config, NullLogger<ItemDetailsService>.Instance),
            NullLogger<ItemDetailsController>.Instance).As();

        var result = Assert.IsType<JsonResult>(c.Details(Guid.NewGuid()));
        Assert.Equal(500, result.StatusCode);
    }

    [Fact]
    public void Trailers_come_from_Jellyfin_first_then_the_cached_TMDB_keys_without_duplicates()
    {
        var show = new CatalogItem { Id = Guid.NewGuid(), Name = "S", Kind = CatalogKind.Series, TmdbId = 77, TrailerKey = "catalogKey1" };
        _catalog.Items.Add(show);
        _ts.Store.Write(d =>
        {
            d.TrailerKeys[$"item:{show.Id:N}"] = "itemKey123";
            d.TrailerKeys["tv:77"] = "tmdbKey123";
        });
        _source.Data[show.Id] = new TitleData(null, Array.Empty<SeasonData>(), Array.Empty<PersonData>(),
            new[] { new TrailerData("ownKey1234", "Season 1 Trailer"), new TrailerData("catalogKey1", null) });

        var keys = Service().Get(Me, show.Id)!.Trailers;

        Assert.Equal(new[] { "ownKey1234", "catalogKey1", "itemKey123", "tmdbKey123" }, keys.Select(t => t.Key).ToArray());
        Assert.Equal("Season 1 Trailer", keys[0].Name);
        Assert.Equal("Trailer", keys[1].Name);
    }

    [Fact]
    public void Without_a_TMDB_key_the_TMDB_cached_keys_are_ignored_and_nothing_hits_the_network()
    {
        _config.Current = new PluginConfiguration { TmdbApiKey = string.Empty };
        var movie = new CatalogItem { Id = Guid.NewGuid(), Name = "M", Kind = CatalogKind.Movie, TmdbId = 9 };
        _catalog.Items.Add(movie);
        _ts.Store.Write(d =>
        {
            d.TrailerKeys[$"item:{movie.Id:N}"] = "itemKey123";
            d.TrailerKeys["movie:9"] = "tmdbKey123";
        });
        _source.Data[movie.Id] = new TitleData(null, Array.Empty<SeasonData>(), Array.Empty<PersonData>(), new[] { new TrailerData("ownKey1234", null) });

        var result = Service().Get(Me, movie.Id)!;

        // The library's own cached key (shown on every card) stays; the TMDB-by-id key is not used without a TMDB key.
        Assert.Equal(new[] { "ownKey1234", "itemKey123" }, result.Trailers.Select(t => t.Key).ToArray());
    }

    [Fact]
    public void Trailers_are_capped()
    {
        var movie = Make.Item("M", 9);
        _catalog.Items.Add(movie);
        _source.Data[movie.Id] = new TitleData(null, Array.Empty<SeasonData>(), Array.Empty<PersonData>(),
            Enumerable.Range(0, 12).Select(i => new TrailerData("trailer" + i.ToString("D4"), null)).ToList());

        Assert.Equal(ItemDetailsService.MaxTrailers, Service().Get(Me, movie.Id)!.Trailers.Count);
    }

    // ---------------------------------------------------------------- More like this

    private static CatalogItem Sci(string name, int tmdb, float rating = 7.5f, Guid? collection = null, string[]? genres = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Kind = CatalogKind.Movie,
            TmdbId = tmdb,
            Rating = rating,
            Genres = genres ?? new[] { "Science Fiction", "Adventure" },
            DateAdded = new DateTime(2024, 1, 1),
            CollectionId = collection,
            CollectionName = collection is null ? null : "Saga",
        };

    [Fact]
    public void Similar_excludes_the_title_itself_thumbs_down_finished_dropped_and_invisible_titles()
    {
        var seed = Sci("Seed", 1);
        var good = Sci("Good", 2);
        var down = Sci("Thumbs Down", 3);
        var done = Sci("Finished", 4);
        var unseen = Sci("Not Visible", 5);
        var other = Sci("Unrelated", 6, genres: new[] { "Documentary" });
        var dropped = new CatalogItem
        {
            Id = Guid.NewGuid(), Name = "Dropped Show", Kind = CatalogKind.Series, TmdbId = 7, Genres = new[] { "Science Fiction", "Adventure" }, DateAdded = new DateTime(2024, 1, 1),
            Seasons = new[] { new SeasonInfo(1, 10, new DateTime(2024, 1, 1), new DateTime(2024, 1, 1)) },
        };
        _catalog.Items.AddRange(new[] { seed, good, down, done, unseen, other, dropped });
        _catalog.Visible = _catalog.Items.Select(i => i.Id).Where(i => i != unseen.Id).ToHashSet();
        var now = Clock.UtcNow;
        _ts.Store.Write(d =>
        {
            d.Ratings[StoreData.UserItemKey(Me, down.Id)] = -1;
            d.Signals.Add(new PlaySignal { UserId = Me, ItemId = done.Id, At = now.AddDays(-5), Completion = 1, Completed = true });
            d.Signals.Add(new PlaySignal { UserId = Me, ItemId = dropped.Id, IsEpisode = true, Season = 1, Episode = 1, At = now.AddDays(-80), Completion = 0.2 });
        });

        var names = Home().Similar(Me, seed.Id, 12).Select(c => c.Name).ToList();

        Assert.Contains("Good", names);
        Assert.DoesNotContain("Seed", names);
        Assert.DoesNotContain("Thumbs Down", names);
        Assert.DoesNotContain("Finished", names);
        Assert.DoesNotContain("Not Visible", names);
        Assert.DoesNotContain("Dropped Show", names);
        Assert.DoesNotContain("Unrelated", names);
    }

    [Fact]
    public void Similar_puts_collection_siblings_first_and_respects_the_count()
    {
        var saga = Guid.NewGuid();
        var seed = Sci("Part 1", 1, collection: saga, genres: new[] { "Drama" });
        var sibling = Sci("Part 2", 2, rating: 5f, collection: saga, genres: new[] { "Comedy" });   // nothing in common but the collection
        var similar = Enumerable.Range(0, 5).Select(i => Sci("Drama " + i, 10 + i, genres: new[] { "Drama" })).ToList();
        _catalog.Items.Add(seed);
        _catalog.Items.Add(sibling);
        _catalog.Items.AddRange(similar);

        var cards = Home().Similar(Me, seed.Id, 3);

        Assert.Equal(3, cards.Count);
        Assert.Equal("Part 2", cards[0].Name);
        Assert.Equal("Also in Saga", cards[0].Reason);
        Assert.All(cards, c => Assert.NotEqual(seed.Id.ToString("N"), c.Id));
    }

    [Fact]
    public void Similar_is_empty_for_a_title_the_user_cannot_see_and_for_a_zero_count()
    {
        var seed = Sci("Seed", 1);
        _catalog.Items.Add(seed);
        _catalog.Items.Add(Sci("Other", 2));
        _catalog.Visible = _catalog.Items.Where(i => i.Id != seed.Id).Select(i => i.Id).ToHashSet();

        Assert.Empty(Home().Similar(Me, seed.Id, 12));
        _catalog.Visible = null;
        Assert.Empty(Home().Similar(Me, seed.Id, 0));
        Assert.Empty(Home().Similar(Me, Guid.NewGuid(), 12));
    }

    [Fact]
    public void A_failing_similar_lookup_does_not_stop_the_page_from_opening()
    {
        // The catalog works for the card, then breaks while the source runs, so the Similar call (which reads the library again) fails.
        var movie = Make.Item("M", 9, genres: new[] { "Drama" });
        var flaky = new FlakyCatalog { Items = { movie } };
        _source.OnLoad = () => flaky.Broken = true;
        _source.Data[movie.Id] = new TitleData(null, Array.Empty<SeasonData>(), Array.Empty<PersonData>(), Array.Empty<TrailerData>());
        var svc = new ItemDetailsService(new HomeService(_ts.Store, flaky, NullLogger<HomeService>.Instance, _config), _source, _ts.Store, _config, NullLogger<ItemDetailsService>.Instance);

        var result = svc.Get(Me, movie.Id);

        Assert.NotNull(result);
        Assert.Empty(result!.Similar);
    }

    private sealed class FlakyCatalog : ICatalog
    {
        public List<CatalogItem> Items { get; } = new();

        public bool Broken { get; set; }

        public IReadOnlyList<CatalogItem> All => Items;

        public IReadOnlySet<Guid> VisibleTo(Guid userId)
            => Broken ? throw new InvalidOperationException("boom") : Items.Select(i => i.Id).ToHashSet();

        public void Invalidate()
        {
        }

        public event EventHandler? Changed { add { } remove { } }
    }
}
