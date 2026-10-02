using Jellyfin.Plugin.FullUI.Data;

namespace Jellyfin.Plugin.FullUI.ApiTests;

/// <summary>The store is the plugin's only memory. It must survive restarts, corrupt files, parallel writers and a Dispose.</summary>
public class PluginStoreTests : IDisposable
{
    private readonly DiskStore _disk = new();

    public void Dispose() => _disk.Dispose();

    private static readonly Guid U = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    [Fact]
    public void Data_SurvivesRestart()
    {
        using (var s = _disk.Open())
        {
            s.Write(d =>
            {
                d.Ratings["k"] = 2;
                d.MyList.Add("m");
                d.Votes.Add(new VoteEntry { UserId = U, TmdbId = 7, Title = "T", Vote = 1, At = DateTime.UtcNow });
                d.Signals.Add(new PlaySignal { UserId = U, ItemId = Guid.NewGuid(), At = DateTime.UtcNow, Completed = true });
            });
        } // Dispose flushes

        using var reopened = _disk.Open();
        Assert.Equal(2, reopened.Read(d => d.Ratings["k"]));
        Assert.Contains("m", reopened.Read(d => d.MyList.ToList()));
        Assert.Equal("T", reopened.Read(d => d.Votes.Single().Title));
        Assert.Single(reopened.Read(d => d.Signals.ToList()));
    }

    [Fact]
    public void Flush_WritesImmediately_AndLeavesNoTempFile()
    {
        using var s = _disk.Open(TimeSpan.FromHours(1));
        s.Write(d => d.Ratings["k"] = 1);
        Assert.False(File.Exists(_disk.StoreFile));

        s.Flush();

        Assert.True(File.Exists(_disk.StoreFile));
        Assert.False(File.Exists(_disk.StoreFile + ".tmp"));
        Assert.Contains("\"k\"", File.ReadAllText(_disk.StoreFile));
    }

    [Fact]
    public async Task ContinuousWrites_StillGetSaved_TheTimerIsNotPostponedByEveryWrite()
    {
        // B-05: the debounce used to be re-armed on every write, so a busy server never saved.
        using var s = _disk.Open(TimeSpan.FromMilliseconds(250));
        var deadline = DateTime.UtcNow.AddSeconds(3);
        var saved = false;
        var n = 0;
        while (DateTime.UtcNow < deadline && !saved)
        {
            s.Write(d => d.Ratings["k" + n++] = 1);
            await Task.Delay(40);
            saved = File.Exists(_disk.StoreFile);
        }

        Assert.True(saved, "no save happened while writes kept arriving");
    }

