using System.Net;
using System.Reflection;
using Jellyfin.Plugin.FullUI.Ai;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Discovery;
using Jellyfin.Plugin.FullUI.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.FullUI.DiscoveryTests;

public class OllamaCircuitTests
{
    private static OllamaClient Client(FakeHandler h) => new(
        new FakeFactory(h),
        new FakeConfig { Current = new() { OllamaEnabled = true, OllamaUrl = "http://localhost:11434", OllamaEmbedModel = "m1" } },
        NullLogger<OllamaClient>.Instance);

    [Fact]
    public async Task AFailedSearchEmbedding_MakesLaterSearchesSkipOllamaForAWhile()
    {
        // B-17c: every search used to wait for the full timeout when Ollama was down.
        var h = new FakeHandler { Respond = _ => throw new HttpRequestException("refused") };
        var c = Client(h);

        Assert.Null(await c.EmbedQueryAsync("anything", default));
        var afterFirst = h.Requests.Count;
        Assert.True(afterFirst >= 1);
        Assert.True(c.CircuitOpen);

        Assert.Null(await c.EmbedQueryAsync("again", default));
        Assert.Equal(afterFirst, h.Requests.Count); // no network call at all
    }

    [Fact]
    public async Task TheCircuitCloses_OnceOllamaAnswersAgain_AndTheTestButtonAlwaysTries()
    {
        var fail = true;
        var h = new FakeHandler
        {
            Respond = _ => fail
                ? throw new HttpRequestException("refused")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"embeddings\":[[1,0]],\"message\":{\"content\":\"ok\"}}") },
        };
        var c = Client(h);
        Assert.Null(await c.EmbedQueryAsync("x", default));
        Assert.True(c.CircuitOpen);

        fail = false;
        var before = h.Requests.Count;
        var (ok, _) = await c.TestAsync(null, null, null, default); // admin pressed Test: never short-circuited
        Assert.True(h.Requests.Count > before);
        Assert.True(ok);
    }

    [Fact]
    public async Task ChatFailures_DoNotBlockSearch()
    {
        var h = new FakeHandler { Respond = r => r.RequestUri!.AbsolutePath.EndsWith("chat", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"embeddings\":[[1,0]]}") } };
        var c = Client(h);

        Assert.Null(await c.ChatAsync("s", "u", default));
        Assert.NotNull(await c.EmbedQueryAsync("x", default));
    }

    [Fact]
    public void EmbedModel_IsExposed_SoVectorsCanBeTaggedWithIt()
        => Assert.Equal("m1", Client(new FakeHandler()).EmbedModel);
}

public class TmdbFailureTests
{
    private static TmdbClient Client(FakeHandler h) => new(
        new FakeFactory(h),
        new FakeConfig { Current = new() { TmdbApiKey = "abc" } },
        NullLogger<TmdbClient>.Instance,
        TimeSpan.Zero,
        (_, _) => Task.CompletedTask);

    [Fact]
    public async Task LookupTrailer_SeparatesFailedFromGenuinelyNone()
    {
        var down = new FakeHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) };
        Assert.True((await Client(down).LookupTrailerAsync("movie", 1, default)).Failed);

        var network = new FakeHandler { Respond = _ => throw new HttpRequestException("offline") };
        Assert.True((await Client(network).LookupTrailerAsync("movie", 1, default)).Failed);

        var gone = new FakeHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound) };
        var none = await Client(gone).LookupTrailerAsync("movie", 1, default);
        Assert.False(none.Failed);
        Assert.Null(none.Key);

        var empty = new FakeHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"results\":[]}") } };
        var noClips = await Client(empty).LookupTrailerAsync("movie", 1, default);
        Assert.False(noClips.Failed);
        Assert.Null(noClips.Key);

        var good = new FakeHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"results\":[{\"site\":\"YouTube\",\"type\":\"Trailer\",\"key\":\"abcdef12345\",\"official\":true}]}") } };
        Assert.Equal("abcdef12345", (await Client(good).LookupTrailerAsync("movie", 1, default)).Key);
    }

    [Fact]
    public async Task AfterSeveralFailuresInARow_TmdbIsLeftAloneForAWhile()
    {
        var h = new FakeHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.BadGateway) };
        var c = Client(h);
        for (var i = 1; i <= 5; i++)
        {
            Assert.Empty(await c.RecommendationsAsync("movie", i, default));
        }

        var sent = h.Requests.Count;
        Assert.Empty(await c.RecommendationsAsync("movie", 99, default));
        Assert.Equal(sent, h.Requests.Count);
        Assert.True((await c.LookupTrailerAsync("movie", 100, default)).Failed);
    }

    [Fact]
    public async Task ResponseCache_IsBounded()
    {
        var h = new FakeHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"results\":[]}") } };
        var c = Client(h);
        for (var i = 0; i < 450; i++)
        {
            await c.SimilarAsync("movie", i, default);
        }

        var cache = typeof(TmdbClient).GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(c)!;
        var count = (int)cache.GetType().GetProperty("Count")!.GetValue(cache)!;
        Assert.InRange(count, 1, 400);
    }

    [Fact]
    public async Task CachedAnswers_AreServedWithoutAskingAgain()
    {
        var h = new FakeHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"results\":[{\"id\":3,\"title\":\"X\"}]}") } };
        var c = Client(h);

        Assert.Single(await c.SimilarAsync("movie", 1, default));
        Assert.Single(await c.SimilarAsync("movie", 1, default));
        Assert.Single(h.Requests);
    }
}

