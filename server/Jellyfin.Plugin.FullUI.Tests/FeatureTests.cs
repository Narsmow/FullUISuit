using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FullUI.Ai;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Library;
using Jellyfin.Plugin.FullUI.Recs;
using Xunit;
using static Jellyfin.Plugin.FullUI.Tests.Kit;

namespace Jellyfin.Plugin.FullUI.Tests;

/// <summary>Richer features: IDF, cast/director/collection, AI embeddings and their prefixes (audit item 4).</summary>
public class FeatureTests
{
    private static int Pos(RecRow row, CatalogItem item) => row.Items.ToList().FindIndex(i => i.Item.Id == item.Id);

    [Fact]
    public void ACommonGenre_NoLongerOutweighsARareOne()
    {
        // 90 Drama-only titles and one Noir: the watched title is Drama+Noir. Without IDF both matches tie (and the
        // alphabet decides); with IDF the rare Noir match wins.
        var watched = Movie(1, "Watched", new[] { "Drama", "Noir" });
        var noir = Movie(2, "Zzz Noir", new[] { "Noir" });
        var dramas = Enumerable.Range(10, 90).Select(i => Movie(i, $"Aaa Drama {i}", new[] { "Drama" })).ToList();
        var catalog = dramas.Append(noir).Append(watched).ToList();

        var picks = Row(Build(Input(Viewer, catalog, new[] { Play(Viewer, watched, 2) })), "toppicks")!;

        Assert.Equal(noir.Id, picks.Items[0].Item.Id);
    }

    [Fact]
    public void SharedCastAndDirector_RaiseSimilarity()
    {
        var watched = Movie(1, "Watched", new[] { "Drama" }, cast: new[] { "Ann Star", "Bob Lead", "Cy Side" }, directors: new[] { "Dee Director" });
        var related = Movie(2, "Zzz Related", new[] { "Drama" }, cast: new[] { "Ann Star", "Bob Lead" }, directors: new[] { "Dee Director" });
        var unrelated = Movie(3, "Aaa Unrelated", new[] { "Drama" }, cast: new[] { "Nobody One", "Nobody Two" }, directors: new[] { "Someone Else" });
        var pad = Many(10, 12, "Pad", "Drama");
        var picks = Row(Build(Input(Viewer, pad.Concat(new[] { watched, related, unrelated }), new[] { Play(Viewer, watched, 2) })), "toppicks")!;

        Assert.True(Pos(picks, related) < Pos(picks, unrelated));
    }

    [Fact]
    public void CollectionMembers_AreSimilarToEachOther()
    {
        var col = Guid.NewGuid();
        var watched = Movie(1, "Saga 1", new[] { "Action" }, collection: col, collectionName: "Saga");
        var sequel = Movie(2, "Zzz Saga 2", new[] { "Action" }, collection: col, collectionName: "Saga");
        var other = Movie(3, "Aaa Other", new[] { "Action" });
        var pad = Many(10, 12, "Pad", "Action");
        var picks = Row(Build(Input(Viewer, pad.Concat(new[] { watched, sequel, other }), new[] { Play(Viewer, watched, 2) })), "toppicks")!;

        Assert.True(Pos(picks, sequel) < Pos(picks, other));
    }

    [Fact]
    public void Embeddings_BreakTiesBetweenOtherwiseIdenticalTitles()
    {
        var watched = Movie(1, "Watched", new[] { "Drama" });
        var near = Movie(2, "Zzz Near", new[] { "Drama" });
        var far = Movie(3, "Aaa Far", new[] { "Drama" });
        var pad = Many(10, 12, "Pad", "Drama");
        var catalog = pad.Concat(new[] { watched, near, far }).ToList();
        var emb = EmbeddingIndex.Create(new Dictionary<Guid, float[]>
        {
            [watched.Id] = new[] { 1f, 0f },
            [near.Id] = new[] { 0.95f, 0.1f },
            [far.Id] = new[] { 0.05f, 1f },
        });
        var signals = new[] { Play(Viewer, watched, 2) };

        var without = Row(Build(Input(Viewer, catalog, signals)), "toppicks")!;
        var with = Row(Build(Input(Viewer, catalog, signals, embeddings: emb)), "toppicks")!;

        Assert.True(Pos(without, far) < Pos(without, near), "precondition: identical features, the alphabet decides");
        Assert.True(Pos(with, near) < Pos(with, far), "the embedding says Near is the closer neighbour");
    }