    [Fact]
    public void CorruptFile_IsKeptAsBad_AndTheStoreStartsEmpty_WithoutThrowing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_disk.StoreFile)!);
        File.WriteAllText(_disk.StoreFile, "{ this is not json");

        using var s = _disk.Open();

        Assert.Empty(s.Read(d => d.Ratings.ToList()));
        Assert.True(File.Exists(_disk.StoreFile + ".bad"));
        s.Write(d => d.Ratings["after"] = 1); // still usable
        s.Flush();
        using var again = _disk.Open();
        Assert.Equal(1, again.Read(d => d.Ratings["after"]));
    }

    [Fact]
    public void CorruptEmbeddingsFile_IsRebuilt_NotFatal()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_disk.EmbeddingsFile)!);
        File.WriteAllText(_disk.EmbeddingsFile, "][");

        using var s = _disk.Open();

        Assert.Equal(0, s.ReadEmbeddings(e => e.Count));
        Assert.True(File.Exists(_disk.EmbeddingsFile + ".bad"));
    }

    [Fact]
    public async Task ParallelWriters_LoseNothing()
    {
        using (var s = _disk.Open(TimeSpan.FromMilliseconds(20)))
        {
            await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
            {
                for (var i = 0; i < 400; i++)
                {
                    s.Write(d => d.Ratings[$"{t}|{i}"] = 1);
                    if (i % 50 == 0)
                    {
                        s.Read(d => d.Ratings.Count);
                        s.Flush(); // explicit flushes racing the timer
                    }
                }
            })));
        }

        using var reopened = _disk.Open();
        Assert.Equal(8 * 400, reopened.Read(d => d.Ratings.Count));
    }

    [Fact]
    public void WriteAfterDispose_DoesNotThrow()
    {
        var s = _disk.Open();
        s.Dispose();

        var ex = Record.Exception(() =>
        {
            s.Write(d => d.Ratings["late"] = 1);
            s.Flush();
            s.Dispose();
        });

        Assert.Null(ex);
    }

    [Fact]
    public void FailingDisk_DoesNotThrow_AndRetriesLater()
    {
        using var s = _disk.Open(TimeSpan.FromHours(1));
        s.Write(d => d.Ratings["k"] = 1);
        Directory.Delete(Path.GetDirectoryName(_disk.StoreFile)!, true); // the folder vanishes: the write must fail quietly

        Assert.Null(Record.Exception(() => s.Flush()));

        Directory.CreateDirectory(Path.GetDirectoryName(_disk.StoreFile)!);
        s.Flush();
        Assert.True(File.Exists(_disk.StoreFile), "the unsaved change was forgotten after one failed write");
    }

    [Fact]
    public void Embeddings_LiveInTheirOwnFile_AndDoNotRideAlongWithEveryWrite()
    {
        using (var s = _disk.Open())
        {
            s.WriteEmbeddings(e => e["a"] = new EmbeddingEntry { Vector = new[] { 1f, 2f }, Model = "m", Hash = "h" });
            s.Write(d => d.Ratings["k"] = 1);
        }

        Assert.DoesNotContain("Vector", File.ReadAllText(_disk.StoreFile));
        Assert.Contains("Vector", File.ReadAllText(_disk.EmbeddingsFile));

        using var reopened = _disk.Open();
        var e = reopened.ReadEmbeddings(x => x["a"]);
        Assert.Equal(new[] { 1f, 2f }, e.Vector);
        Assert.Equal("m", e.Model);
    }

    [Fact]
    public void SavingStore_DoesNotRewriteTheEmbeddingsFile()
    {
        using var s = _disk.Open(TimeSpan.FromHours(1));
        s.WriteEmbeddings(e => e["a"] = new EmbeddingEntry { Vector = new[] { 1f } });
        s.Flush();
        var stamp = File.GetLastWriteTimeUtc(_disk.EmbeddingsFile);
        File.SetLastWriteTimeUtc(_disk.EmbeddingsFile, stamp.AddHours(-1));
        var old = File.GetLastWriteTimeUtc(_disk.EmbeddingsFile);

        s.Write(d => d.Ratings["k"] = 1);
        s.Flush();

        Assert.Equal(old, File.GetLastWriteTimeUtc(_disk.EmbeddingsFile));
    }

    [Fact]
    public void EmbeddingsInAnOldStoreFile_AreMovedOut_OnFirstLoad()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_disk.StoreFile)!);
        File.WriteAllText(_disk.StoreFile, "{\"Ratings\":{\"k\":2},\"Embeddings\":{\"abc\":[0.5,0.25]}}");

        using (var s = _disk.Open())
        {
            Assert.Equal(2, s.Read(d => d.Ratings["k"]));
            Assert.Equal(new[] { 0.5f, 0.25f }, s.ReadEmbeddings(e => e["abc"].Vector));
            Assert.Equal(string.Empty, s.ReadEmbeddings(e => e["abc"].Model)); // unknown model: adopted by the indexer later
        }

        Assert.DoesNotContain("Embeddings", File.ReadAllText(_disk.StoreFile));
        Assert.Contains("abc", File.ReadAllText(_disk.EmbeddingsFile));
    }

    [Fact]
    public void OldSignals_AndOldOrExcessNotifications_ArePrunedOnSave()
    {
        using (var s = _disk.Open())
        {
            s.Write(d =>
            {
                d.Signals.Add(new PlaySignal { UserId = U, ItemId = Guid.NewGuid(), At = DateTime.UtcNow.AddDays(-500) });
                d.Signals.Add(new PlaySignal { UserId = U, ItemId = Guid.NewGuid(), At = DateTime.UtcNow.AddDays(-5) });
                d.Notifications.Add(new NotificationEntry { UserId = U, Text = "ancient", At = DateTime.UtcNow.AddDays(-200) });
                for (var i = 0; i < 260; i++)
                {
                    d.Notifications.Add(new NotificationEntry { UserId = U, Text = "n" + i, At = DateTime.UtcNow.AddMinutes(-i) });
                }
            });
        }

        using var reopened = _disk.Open();
        Assert.Single(reopened.Read(d => d.Signals.ToList()));
        var notes = reopened.Read(d => d.Notifications.ToList());
        Assert.Equal(200, notes.Count);
        Assert.DoesNotContain(notes, n => n.Text == "ancient");
        Assert.Contains(notes, n => n.Text == "n0"); // newest are kept
    }

    [Fact]
    public void SnapshotIsIndependent_OfLaterWrites()
    {
        var data = new StoreData();
        data.Ratings["a"] = 1;
        var snap = data.Snapshot();
        data.Ratings["b"] = 2;
        data.Signals.Add(new PlaySignal());

        Assert.Single(snap.Ratings);
        Assert.Empty(snap.Signals);
    }
}