public class TrailerLookupTests : IDisposable
{
    private readonly TempStore _ts = new();

    public void Dispose() => _ts.Dispose();

    private sealed class ScriptedTmdb : ITmdbClient
    {
        public Func<TrailerLookup> Next { get; set; } = () => new TrailerLookup(null, false);

        public int Lookups;

        public bool Configured => true;

        public Task<IReadOnlyList<TmdbTitle>> RecommendationsAsync(string m, int id, CancellationToken ct) => Task.FromResult<IReadOnlyList<TmdbTitle>>(Array.Empty<TmdbTitle>());

        public Task<IReadOnlyList<TmdbTitle>> SimilarAsync(string m, int id, CancellationToken ct) => Task.FromResult<IReadOnlyList<TmdbTitle>>(Array.Empty<TmdbTitle>());

        public Task<IReadOnlyList<TmdbTitle>> UpcomingAsync(string m, CancellationToken ct) => Task.FromResult<IReadOnlyList<TmdbTitle>>(Array.Empty<TmdbTitle>());

        public Task<string?> TrailerKeyAsync(string m, int id, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<TmdbGenre>> GenresAsync(string m, CancellationToken ct) => Task.FromResult<IReadOnlyList<TmdbGenre>>(Array.Empty<TmdbGenre>());

        public Task<TrailerLookup> LookupTrailerAsync(string m, int id, CancellationToken ct)
        {
            Interlocked.Increment(ref Lookups);
            return Task.FromResult(Next());
        }
    }

    private ComingSoonService Service(ScriptedTmdb tmdb, FakeCatalog cat)
        => new(_ts.Store, cat, tmdb, new FakeUsers(), NullLogger<ComingSoonService>.Instance);

    private static FakeCatalog Library(int count)
    {
        var cat = new FakeCatalog();
        for (var i = 0; i < count; i++)
        {
            cat.Items.Add(Make.Item("T" + Guid.NewGuid().ToString("N"), 8_000_000 + i));
        }

        return cat;
    }

    [Fact]
    public async Task AFailedLookup_IsNotRemembered_AsNoTrailer_AndIsRetriedNextRun()
    {
        // B-18: one TMDB outage used to mark up to 200 titles as "no trailer" until the process restarted.
        var cat = Library(3);
        var tmdb = new ScriptedTmdb { Next = () => new TrailerLookup(null, true) };
        var svc = Service(tmdb, cat);

        await svc.RunAsync(null, default);
        Assert.Empty(_ts.Store.Read(d => d.TrailerKeys.ToList()));

        tmdb.Next = () => new TrailerLookup("goodkey1234", false);
        await svc.RunAsync(null, default);
        Assert.Equal(3, _ts.Store.Read(d => d.TrailerKeys.Count));
    }

    [Fact]
    public async Task ManyFailuresInARow_StopTheRun_InsteadOfHammeringTmdb()
    {
        var cat = Library(40);
        var tmdb = new ScriptedTmdb { Next = () => new TrailerLookup(null, true) };

        await Service(tmdb, cat).RunAsync(null, default);

        Assert.InRange(tmdb.Lookups, 1, 6);
    }

    [Fact]
    public async Task AGenuineNoTrailer_IsRemembered_SoItDoesNotUseUpEveryRunsQuota()
    {
        var cat = Library(4);
        var tmdb = new ScriptedTmdb { Next = () => new TrailerLookup(null, false) };
        var svc = Service(tmdb, cat);

        await svc.RunAsync(null, default);
        var first = tmdb.Lookups;
        await svc.RunAsync(null, default);

        Assert.Equal(4, first);
        Assert.Equal(first, tmdb.Lookups);
        Assert.True(ComingSoonService.NoTrailerTtl > TimeSpan.FromHours(1) && ComingSoonService.NoTrailerTtl < TimeSpan.FromDays(30));
    }
}

public class EmbeddingMaintenanceTests : IDisposable
{
    private readonly TempStore _ts = new();

    public void Dispose() => _ts.Dispose();

    private sealed class ModelOllama : IOllamaClient
    {
        public string EmbedModel { get; set; } = "model-a";

        public bool Enabled => true;

        public int Embedded;

        public Task<IReadOnlyList<float[]>?> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            Interlocked.Add(ref Embedded, texts.Count);
            return Task.FromResult<IReadOnlyList<float[]>?>(texts.Select(_ => new float[] { 1, 0 }).ToList());
        }

