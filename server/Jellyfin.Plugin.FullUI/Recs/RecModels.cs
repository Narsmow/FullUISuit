using System;
using System.Collections.Generic;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Library;

namespace Jellyfin.Plugin.FullUI.Recs;

/// <summary>What Jellyfin says one user has watched of one series: episodes per real season (season 0 specials never count).</summary>
public sealed record SeriesWatchInfo(IReadOnlyDictionary<int, int> EpisodesBySeason);

/// <summary>The next episode Jellyfin would play for a series (for the "S2:E5" label).</summary>
public sealed record NextUpEpisode(int? Season, int? Episode);

/// <summary>Everything the engine needs for one user. No Jellyfin types, so it is trivially testable.</summary>
public sealed class RecInput
{
    public Guid UserId { get; init; }
    public IReadOnlyList<CatalogItem> Catalog { get; init; } = Array.Empty<CatalogItem>();
    public IReadOnlySet<Guid> Visible { get; init; } = new HashSet<Guid>();
    public IReadOnlyList<PlaySignal> Signals { get; init; } = Array.Empty<PlaySignal>();

    /// <summary>All users' ratings, key "{userId:N}|{itemId:N}" (see StoreData.UserItemKey).</summary>
    public IReadOnlyDictionary<string, int> Ratings { get; init; } = new Dictionary<string, int>();

    /// <summary>All users' My List entries, same key format.</summary>
    public IReadOnlySet<string> MyList { get; init; } = new HashSet<string>();

    public DateTime Now { get; init; } = DateTime.UtcNow;
    public string ServerName { get; init; } = "FullUI";
    public int TopTenWindowDays { get; init; } = 7;
    public IReadOnlySet<Guid> ExcludedUsers { get; init; } = new HashSet<Guid>();
    public IReadOnlyDictionary<string, string> RowTitles { get; init; } = new Dictionary<string, string>();

    /// <summary>Series with a next episode ready (Jellyfin Next Up), most relevant first. They join Continue Watching.</summary>
    public IReadOnlyList<Guid> NextUpSeries { get; init; } = Array.Empty<Guid>();

    /// <summary>The episode behind each <see cref="NextUpSeries"/> entry, when known.</summary>
    public IReadOnlyDictionary<Guid, NextUpEpisode> NextUpEpisodes { get; init; } = new Dictionary<Guid, NextUpEpisode>();

    /// <summary>This user's watched episodes per series as Jellyfin reports them (more complete than the stored signals). Optional.</summary>
    public IReadOnlyDictionary<Guid, SeriesWatchInfo> SeriesWatch { get; init; } = new Dictionary<Guid, SeriesWatchInfo>();

    /// <summary>Titles this user removed from their rows (never shown again in any row).</summary>
    public IReadOnlySet<Guid> HiddenItems { get; init; } = new HashSet<Guid>();

    /// <summary>
    /// Users with a restrictive parental cap (see <see cref="KidDetector"/>). Their signals are left out of Top 10, Trending,
    /// popularity and collaborative filtering - unless the viewer is one of them.
    /// </summary>
    public IReadOnlySet<Guid> KidUsers { get; init; } = new HashSet<Guid>();

    /// <summary>Optional Ollama embeddings of the catalog (used as one more similarity term).</summary>
    public EmbeddingIndex? Embeddings { get; init; }

    /// <summary>Optional cache of other users' item weights between requests; valid while <see cref="DataFingerprint"/> is unchanged.</summary>
    public UserWeightsCache? WeightsCache { get; init; }

    public string? DataFingerprint { get; init; }
}

/// <summary>One card in a row. Match/Reason/SeriesLabel/MinutesLeft are optional explanations shown by the clients.</summary>
public sealed record RankedItem(
    CatalogItem Item,
    string[] Badges,
    int? Rank,
    double? Progress,
    int? Match = null,
    string? Reason = null,
    string? SeriesLabel = null,
    int? MinutesLeft = null);

/// <summary>A composed row. <see cref="Order"/> is the contract position (see RecEngine.Order*).</summary>
public sealed record RecRow(string Id, string Title, string Type, int Order, IReadOnlyList<RankedItem> Items);
