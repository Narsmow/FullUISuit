using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.FullUI.Configuration;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Discovery;
using Jellyfin.Plugin.FullUI.Library;
using Jellyfin.Plugin.FullUI.Recs;
using Jellyfin.Plugin.FullUI.Services;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.FullUI.Tests;

/// <summary>Shared builders for engine tests: a fixed clock, compact catalog items, play signals and a RecInput.</summary>
internal static class Kit
{
    public static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    public static Guid Id(int n) => new(n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1);

    public static CatalogItem Movie(int n, string name, string[] genres, float? rating = 6.5f, int daysOld = 400, int? year = 2005,
        int? tmdb = null, string[]? cast = null, string[]? directors = null, Guid? collection = null, string? collectionName = null,
        string? rated = null, int? runtime = null, bool backdrop = false) => new()
        {
            Id = Id(n),
            Kind = CatalogKind.Movie,
            Name = name,
            Year = year,
            Rating = rating,
            Genres = genres,
            DateAdded = Now.AddDays(-daysOld),
            TmdbId = tmdb,
            Cast = cast ?? Array.Empty<string>(),
            Directors = directors ?? Array.Empty<string>(),
            CollectionId = collection,
            CollectionName = collectionName,
            OfficialRating = rated,
            RuntimeMinutes = runtime,
            HasBackdrop = backdrop,
        };

    /// <summary>A series. <paramref name="episodes"/> = episodes per season (season 1, 2, ...); <paramref name="seasonAddedDaysAgo"/> = when each season arrived.</summary>
    public static CatalogItem Show(int n, string name, string[] genres, int[]? episodes = null, int[]? seasonAddedDaysAgo = null,
        float? rating = 6.5f, int daysOld = 400, int[]? recentEpisodeDaysAgo = null, string? rated = null, int? runtime = null, int? tmdb = null)
    {
        var seasons = new List<SeasonInfo>();
        for (var i = 0; i < (episodes?.Length ?? 0); i++)
        {
            var added = Now.AddDays(-(seasonAddedDaysAgo is not null && i < seasonAddedDaysAgo.Length ? seasonAddedDaysAgo[i] : daysOld));
            seasons.Add(new SeasonInfo(i + 1, episodes![i], added, added));
        }

        var recent = (recentEpisodeDaysAgo ?? Array.Empty<int>()).Select(d => Now.AddDays(-d)).OrderByDescending(d => d).ToList();
        DateTime? latest = recent.Count > 0 ? recent[0] : seasons.Count > 0 ? seasons.Max(s => s.LastAdded) : null;
        return new CatalogItem
        {
            Id = Id(n),
            Kind = CatalogKind.Series,
            Name = name,
            Year = 2010,
            Rating = rating,
            Genres = genres,
            DateAdded = Now.AddDays(-daysOld),
            Seasons = seasons,
            RecentEpisodeDates = recent,
            LatestEpisodeAdded = latest,
            OfficialRating = rated,
            RuntimeMinutes = runtime,
            TmdbId = tmdb,
        };
    }

    public static PlaySignal Play(Guid user, CatalogItem item, double daysAgo = 1, double completion = 1, bool? completed = null) => new()
    {
        UserId = user,
        ItemId = item.Id,
        IsEpisode = false,
        At = Now.AddDays(-daysAgo),
        Completion = completion,
        Completed = completed ?? completion >= 0.9,
    };

    public static PlaySignal Ep(Guid user, CatalogItem show, int season, int episode, double daysAgo = 1, double completion = 1) => new()
    {
        UserId = user,
        ItemId = show.Id,
        IsEpisode = true,
        At = Now.AddDays(-daysAgo),
        Completion = completion,
        Completed = completion >= 0.9,
        Season = season,
        Episode = episode,
    };

    /// <summary>All episodes of the given seasons watched (one signal each).</summary>
    public static IEnumerable<PlaySignal> WatchSeasons(Guid user, CatalogItem show, double daysAgo, params int[] seasons)
    {
        foreach (var s in seasons)
        {
            var count = show.Seasons.First(x => x.Number == s).Episodes;
            for (var e = 1; e <= count; e++)
            {
                yield return Ep(user, show, s, e, daysAgo);
            }
        }
    }

    public static string Key(Guid user, CatalogItem item) => StoreData.UserItemKey(user, item.Id);

