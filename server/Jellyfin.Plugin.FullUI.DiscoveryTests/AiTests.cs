using System.Net;
using Jellyfin.Plugin.FullUI.Ai;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.FullUI.DiscoveryTests;

public class AiTests
{
    private static readonly CatalogItem[] Lib =
    {
        Make.Item("Blade Runner", 1, genres: new[] { "Science Fiction", "Thriller" }, overview: "A blade runner hunts replicants in a rainy future.", year: 1982),
        Make.Item("The Notebook", 2, genres: new[] { "Romance" }, overview: "A love story across decades.", year: 2004),
        Make.Item("Alien", 3, genres: new[] { "Horror", "Science Fiction" }, overview: "Crew meets a deadly creature.", year: 1979, tags: new[] { "space" }),
    };

    [Fact]
    public void Keyword_requires_all_words_and_ranks_name_first()
    {
        var hits = NlSearch.Keyword(Lib, "science fiction");
        Assert.Equal(2, hits.Count);
        Assert.DoesNotContain(hits, h => h.Item.Name == "The Notebook");
        Assert.Equal("Blade Runner", NlSearch.Keyword(Lib, "blade")[0].Item.Name);
        Assert.Equal("Alien", NlSearch.Keyword(Lib, "space")[0].Item.Name);
        Assert.Equal("The Notebook", NlSearch.Keyword(Lib, "2004")[0].Item.Name);
    }

    [Fact]
    public void Keyword_falls_back_to_any_word_and_handles_empty()
    {
        var hits = NlSearch.Keyword(Lib, "romance zombie");
        Assert.Single(hits);
        Assert.Empty(NlSearch.Keyword(Lib, "   "));
        Assert.Empty(NlSearch.Keyword(Lib, "zzzz"));
    }

    private sealed class FakeOllama : IOllamaClient
    {
        public bool Enabled { get; set; } = true;

        public Func<IReadOnlyList<string>, IReadOnlyList<float[]>?> Embed { get; set; } = t => t.Select(_ => new float[] { 1, 0 }).ToList();

        public string? ChatReply { get; set; }

        public Task<IReadOnlyList<float[]>?> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct) => Task.FromResult(Embed(texts));

        public Task<string?> ChatAsync(string system, string user, CancellationToken ct) => Task.FromResult(ChatReply);

