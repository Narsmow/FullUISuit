using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.FullUI.Data;

/// <summary>One playback observation for (user, title). Episodes are rolled up to their series id.</summary>
public sealed class PlaySignal
{
    public Guid UserId { get; set; }
    public Guid ItemId { get; set; }          // movie id, or SERIES id for episodes
    public bool IsEpisode { get; set; }
    public DateTime At { get; set; }
    public double Completion { get; set; }    // 0..1
    public bool Completed { get; set; }
}

public sealed class VoteEntry
{
    public Guid UserId { get; set; }
    public int TmdbId { get; set; }
    public string MediaType { get; set; } = "movie"; // "movie" | "tv"
    public int Vote { get; set; }                    // 1 = I want this, -1 = not for me
    public string Title { get; set; } = string.Empty;
    public string? PosterPath { get; set; }
    public string? BackdropPath { get; set; }
    public string? ReleaseDate { get; set; }
    public string? Overview { get; set; }
    public DateTime At { get; set; }
}

public sealed class RequestStatusEntry
{
    public string Status { get; set; } = "Requested"; // Requested | Getting it | Added
    public string Note { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
}

public sealed class NotificationEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Text { get; set; } = string.Empty;
    public Guid? ItemId { get; set; }
    public DateTime At { get; set; }
    public bool Read { get; set; }
}

/// <summary>Cached Coming Soon candidate (persisted per user so Home is instant).</summary>
public sealed class ComingSoonEntry
{
    public int TmdbId { get; set; }
    public string MediaType { get; set; } = "movie";
    public string Title { get; set; } = string.Empty;
    public string? Overview { get; set; }
    public string? PosterPath { get; set; }
    public string? BackdropPath { get; set; }
    public string? ReleaseDate { get; set; }
    public string? TrailerKey { get; set; }
    public double Score { get; set; }
}

/// <summary>One AI vector plus what it was computed from, so a changed model or edited title text is re-embedded.</summary>
public sealed class EmbeddingEntry
{
    public float[] Vector { get; set; } = System.Array.Empty<float>();

    /// <summary>Embedding model name; empty for vectors migrated from an older version.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Short hash of the text that was embedded; empty for migrated vectors.</summary>
    public string Hash { get; set; } = string.Empty;
}

/// <summary>Everything we persist. JSON file at {DataPath}/fullui/store.json (see PluginStore).</summary>
public sealed class StoreData
{
    public List<PlaySignal> Signals { get; set; } = new();

    /// <summary>key "{userId:N}|{itemId:N}" -> -1 not for me, 1 like, 2 love.</summary>
    public Dictionary<string, int> Ratings { get; set; } = new();

    /// <summary>"{userId:N}|{itemId:N}" entries.</summary>
    public HashSet<string> MyList { get; set; } = new();

    public List<VoteEntry> Votes { get; set; } = new();

    /// <summary>key: "{mediaType}:{tmdbId}".</summary>
    public Dictionary<string, RequestStatusEntry> Statuses { get; set; } = new();

    public List<NotificationEntry> Notifications { get; set; } = new();

    /// <summary>key: userId:N.</summary>
    public Dictionary<string, List<ComingSoonEntry>> ComingSoon { get; set; } = new();

    /// <summary>YouTube trailer keys. key: "{mediaType}:{tmdbId}" (tmdb) or "item:{itemId:N}" (library).</summary>
    public Dictionary<string, string> TrailerKeys { get; set; } = new();

    /// <summary>
    /// Read-only migration hook: store.json written by older versions contained "Embeddings". PluginStore moves them to
    /// embeddings.json on load and nulls this, so it is never written back. Use PluginStore.ReadEmbeddings/WriteEmbeddings.
    /// </summary>
    [JsonPropertyName("Embeddings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, float[]>? LegacyEmbeddings { get; set; }

    /// <summary>User ids (N format) whose existing Jellyfin watch history has been imported once (see PlaybackBackfill).</summary>
    public HashSet<string> BackfilledUsers { get; set; } = new();

    /// <summary>LLM-generated row titles. key: genre name.</summary>
    public Dictionary<string, string> RowTitles { get; set; } = new();

    /// <summary>A copy that is safe to serialize while other threads keep mutating the original (collections copied, elements shared).</summary>
    public StoreData Snapshot() => new()
    {
        Signals = Signals.ToList(),
        Ratings = new Dictionary<string, int>(Ratings),
        MyList = new HashSet<string>(MyList),
        Votes = Votes.ToList(),
        Statuses = new Dictionary<string, RequestStatusEntry>(Statuses),
        Notifications = Notifications.ToList(),
        ComingSoon = ComingSoon.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
        TrailerKeys = new Dictionary<string, string>(TrailerKeys),
        RowTitles = new Dictionary<string, string>(RowTitles),
        BackfilledUsers = new HashSet<string>(BackfilledUsers),
    };

    public static string UserItemKey(Guid userId, Guid itemId) => $"{userId:N}|{itemId:N}";
}