    public static RecInput Input(Guid user, IEnumerable<CatalogItem> catalog, IEnumerable<PlaySignal>? signals = null,
        Dictionary<string, int>? ratings = null, HashSet<string>? myList = null, IEnumerable<Guid>? excluded = null,
        IEnumerable<Guid>? nextUp = null, IEnumerable<Guid>? hidden = null, IEnumerable<Guid>? kids = null,
        IReadOnlySet<Guid>? visible = null, EmbeddingIndex? embeddings = null, DateTime? now = null, int window = 7,
        Dictionary<Guid, NextUpEpisode>? nextUpEpisodes = null, UserWeightsCache? cache = null, string? fingerprint = null,
        Dictionary<Guid, SeriesWatchInfo>? watch = null)
    {
        var cat = catalog.ToList();
        return new RecInput
        {
            UserId = user,
            Catalog = cat,
            Visible = visible ?? cat.Select(c => c.Id).ToHashSet(),
            Signals = (signals ?? Array.Empty<PlaySignal>()).ToList(),
            Ratings = ratings ?? new Dictionary<string, int>(),
            MyList = myList ?? new HashSet<string>(),
            Now = now ?? Now,
            ServerName = "Srv",
            TopTenWindowDays = window,
            ExcludedUsers = (excluded ?? Array.Empty<Guid>()).ToHashSet(),
            NextUpSeries = (nextUp ?? Array.Empty<Guid>()).ToList(),
            NextUpEpisodes = nextUpEpisodes ?? new Dictionary<Guid, NextUpEpisode>(),
            HiddenItems = (hidden ?? Array.Empty<Guid>()).ToHashSet(),
            KidUsers = (kids ?? Array.Empty<Guid>()).ToHashSet(),
            Embeddings = embeddings,
            WeightsCache = cache,
            DataFingerprint = fingerprint,
            SeriesWatch = watch ?? new Dictionary<Guid, SeriesWatchInfo>(),
        };
    }

    public static IReadOnlyList<RecRow> Build(RecInput input) => RecEngine.Build(input);

    public static RecRow? Row(IReadOnlyList<RecRow> rows, string id) => rows.FirstOrDefault(r => r.Id == id);

    public static IEnumerable<Guid> AllIds(IReadOnlyList<RecRow> rows) => rows.SelectMany(r => r.Items.Select(i => i.Item.Id));

    public static List<CatalogItem> Many(int start, int count, string prefix, params string[] genres) =>
        Enumerable.Range(start, count).Select(i => Movie(i, $"{prefix}{i:000}", genres)).ToList();

    public static readonly Guid U1 = Id(9001), U2 = Id(9002), U3 = Id(9003), U4 = Id(9004), Viewer = Id(9100);
}

/// <summary>A PluginStore on a temp folder (the real store, no fakes).</summary>
internal sealed class TempStore : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fullui-engine-" + Guid.NewGuid().ToString("N"));

    public TempStore()
    {
        var paths = DispatchProxy.Create<IApplicationPaths, PathsProxy>();
        ((PathsProxy)(object)paths).Dir = _dir;
        Store = new PluginStore(paths, NullLogger<PluginStore>.Instance);
    }

    public PluginStore Store { get; }

    public void Dispose()
    {
        Store.Dispose();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    public class PathsProxy : DispatchProxy
    {
        public string Dir { get; set; } = string.Empty;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.ReturnType == typeof(string) ? Dir : null;
    }
}

/// <summary>An in-memory library where every user may have a different visible set.</summary>
internal sealed class SimCatalog : ICatalog
{
    public List<CatalogItem> Items { get; set; } = new();

    public Dictionary<Guid, HashSet<Guid>> VisibleByUser { get; } = new();

    public IReadOnlyList<CatalogItem> All => Items;

    private HashSet<Guid>? _all;
    private int _allFor = -1;

    public IReadOnlySet<Guid> VisibleTo(Guid userId)
    {
        if (VisibleByUser.TryGetValue(userId, out var v))
        {
            return v;
        }

        if (_all is null || _allFor != Items.Count)
        {
            _all = Items.Select(i => i.Id).ToHashSet();
            _allFor = Items.Count;
        }

        return _all;
    }

    public void Invalidate()
    {
    }

    public event EventHandler? Changed { add { } remove { } }
}

internal sealed class SimConfig : IConfigSource
{
    public PluginConfiguration Current { get; set; } = new() { ServerName = "Home" };
}

internal sealed class SimNextUp : INextUpSource
{
    public Dictionary<Guid, List<NextUpEntry>> ByUser { get; } = new();

    public IReadOnlyList<Guid> NextUpSeries(Guid userId) => NextUpEntries(userId).Select(e => e.SeriesId).ToList();

    public IReadOnlyList<NextUpEntry> NextUpEntries(Guid userId) => ByUser.TryGetValue(userId, out var l) ? l : new List<NextUpEntry>();
}