        public Task<string?> ChatAsync(string system, string user, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<(bool ok, string message)> TestAsync(string? url, string? e, string? c, CancellationToken ct) => Task.FromResult((true, "ok"));
    }

    private static CatalogItem Item(string name, string overview) => Make.Item(name, 1, overview: overview, genres: new[] { "Drama" });

    [Fact]
    public async Task ChangingTheEmbeddingModel_ReEmbedsEverything_ButNotWhenNothingChanged()
    {
        var cat = new FakeCatalog { Items = { Item("A", "x"), Item("B", "y"), Item("C", "z") } };
        var ollama = new ModelOllama();
        var indexer = new EmbeddingIndexer(_ts.Store, cat, ollama);

        Assert.Equal(3, await indexer.RunAsync(null, default));
        Assert.Equal(0, await indexer.RunAsync(null, default));

        ollama.EmbedModel = "model-b"; // B-17b: old-dimension vectors used to stay forever (cosine 0)
        Assert.Equal(3, await indexer.RunAsync(null, default));
        Assert.All(_ts.Store.ReadEmbeddings(e => e.Values.ToList()), v => Assert.Equal("model-b", v.Model));
    }

    [Fact]
    public async Task EditingATitlesText_ReEmbedsOnlyThatTitle()
    {
        var a = Item("A", "x");
        var cat = new FakeCatalog { Items = { a, Item("B", "y") } };
        var indexer = new EmbeddingIndexer(_ts.Store, cat, new ModelOllama());
        await indexer.RunAsync(null, default);

        cat.Items[0] = new CatalogItem { Id = a.Id, Name = "A", Kind = a.Kind, TmdbId = 1, Overview = "a brand new description", Genres = a.Genres };

        Assert.Equal(1, await indexer.RunAsync(null, default));
    }

    [Fact]
    public async Task VectorsFromADifferentModel_AreIgnoredBySearch_AndNewTitlesAreStillFoundByName()
    {
        var old = Item("Old Title", "x");
        var fresh = Item("Brand New Arrival", "y");   // added after the last indexing run: no vector yet
        var cat = new FakeCatalog { Items = { old, fresh } };
        _ts.Store.WriteEmbeddings(e => e[old.Id.ToString("N")] = new EmbeddingEntry { Vector = new float[] { 1, 0 }, Model = "model-a" });
        var ollama = new ModelOllama { EmbedModel = "model-a" };

        var (mode, hits) = await new NlSearch(_ts.Store, cat, ollama).SearchAsync(Guid.NewGuid(), "old title brand", default);
        // (a) semantic results come first, and (b) the not-yet-indexed title is still reachable by its words
        Assert.Equal("semantic", mode);
        Assert.Equal("Old Title", hits[0].Item.Name);
        var (_, byName) = await new NlSearch(_ts.Store, cat, ollama).SearchAsync(Guid.NewGuid(), "brand new arrival", default);
        Assert.Contains(byName, h => h.Item.Name == "Brand New Arrival");

        ollama.EmbedModel = "model-b";
        var (mode2, hits2) = await new NlSearch(_ts.Store, cat, ollama).SearchAsync(Guid.NewGuid(), "old title", default);
        Assert.Equal("keyword", mode2);      // nothing usable from model-a: honest keyword fallback
        Assert.Equal("Old Title", hits2[0].Item.Name);
    }
}

public class SeedPerformanceTests
{
    [Fact]
    public void SelectSeeds_IsLinear_NotTitlesTimesSignals()
    {
        // B-21: this used to loop all signals for every title while holding the global store lock.
        var u = Guid.NewGuid();
        var items = Enumerable.Range(0, 5000).Select(i => Make.Item("T" + i, 1000 + i)).ToList();
        var data = new StoreData();
        foreach (var item in items)
        {
            for (var k = 0; k < 4; k++)
            {
                data.Signals.Add(new PlaySignal { UserId = u, ItemId = item.Id, At = DateTime.UtcNow.AddDays(-k), Completion = 1, Completed = k == 0 });
            }
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var seeds = ComingSoonRanker.SelectSeeds(data, u, items, DateTime.UtcNow);
        sw.Stop();

        Assert.Equal(ComingSoonRanker.MaxSeeds, seeds.Count);
        Assert.True(sw.ElapsedMilliseconds < 1500, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void ImageTag_ChangesWithTheImage_AndIsNullWithoutOne()
    {
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var a = JellyfinCatalog.ImageTag("/m/a/poster.jpg", t);
        Assert.Equal(16, a!.Length);
        Assert.Equal(a, JellyfinCatalog.ImageTag("/m/a/poster.jpg", t));
        Assert.NotEqual(a, JellyfinCatalog.ImageTag("/m/a/poster.jpg", t.AddSeconds(1)));
        Assert.NotEqual(a, JellyfinCatalog.ImageTag("/m/b/poster.jpg", t));
        Assert.Null(JellyfinCatalog.ImageTag(null, t));
        Assert.Null(JellyfinCatalog.ImageTag("", t));
    }
}
