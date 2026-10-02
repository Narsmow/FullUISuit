using System.Net;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Discovery;
using Jellyfin.Plugin.FullUI.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.FullUI.DiscoveryTests;

public class FakeHandler : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = new();

    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(Respond(request));
    }
}

public sealed class FakeFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _h;

    public FakeFactory(HttpMessageHandler h) => _h = h;

    public HttpClient CreateClient(string name) => new(_h, disposeHandler: false);
}

public class TmdbTests
{
    private static TmdbClient Client(FakeHandler h, string key = "abc") => new(
        new FakeFactory(h),
        new FakeConfig { Current = new() { TmdbApiKey = key } },
        NullLogger<TmdbClient>.Instance,
        TimeSpan.Zero,
        (_, _) => Task.CompletedTask);

    private static TmdbTitle T(int id, string type = "movie", string? poster = "/p.jpg", double va = 7, string? date = "2026-12-01", params int[] genres)
        => new(id, type, "T" + id, null, poster, null, date, va, genres);

    [Fact]
    public void Ranker_excludes_library_downvoted_and_posterless()
    {
        var cands = new[]
        {
            new ComingSoonRanker.Candidate(T(1)) { SeedHits = 2 },
            new ComingSoonRanker.Candidate(T(2)) { SeedHits = 2 },
            new ComingSoonRanker.Candidate(T(3, poster: null)) { SeedHits = 5 },
            new ComingSoonRanker.Candidate(T(4)) { SeedHits = 1 },
            new ComingSoonRanker.Candidate(T(1, "tv")) { SeedHits = 1 },
        };
        var res = ComingSoonRanker.Rank(cands, new HashSet<string> { "movie:1" }, new HashSet<string> { "movie:2" }, new HashSet<int>(), DateTime.UtcNow);
        Assert.Equal(new[] { "movie:4", "tv:1" }, res.Select(r => r.MediaType + ":" + r.TmdbId).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Ranker_scores_by_seed_hits_then_rating_and_keeps_top_n()
    {
        var now = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var cands = Enumerable.Range(1, 40).Select(i => new ComingSoonRanker.Candidate(T(i, va: 5, date: "2020-01-01")) { SeedHits = 1 }).ToList();
        cands.Add(new ComingSoonRanker.Candidate(T(100, va: 5, date: "2020-01-01")) { SeedHits = 4 });
        cands.Add(new ComingSoonRanker.Candidate(T(101, va: 9, date: "2020-01-01")) { SeedHits = 1 });
        var res = ComingSoonRanker.Rank(cands, new HashSet<string>(), new HashSet<string>(), new HashSet<int>(), now, 30);
        Assert.Equal(30, res.Count);
        Assert.Equal(100, res[0].TmdbId);
        Assert.Equal(101, res[1].TmdbId);
    }

    [Fact]
    public void Recency_prefers_soon_over_old()
    {
        var now = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(ComingSoonRanker.RecencyBonus("2026-07-01", now) > ComingSoonRanker.RecencyBonus("2019-01-01", now));
        Assert.Equal(0, ComingSoonRanker.RecencyBonus(null, now));
    }

    [Fact]
    public void Seeds_use_love_like_completed_and_skip_downvoted_or_untmdb()
    {
        var u = Guid.NewGuid();
        var a = Make.Item("A", 10);
        var b = Make.Item("B", 11);
        var c = Make.Item("C", 12);
        var d = Make.Item("D", null);
        var e = Make.Item("E", 14);
        var data = new StoreData();
        data.Ratings[StoreData.UserItemKey(u, a.Id)] = 2;
        data.Ratings[StoreData.UserItemKey(u, b.Id)] = -1;
        data.Ratings[StoreData.UserItemKey(u, d.Id)] = 2;
        data.Signals.Add(new PlaySignal { UserId = u, ItemId = c.Id, At = DateTime.UtcNow, Completed = true, Completion = 1 });
        var seeds = ComingSoonRanker.SelectSeeds(data, u, new[] { a, b, c, d, e }, DateTime.UtcNow);
        Assert.Equal(new[] { a.Id, c.Id }.OrderBy(x => x).ToArray(), seeds.Select(s => s.Item.Id).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Genre_mapping_handles_sci_fi_and_ampersand()
    {
        var ids = ComingSoonRanker.MapGenreIds(new[] { "Sci-Fi", "Action" }, new[] { new TmdbGenre(878, "Science Fiction"), new TmdbGenre(10759, "Action & Adventure"), new TmdbGenre(35, "Comedy") });
        Assert.Contains(878, ids);
        Assert.Contains(10759, ids);
        Assert.DoesNotContain(35, ids);
    }

    [Fact]
    public void PickTrailer_prefers_official_trailer_over_teaser()
    {
        var k = TmdbClient.PickTrailer(new[]
        {
            new TmdbVideo("YouTube", "Teaser", "tease", true, "en"),
            new TmdbVideo("Vimeo", "Trailer", "vim", true, "en"),
            new TmdbVideo("YouTube", "Trailer", "fan", false, "en"),
            new TmdbVideo("YouTube", "Trailer", "off", true, "en"),
        });
        Assert.Equal("off", k);
        Assert.Null(TmdbClient.PickTrailer(new[] { new TmdbVideo("YouTube", "Clip", "x", true, "en") }));
    }

    [Fact]
    public async Task V3_key_goes_in_query_and_v4_token_in_bearer_header()
    {
        var h = new FakeHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"results\":[{\"id\":5,\"title\":\"X\",\"poster_path\":\"/x.jpg\",\"vote_average\":8,\"genre_ids\":[1],\"release_date\":\"2026-01-01\"}]}") } };
        var res = await Client(h, "abc123").RecommendationsAsync("movie", 1, default);
        Assert.Single(res);
        Assert.Equal("X", res[0].Title);
        Assert.Contains("api_key=abc123", h.Requests[0].RequestUri!.Query);
        Assert.Null(h.Requests[0].Headers.Authorization);

        var h2 = new FakeHandler();
        await Client(h2, "eyJtoken").SimilarAsync("tv", 1, default);
        Assert.DoesNotContain("api_key", h2.Requests[0].RequestUri!.Query);
        Assert.Equal("Bearer", h2.Requests[0].Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task Retries_on_429_then_succeeds()
    {
        var n = 0;
        var h = new FakeHandler
        {
            Respond = _ => ++n < 3
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"results\":[{\"id\":1,\"title\":\"A\"}]}") },
        };
        var res = await Client(h).RecommendationsAsync("movie", 9, default);
        Assert.Single(res);
        Assert.Equal(3, h.Requests.Count);
    }

    [Fact]
    public async Task Bad_key_malformed_json_and_network_errors_degrade_to_empty()
    {
        var unauthorized = new FakeHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) };
        var c = Client(unauthorized);
        Assert.Empty(await c.RecommendationsAsync("movie", 1, default));
        Assert.True(c.KeyRejected);