    [Fact]
    public void Embeddings_OrderBecauseYouWatchedNeighbours_AndMissingVectorsAreHarmless()
    {
        var seed = Movie(1, "Seed", new[] { "Drama" });
        // 30 candidates with identical features; the first 20 have vectors that drift further from the seed's with the index
        // (names are reversed so the alphabet would give the opposite order), the last 10 have no vector at all.
        var items = Enumerable.Range(0, 30).Select(i => Movie(100 + i, $"Cand{30 - i:00}", new[] { "Drama" })).ToList();
        var vectors = new Dictionary<Guid, float[]> { [seed.Id] = new[] { 1f, 0f } };
        for (var i = 0; i < 20; i++)
        {
            var theta = i * 0.05;
            vectors[items[i].Id] = new[] { (float)Math.Cos(theta), (float)Math.Sin(theta) };
        }

        var catalog = items.Append(seed).ToList();
        var rows = Build(Input(Viewer, catalog, new[] { Play(Viewer, seed, 2) }, embeddings: EmbeddingIndex.Create(vectors)));
        var because = rows.First(r => r.Type == "because");

        var order = because.Items.Select(i => items.FindIndex(c => c.Id == i.Item.Id)).Where(ix => ix < 20).ToList();
        Assert.True(order.Count >= 3);
        Assert.Equal(order.OrderBy(x => x).ToList(), order);   // nearest embedding first
        Assert.All(rows, r => Assert.NotEmpty(r.Items));
        Assert.True(Row(rows, "toppicks")!.Items.Take(3).All(i => items.FindIndex(c => c.Id == i.Item.Id) < 20), "Top Picks also prefers the semantically close titles");
    }

    [Fact]
    public void EmbeddingIndex_IgnoresEmptyAndZeroVectors()
    {
        var idx = EmbeddingIndex.Create(new Dictionary<Guid, float[]> { [Id(1)] = Array.Empty<float>(), [Id(2)] = new[] { 0f, 0f }, [Id(3)] = new[] { 3f, 4f } });
        Assert.Equal(1, idx.Count);
        Assert.Equal(0.6f, idx.Unit(Id(3))![0], 3);
        Assert.Null(idx.Cosine(Id(1), Id(3)));
    }

    // ---- Ollama prefixes -------------------------------------------------------------------------------------------

    private sealed class FakeOllama : IOllamaClient
    {
        public string EmbedModel { get; set; } = "nomic-embed-text";

        public bool Enabled => true;

        public List<string> Texts { get; } = new();

        public Task<IReadOnlyList<float[]>?> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            Texts.AddRange(texts);
            return Task.FromResult<IReadOnlyList<float[]>?>(texts.Select(_ => new[] { 1f, 0f }).ToList());
        }

        public Task<string?> ChatAsync(string system, string user, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<(bool ok, string message)> TestAsync(string? url, string? e, string? c, CancellationToken ct) => Task.FromResult((true, "ok"));
    }

    [Fact]
    public async Task Indexer_UsesTheDocumentPrefix_ForNomic_AndNotForOtherModels()
    {
        using var ts = new TempStore();
        var cat = new SimCatalog { Items = { Movie(1, "Alien", new[] { "Horror" }) } };
        var nomic = new FakeOllama();
        await new EmbeddingIndexer(ts.Store, cat, nomic).RunAsync(null, default);
        Assert.All(nomic.Texts, t => Assert.StartsWith("search_document: Alien", t));

        var other = new FakeOllama { EmbedModel = "mxbai-embed-large" };
        await new EmbeddingIndexer(ts.Store, cat, other).RunAsync(null, default);
        Assert.All(other.Texts, t => Assert.StartsWith("Alien", t));
    }

    [Fact]
    public async Task Vectors_FromTheOldTextScheme_AreReEmbedded_ButCurrentOnesAreKept()
    {
        using var ts = new TempStore();
        var item = Movie(1, "Alien", new[] { "Horror" });
        var cat = new SimCatalog { Items = { item } };
        // What the previous version stored: plain text, hash of the plain text.
        ts.Store.WriteEmbeddings(e => e[item.Id.ToString("N")] = new EmbeddingEntry
        {
            Vector = new[] { 9f, 9f },
            Model = "nomic-embed-text",
            Hash = EmbeddingIndexer.HashOf(EmbeddingIndexer.TextFor(item)),
        });
        var ollama = new FakeOllama();
        var indexer = new EmbeddingIndexer(ts.Store, cat, ollama);

        Assert.Equal(1, await indexer.RunAsync(null, default));   // re-embedded under the new scheme
        Assert.Equal(0, await indexer.RunAsync(null, default));   // and now current
        Assert.Equal(EmbeddingIndexer.ExpectedHash(item, "nomic-embed-text"), ts.Store.ReadEmbeddings(e => e[item.Id.ToString("N")].Hash));
        Assert.NotEqual(EmbeddingIndexer.HashOf(EmbeddingIndexer.TextFor(item)), EmbeddingIndexer.ExpectedHash(item, "nomic-embed-text"));
    }

    [Fact]
    public async Task Search_UsesTheQueryPrefix()
    {
        using var ts = new TempStore();
        var item = Movie(1, "Alien", new[] { "Horror" });
        var cat = new SimCatalog { Items = { item } };
        ts.Store.WriteEmbeddings(e => e[item.Id.ToString("N")] = new EmbeddingEntry { Vector = new[] { 1f, 0f }, Model = "nomic-embed-text" });
        var ollama = new FakeOllama();

        var (mode, _) = await new NlSearch(ts.Store, cat, ollama).SearchAsync(Guid.NewGuid(), "space horror", default);

        Assert.Equal("semantic", mode);
        Assert.Equal("search_query: space horror", Assert.Single(ollama.Texts));
    }
}
