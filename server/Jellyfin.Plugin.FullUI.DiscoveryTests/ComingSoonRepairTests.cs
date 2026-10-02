using System.Text.Json;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Discovery;
using Jellyfin.Plugin.FullUI.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.FullUI.DiscoveryTests;

public class ComingSoonRepairTests
{
    private static readonly DateTime Now = new(2026, 10, 2, 14, 30, 0, DateTimeKind.Utc);

    private static TmdbTitle T(int id, string date, string type = "movie", string? poster = "/p.jpg", params int[] genres)
        => new(id, type, "T" + id, null, poster, null, date, 7, genres);

    private sealed class Tmdb : ITmdbClient
    {
        public bool Configured => true;

        public List<TmdbTitle> Recs { get; } = new();

        public List<TmdbTitle> Upcoming { get; } = new();

        public Dictionary<string, TmdbCertification?> Certs { get; } = new();

        public int CertCalls { get; private set; }

        public TmdbNewSeason? NextSeason { get; set; }

        public Task<IReadOnlyList<TmdbTitle>> RecommendationsAsync(string m, int id, CancellationToken ct) => Task.FromResult<IReadOnlyList<TmdbTitle>>(Recs.ToList());

        public Task<IReadOnlyList<TmdbTitle>> SimilarAsync(string m, int id, CancellationToken ct) => Task.FromResult<IReadOnlyList<TmdbTitle>>(Array.Empty<TmdbTitle>());

        public Task<IReadOnlyList<TmdbTitle>> UpcomingAsync(string m, CancellationToken ct) => Task.FromResult<IReadOnlyList<TmdbTitle>>(Upcoming.Where(t => t.MediaType == m).ToList());

        public Task<string?> TrailerKeyAsync(string m, int id, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<TmdbGenre>> GenresAsync(string m, CancellationToken ct) => Task.FromResult<IReadOnlyList<TmdbGenre>>(Array.Empty<TmdbGenre>());

        public Task<TmdbCertification?> CertificationAsync(string m, int id, CancellationToken ct)
        {
            CertCalls++;
            return Task.FromResult(Certs.GetValueOrDefault(m + ":" + id));
        }

        public Task<TmdbNewSeason?> NextSeasonAsync(int tvId, CancellationToken ct) => Task.FromResult(NextSeason);
    }

    private sealed class Users : IUserDirectory
    {
        public Guid Id { get; } = Guid.NewGuid();

        public int? Cap { get; set; }

        public IReadOnlyList<Guid> UserIds => new[] { Id };

        public string? NameOf(Guid userId) => "ann";

        public int? MaxParentalRatingScore(Guid userId) => Cap;
    }

    private sealed class Scorer : IRatingScorer
    {
        public int? Score(string rating, string country) => rating switch { "G" => 1, "PG" => 5, "PG-13" => 7, "R" => 9, "TV-MA" => 9, _ => null };
    }

    private static (ComingSoonService Svc, TempStore Ts, Users U, Tmdb Tmdb, CatalogItem Liked) Setup(int? cap = null, bool showRecommended = true, CatalogKind kind = CatalogKind.Movie)
    {
        var ts = new TempStore();
        var u = new Users { Cap = cap };
        var tmdb = new Tmdb();
        var liked = Make.Item("Liked", 10, kind);
        ts.Store.Write(d => d.Ratings[StoreData.UserItemKey(u.Id, liked.Id)] = 2);
        var cfg = new FakeConfig { Current = new() { TmdbApiKey = "k", ShowRecommendedNotInLibrary = showRecommended } };
        var svc = new ComingSoonService(ts.Store, new FakeCatalog { Items = { liked } }, tmdb, u, NullLogger<ComingSoonService>.Instance, new Scorer(), cfg);
        return (svc, ts, u, tmdb, liked);
    }

    private static List<ComingSoonEntry> Stored(TempStore ts, Users u) => ts.Store.Read(d => d.ComingSoon[u.Id.ToString("N")].ToList());

