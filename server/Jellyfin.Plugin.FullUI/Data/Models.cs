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

    /// <summary>True when the title was upcoming (release date today or later) when the list was built. The reader re-checks the date.</summary>
    public bool Upcoming { get; set; }

    /// <summary>Set when this entry is a genuinely new season of a show; the title is then "Show: Season N". Such entries are not hidden because the show is in the library.</summary>
    public int? SeasonNumber { get; set; }

    /// <summary>TMDB certification used for parental filtering, "COUNTRY:RATING" (for example "US:PG-13"); null when TMDB has none.</summary>
    public string? Certification { get; set; }
}

/// <summary>"Remind me" for one TMDB title. Private to the user who asked.</summary>
public sealed class ReminderEntry
{
    public Guid UserId { get; set; }
    public int TmdbId { get; set; }
    public string MediaType { get; set; } = "movie";
    public string Title { get; set; } = string.Empty;
    public string? PosterPath { get; set; }
    public string? ReleaseDate { get; set; }
    public int? SeasonNumber { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>The notification has been sent; the reminder stays listed until the user removes it or it expires.</summary>
    public bool Notified { get; set; }
}

/// <summary>First-run "pick your favourites" state for one user.</summary>
public sealed class OnboardingState
{
    public bool Completed { get; set; }
    public bool Skipped { get; set; }
    public DateTime At { get; set; }

    /// <summary>Genres the user said they like (library spelling).</summary>
    public List<string> Genres { get; set; } = new();
}

/// <summary>One run of a FullUI scheduled task or background service, for the health page.</summary>
public sealed class TaskRunRecord
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }

    /// <summary>"Success" | "Problem" (ran but something went wrong) | "Failed" | "Cancelled".</summary>
    public string Outcome { get; set; } = "Success";

    /// <summary>Short plain-English note. Never contains secrets.</summary>
    public string Message { get; set; } = string.Empty;
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
    /// <summary>
    /// Schema version of store.json. Files written before versioning existed have no value (0) and count as version 1.
    /// See <see cref="StoreMigrations"/>.
    /// </summary>
    public int Version { get; set; }

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

    /// <summary>"Remind me" requests (private per user).</summary>
    public List<ReminderEntry> Reminders { get; set; } = new();

    /// <summary>key: userId:N.</summary>
    public Dictionary<string, OnboardingState> Onboarding { get; set; } = new();

    /// <summary>"{userId:N}|{itemId:N}": titles the user removed from Continue Watching.</summary>
    public HashSet<string> HiddenContinue { get; set; } = new();

    /// <summary>Recent runs of FullUI tasks (bounded, see TaskRunLog).</summary>
    public List<TaskRunRecord> TaskRuns { get; set; } = new();

    /// <summary>When each LLM row title was generated. key: genre name (same keys as RowTitles).</summary>
    public Dictionary<string, DateTime> RowTitleStamps { get; set; } = new();

    /// <summary>A copy that is safe to serialize while other threads keep mutating the original (collections copied, elements shared).</summary>
    public StoreData Snapshot() => new()
    {
        Version = Version,
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
        Reminders = Reminders.ToList(),
        Onboarding = new Dictionary<string, OnboardingState>(Onboarding),
        HiddenContinue = new HashSet<string>(HiddenContinue),
        TaskRuns = TaskRuns.ToList(),
        RowTitleStamps = new Dictionary<string, DateTime>(RowTitleStamps),
    };

    public static string UserItemKey(Guid userId, Guid itemId) => $"{userId:N}|{itemId:N}";
}