        var junk = new FakeHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>not json") } };
        Assert.Empty(await Client(junk).UpcomingAsync("movie", default));

        var boom = new FakeHandler { Respond = _ => throw new HttpRequestException("down") };
        Assert.Empty(await Client(boom).RecommendationsAsync("movie", 1, default));
        Assert.Null(await Client(boom).TrailerKeyAsync("movie", 1, default));

        var noKey = new FakeHandler();
        var nk = Client(noKey, "");
        Assert.False(nk.Configured);
        Assert.Empty(await nk.RecommendationsAsync("movie", 1, default));
        Assert.Empty(noKey.Requests);
    }

    private sealed class ThrowingTmdb : ITmdbClient
    {
        public bool Configured => true;

        public Task<IReadOnlyList<TmdbTitle>> RecommendationsAsync(string m, int id, CancellationToken ct) => throw new InvalidOperationException("boom");

        public Task<IReadOnlyList<TmdbTitle>> SimilarAsync(string m, int id, CancellationToken ct) => throw new InvalidOperationException("boom");

        public Task<IReadOnlyList<TmdbTitle>> UpcomingAsync(string m, CancellationToken ct) => throw new InvalidOperationException("boom");

        public Task<string?> TrailerKeyAsync(string m, int id, CancellationToken ct) => throw new InvalidOperationException("boom");

        public Task<IReadOnlyList<TmdbGenre>> GenresAsync(string m, CancellationToken ct) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task ComingSoonService_never_throws_and_keeps_previous_list_when_tmdb_fails()
    {
        using var ts = new TempStore();
        var u = Guid.NewGuid();
        var users = new FakeUsers();
        users.Names[u] = "ann";
        var liked = Make.Item("Liked", 10);
        var cat = new FakeCatalog { Items = { liked } };
        ts.Store.Write(d =>
        {
            d.Ratings[StoreData.UserItemKey(u, liked.Id)] = 2;
            d.ComingSoon[u.ToString("N")] = new() { new ComingSoonEntry { TmdbId = 7, Title = "Old" } };
        });
        var svc = new ComingSoonService(ts.Store, cat, new ThrowingTmdb(), users, NullLogger<ComingSoonService>.Instance);
        await svc.RunAsync(null, default);
        Assert.Equal("Old", ts.Store.Read(d => d.ComingSoon[u.ToString("N")][0].Title));
    }

    private sealed class FakeTmdb : ITmdbClient
    {
        public bool Configured => true;

        public Task<IReadOnlyList<TmdbTitle>> RecommendationsAsync(string m, int id, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TmdbTitle>>(new[] { T(500), T(501), T(id) });

        public Task<IReadOnlyList<TmdbTitle>> SimilarAsync(string m, int id, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TmdbTitle>>(new[] { T(500) });

        public Task<IReadOnlyList<TmdbTitle>> UpcomingAsync(string m, CancellationToken ct) => Task.FromResult<IReadOnlyList<TmdbTitle>>(Array.Empty<TmdbTitle>());

        public Task<string?> TrailerKeyAsync(string m, int id, CancellationToken ct) => Task.FromResult<string?>("yt" + id);

        public Task<IReadOnlyList<TmdbGenre>> GenresAsync(string m, CancellationToken ct) => Task.FromResult<IReadOnlyList<TmdbGenre>>(Array.Empty<TmdbGenre>());
    }

    [Fact]
    public async Task ComingSoonService_writes_ranked_entries_with_trailers_and_excludes_library()
    {
        using var ts = new TempStore();
        var u = Guid.NewGuid();
        var users = new FakeUsers();
        users.Names[u] = "ann";
        var liked = Make.Item("Liked", 10);
        var owned = Make.Item("Owned", 501);
        var cat = new FakeCatalog { Items = { liked, owned } };
        ts.Store.Write(d => d.Ratings[StoreData.UserItemKey(u, liked.Id)] = 2);
        var svc = new ComingSoonService(ts.Store, cat, new FakeTmdb(), users, NullLogger<ComingSoonService>.Instance);
        await svc.RunAsync(null, default);
        var list = ts.Store.Read(d => d.ComingSoon[u.ToString("N")].ToList());
        Assert.Contains(list, e => e.TmdbId == 500 && e.TrailerKey == "yt500");
        Assert.DoesNotContain(list, e => e.TmdbId == 501);
        Assert.DoesNotContain(list, e => e.TmdbId == 10);
        Assert.True(ts.Store.Read(d => d.TrailerKeys.ContainsKey("item:" + owned.Id.ToString("N"))));
    }
}