    [Fact]
    public async Task Released_titles_are_never_coming_soon_even_when_tmdb_lists_them_as_upcoming()
    {
        var (svc, ts, u, tmdb, _) = Setup();
        using var scope = ts;
        tmdb.Upcoming.Add(T(1, "2026-09-01"));           // on the air since September: NOT upcoming
        tmdb.Upcoming.Add(T(2, "2026-10-02"));           // releases today: still coming soon
        tmdb.Upcoming.Add(T(3, "2026-12-25"));
        tmdb.Upcoming.Add(T(4, "2026-12-25", "tv"));
        await svc.RunAsync(null, default, Now);

        var stored = Stored(ts, u);
        Assert.DoesNotContain(stored, e => e.TmdbId == 1);
        Assert.Contains(stored, e => e.TmdbId == 2 && e.Upcoming);
        Assert.Equal(new[] { 2, 3, 4 }, stored.Where(e => e.Upcoming).Select(e => e.TmdbId).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task Released_recommendations_go_to_the_separate_recommended_list_not_coming_soon()
    {
        var (svc, ts, u, tmdb, _) = Setup();
        using var scope = ts;
        tmdb.Recs.Add(T(20, "2019-05-01"));
        tmdb.Recs.Add(T(21, "2027-01-15"));
        await svc.RunAsync(null, default, Now);

        var libraryKeys = new HashSet<string>();
        var coming = ts.Store.Read(d => ComingSoonView.Cards(d, u.Id, libraryKeys, Now, ComingSoonKind.Upcoming));
        var released = ts.Store.Read(d => ComingSoonView.Cards(d, u.Id, libraryKeys, Now, ComingSoonKind.Released));
        Assert.Equal(new[] { 21 }, coming.Select(c => c.TmdbId).ToArray());
        Assert.All(coming, c => Assert.True(c.Upcoming));
        Assert.Equal(new[] { 20 }, released.Select(c => c.TmdbId).ToArray());
        Assert.All(released, c => Assert.False(c.Upcoming));
        // The default call (used by the Home row) is Coming Soon only.
        Assert.DoesNotContain(ts.Store.Read(d => ComingSoonView.Cards(d, u.Id, libraryKeys)), c => c.TmdbId == 20);
    }

    [Fact]
    public async Task The_recommended_list_can_be_switched_off()
    {
        var (svc, ts, u, tmdb, _) = Setup(showRecommended: false);
        using var scope = ts;
        tmdb.Recs.Add(T(20, "2019-05-01"));
        await svc.RunAsync(null, default, Now);
        Assert.DoesNotContain(Stored(ts, u), e => e.TmdbId == 20);
    }

    [Fact]
    public void A_title_stored_as_upcoming_stops_being_coming_soon_once_released()
    {
        using var ts = new TempStore();
        var u = Guid.NewGuid();
        ts.Store.Write(d => d.ComingSoon[u.ToString("N")] = new() { new ComingSoonEntry { TmdbId = 5, Title = "Was soon", PosterPath = "/p", ReleaseDate = "2026-10-01", Upcoming = true } });
        var libraryKeys = new HashSet<string>();
        Assert.Empty(ts.Store.Read(d => ComingSoonView.Cards(d, u, libraryKeys, Now, ComingSoonKind.Upcoming)));
        Assert.Single(ts.Store.Read(d => ComingSoonView.Cards(d, u, libraryKeys, Now, ComingSoonKind.Released)));
    }

    [Fact]
    public void Entries_without_a_release_date_are_not_coming_soon()
    {
        using var ts = new TempStore();
        var u = Guid.NewGuid();
        ts.Store.Write(d => d.ComingSoon[u.ToString("N")] = new() { new ComingSoonEntry { TmdbId = 5, Title = "Dateless", ReleaseDate = null } });
        Assert.Empty(ts.Store.Read(d => ComingSoonView.Cards(d, u, new HashSet<string>(), Now, ComingSoonKind.Upcoming)));
    }

    [Fact]
    public void A_past_release_earns_no_ranking_bonus()
    {
        Assert.Equal(0, ComingSoonRanker.RecencyBonus("2026-09-30", Now));
        Assert.Equal(0, ComingSoonRanker.RecencyBonus("2020-01-01", Now));
        Assert.True(ComingSoonRanker.RecencyBonus("2026-10-02", Now) > 0, "today is still upcoming");
        Assert.True(ComingSoonRanker.IsUpcoming("2026-10-02", Now));
        Assert.False(ComingSoonRanker.IsUpcoming("2026-10-01", Now));
        Assert.False(ComingSoonRanker.IsUpcoming(null, Now));
        Assert.False(ComingSoonRanker.IsUpcoming("not a date", Now));
    }

    [Fact]
    public async Task Restricted_user_sees_only_titles_whose_tmdb_rating_is_within_their_cap()
    {
        var (svc, ts, u, tmdb, _) = Setup(cap: 5);
        using var scope = ts;
        foreach (var id in new[] { 30, 31, 32, 33 })
        {
            tmdb.Upcoming.Add(T(id, "2026-12-25"));
        }

        tmdb.Certs["movie:30"] = new TmdbCertification("US", "G");
        tmdb.Certs["movie:31"] = new TmdbCertification("US", "R");
        tmdb.Certs["movie:32"] = null;                                       // no certification at all
        tmdb.Certs["movie:33"] = new TmdbCertification("US", "Weird-Rating"); // not recognised
        await svc.RunAsync(null, default, Now);

        Assert.Equal(new[] { 30 }, Stored(ts, u).Select(e => e.TmdbId).ToArray());
        Assert.Equal("US:G", Stored(ts, u)[0].Certification);
    }

    [Fact]
    public async Task Unrestricted_user_sees_everything_and_no_rating_lookups_are_made()
    {
        var (svc, ts, u, tmdb, _) = Setup(cap: null);
        using var scope = ts;
        tmdb.Upcoming.Add(T(30, "2026-12-25"));
        tmdb.Upcoming.Add(T(31, "2026-12-26"));
        tmdb.Certs["movie:31"] = new TmdbCertification("US", "R");
        await svc.RunAsync(null, default, Now);
        Assert.Equal(2, Stored(ts, u).Count);
        Assert.Equal(0, tmdb.CertCalls);
    }

    [Fact]
    public async Task Restricted_user_also_has_released_recommendations_filtered()
    {
        var (svc, ts, u, tmdb, _) = Setup(cap: 7);
        using var scope = ts;
        tmdb.Recs.Add(T(40, "2019-01-01"));
        tmdb.Recs.Add(T(41, "2019-01-01"));
        tmdb.Certs["movie:40"] = new TmdbCertification("US", "PG-13");
        tmdb.Certs["movie:41"] = new TmdbCertification("US", "R");
        await svc.RunAsync(null, default, Now);
        Assert.Equal(new[] { 40 }, Stored(ts, u).Select(e => e.TmdbId).ToArray());
    }

    [Fact]
    public void Parental_gate_decisions()
    {
        var s = new Scorer();
        Assert.True(ParentalGate.Allows(null, null, s));                                           // no cap: everything
        Assert.False(ParentalGate.Allows(5, null, s));                                             // capped, no rating: hidden
        Assert.False(ParentalGate.Allows(5, new TmdbCertification("US", "R"), s));
        Assert.True(ParentalGate.Allows(9, new TmdbCertification("US", "R"), s));
        Assert.False(ParentalGate.Allows(5, new TmdbCertification("US", "PG"), null));             // cannot score: hidden
        Assert.Equal("US:PG-13", ParentalGate.Format(new TmdbCertification("US", "PG-13")));
        Assert.Equal(new TmdbCertification("US", "PG-13"), ParentalGate.Parse("US:PG-13"));
        Assert.Null(ParentalGate.Parse("junk"));
    }

    [Fact]
    public async Task A_genuinely_new_season_of_a_show_in_the_library_is_coming_soon()
    {
        var (svc, ts, u, tmdb, show) = Setup(kind: CatalogKind.Series);
        using var scope = ts;
        tmdb.NextSeason = new TmdbNewSeason(T(10, "2026-01-01", "tv"), 3, "2026-11-20");
        await svc.RunAsync(null, default, Now);

        var e = Assert.Single(Stored(ts, u));
        Assert.Equal(10, e.TmdbId);
        Assert.Equal(3, e.SeasonNumber);
        Assert.Equal("T10: Season 3", e.Title);
        Assert.True(e.Upcoming);
        // ... although the show itself is in the library.
        var inLibrary = ComingSoonView.LibraryKeys(new[] { show });
        Assert.Single(ts.Store.Read(d => ComingSoonView.Cards(d, u.Id, inLibrary, Now, ComingSoonKind.Upcoming)));
    }

    [Fact]
    public void Tmdb_json_parsing_of_certifications_and_new_seasons()
    {
        using var movie = JsonDocument.Parse("""
            {"results":[{"iso_3166_1":"DE","release_dates":[{"certification":"12","type":3}]},
                        {"iso_3166_1":"US","release_dates":[{"certification":"","type":1},{"certification":"R","type":4},{"certification":"PG-13","type":3}]}]}
            """);
        Assert.Equal(new TmdbCertification("US", "PG-13"), TmdbClient.ParseMovieCertification(movie, "US"));
        Assert.Equal(new TmdbCertification("DE", "12"), TmdbClient.ParseMovieCertification(movie, "de"));
        Assert.Equal(new TmdbCertification("US", "PG-13"), TmdbClient.ParseMovieCertification(movie, "FR")); // falls back to the US
        Assert.Null(TmdbClient.ParseMovieCertification(null, "US"));

        using var tv = JsonDocument.Parse("""{"results":[{"iso_3166_1":"US","rating":"TV-MA"},{"iso_3166_1":"GB","rating":""}]}""");
        Assert.Equal(new TmdbCertification("US", "TV-MA"), TmdbClient.ParseTvCertification(tv, "GB"));

        using var show = JsonDocument.Parse("""
            {"id":7,"name":"Show","poster_path":"/p.jpg","vote_average":8.1,"genres":[{"id":18}],
             "next_episode_to_air":{"air_date":"2026-11-20","season_number":3,"episode_number":1}}
            """);
        var ns = TmdbClient.ParseNewSeason(show, new DateTime(2026, 10, 2));
        Assert.NotNull(ns);
        Assert.Equal(3, ns!.SeasonNumber);
        Assert.Equal("2026-11-20", ns.AirDate);

        using var midSeason = JsonDocument.Parse("""{"id":7,"name":"Show","next_episode_to_air":{"air_date":"2026-11-20","season_number":3,"episode_number":4}}""");
        Assert.Null(TmdbClient.ParseNewSeason(midSeason, new DateTime(2026, 10, 2)));
        using var past = JsonDocument.Parse("""{"id":7,"name":"Show","next_episode_to_air":{"air_date":"2026-09-20","season_number":3,"episode_number":1}}""");
        Assert.Null(TmdbClient.ParseNewSeason(past, new DateTime(2026, 10, 2)));
    }

    [Fact]
    public async Task Upcoming_uses_discover_with_a_date_floor_never_on_the_air_and_every_call_excludes_adult_titles()
    {
        var h = new FakeHandler { Respond = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"results\":[]}") } };
        var c = new TmdbClient(new FakeFactory(h), new FakeConfig { Current = new() { TmdbApiKey = "abc" } }, NullLogger<TmdbClient>.Instance, TimeSpan.Zero, (_, _) => Task.CompletedTask)
        {
            Today = () => new DateTime(2026, 10, 2),
        };
        await c.UpcomingAsync("movie", default);
        await c.UpcomingAsync("tv", default);
        await c.RecommendationsAsync("movie", 1, default);

        var urls = h.Requests.Select(r => Uri.UnescapeDataString(r.RequestUri!.ToString())).ToList();
        Assert.DoesNotContain(urls, x => x.Contains("on_the_air", StringComparison.Ordinal));
        Assert.DoesNotContain(urls, x => x.Contains("/movie/upcoming", StringComparison.Ordinal));
        Assert.Contains(urls, x => x.Contains("/discover/movie", StringComparison.Ordinal) && x.Contains("primary_release_date.gte=2026-10-02", StringComparison.Ordinal));
        Assert.Contains(urls, x => x.Contains("/discover/tv", StringComparison.Ordinal) && x.Contains("first_air_date.gte=2026-10-02", StringComparison.Ordinal));
        Assert.All(urls, x => Assert.Contains("include_adult=false", x, StringComparison.Ordinal));
    }
}
