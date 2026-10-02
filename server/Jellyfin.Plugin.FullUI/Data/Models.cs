using System;
using System.Collections.Generic;

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

    /// <summary>Embeddings for semantic search. key: itemId:N.</summary>
    public Dictionary<string, float[]> Embeddings { get; set; } = new();

    /// <summary>LLM-generated row titles. key: genre name.</summary>
    public Dictionary<string, string> RowTitles { get; set; } = new();

    public static string UserItemKey(Guid userId, Guid itemId) => $"{userId:N}|{itemId:N}";
}