        public Task<(bool ok, string message)> TestAsync(string? url, string? e, string? c, CancellationToken ct) => Task.FromResult((true, "ok"));
    }

    [Fact]
    public async Task Search_uses_semantic_ranking_filtered_to_visible_and_falls_back_when_ollama_down()
    {
        using var ts = new TempStore();
        var cat = new FakeCatalog { Items = Lib.ToList() };
        cat.Visible = new HashSet<Guid> { Lib[0].Id, Lib[1].Id }; // Alien hidden
        ts.Store.WriteEmbeddings(e =>
        {
            e[Lib[0].Id.ToString("N")] = new EmbeddingEntry { Vector = new float[] { 1, 0 } };
            e[Lib[1].Id.ToString("N")] = new EmbeddingEntry { Vector = new float[] { 0, 1 } };
            e[Lib[2].Id.ToString("N")] = new EmbeddingEntry { Vector = new float[] { 1, 0 } };
        });
        var ollama = new FakeOllama();
        var search = new NlSearch(ts.Store, cat, ollama);
        var (mode, hits) = await search.SearchAsync(Guid.NewGuid(), "rainy future", default);
        Assert.Equal("semantic", mode);
        Assert.Equal(new[] { "Blade Runner" }, hits.Select(h => h.Item.Name).ToArray());

        ollama.Embed = _ => null; // Ollama unreachable
        var (mode2, hits2) = await search.SearchAsync(Guid.NewGuid(), "romance", default);
        Assert.Equal("keyword", mode2);
        Assert.Equal("The Notebook", hits2[0].Item.Name);

        ollama.Enabled = false;
        Assert.Equal("keyword", (await search.SearchAsync(Guid.NewGuid(), "blade", default)).mode);
    }

    [Theory]
    [InlineData("Twisty Sci-Fi Mysteries", "Twisty Sci-Fi Mysteries")]
    [InlineData("\"Cozy Rainy Day Romances.\"", "Cozy Rainy Day Romances")]
    [InlineData("Dark Tales\nExtra explanation line", "Dark Tales")]
    [InlineData("Hi", null)]
    [InlineData("This title is far far too long to ever be accepted by us", null)]
    [InlineData("<script>alert(1)</script>", null)]
    [InlineData("", null)]
    [InlineData("Single", null)]
    public void RowTitle_validation(string raw, string? expected) => Assert.Equal(expected, RowTitleGenerator.Validate(raw));

    [Fact]
    public async Task Indexer_embeds_only_missing_items_and_stops_quietly_when_ollama_fails()
    {
        using var ts = new TempStore();
        var cat = new FakeCatalog { Items = Lib.ToList() };
        ts.Store.WriteEmbeddings(e => e[Lib[0].Id.ToString("N")] = new EmbeddingEntry { Vector = new float[] { 9, 9 } });
        var ollama = new FakeOllama();
        var seen = new List<string>();
        ollama.Embed = t =>
        {
            seen.AddRange(t);
            return t.Select(_ => new float[] { 1 }).ToList();
        };
        var n = await new EmbeddingIndexer(ts.Store, cat, ollama).RunAsync(null, default);
        Assert.Equal(2, n);
        Assert.Equal(2, seen.Count);
        Assert.DoesNotContain(seen, s => s.StartsWith("Blade Runner"));

        ts.Store.WriteEmbeddings(e => e.Remove(Lib[1].Id.ToString("N")));
        ollama.Embed = _ => null;
        Assert.Equal(0, await new EmbeddingIndexer(ts.Store, cat, ollama).RunAsync(null, default));
    }

    [Fact]
    public async Task RowTitleGenerator_stores_valid_titles_and_ignores_garbage()
    {
        using var ts = new TempStore();
        var cat = new FakeCatalog { Items = Lib.ToList() };
        var ollama = new FakeOllama { ChatReply = "Mind-Bending Sci-Fi Journeys" };
        var gen = new RowTitleGenerator(ts.Store, cat, ollama);
        Assert.True(await gen.RunAsync(default) > 0);
        Assert.Contains("Mind-Bending Sci-Fi Journeys", ts.Store.Read(d => d.RowTitles.Values.ToList()));

        using var ts2 = new TempStore();
        ollama.ChatReply = null;
        Assert.Equal(0, await new RowTitleGenerator(ts2.Store, cat, ollama).RunAsync(default));
        Assert.Empty(ts2.Store.Read(d => d.RowTitles.ToList()));
    }

    [Fact]
    public async Task OllamaClient_failures_return_null_and_never_throw()
    {
        var h = new FakeHandler { Respond = _ => throw new HttpRequestException("refused") };
        var cfg = new FakeConfig { Current = new() { OllamaEnabled = true, OllamaUrl = "http://localhost:11434" } };
        var c = new OllamaClient(new FakeFactory(h), cfg, NullLogger<OllamaClient>.Instance);
        Assert.Null(await c.EmbedAsync(new[] { "x" }, default));
        Assert.Null(await c.ChatAsync("s", "u", default));
        var (ok, msg) = await c.TestAsync(null, null, null, default);
        Assert.False(ok);
        Assert.DoesNotContain("refused", msg);

        var bad = new OllamaClient(new FakeFactory(h), new FakeConfig { Current = new() { OllamaEnabled = true, OllamaUrl = "file:///etc" } }, NullLogger<OllamaClient>.Instance);
        Assert.False(bad.Enabled);
    }

    [Fact]
    public async Task OllamaClient_parses_embed_response()
    {
        var h = new FakeHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"embeddings\":[[0.5,0.5],[1,0]]}") } };
        var cfg = new FakeConfig { Current = new() { OllamaEnabled = true } };
        var res = await new OllamaClient(new FakeFactory(h), cfg, NullLogger<OllamaClient>.Instance).EmbedAsync(new[] { "a", "b" }, default);
        Assert.Equal(2, res!.Count);
        Assert.Equal(0.5f, res[0][0]);
    }
}
