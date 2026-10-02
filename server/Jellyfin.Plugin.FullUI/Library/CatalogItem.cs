using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.FullUI.Library;

public enum CatalogKind
{
    Movie,
    Series,
}

/// <summary>Immutable snapshot of a library title (movie or series) so the engine never touches Jellyfin types.</summary>
public sealed class CatalogItem
{
    public Guid Id { get; init; }
    public CatalogKind Kind { get; init; }
    public string Name { get; init; } = string.Empty;
    public int? Year { get; init; }
    public float? Rating { get; init; }              // community rating 0..10
    public string? OfficialRating { get; init; }     // e.g. PG-13
    public string? Overview { get; init; }
    public int? RuntimeMinutes { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Studios { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public DateTime DateAdded { get; init; }
    public DateTime? LatestEpisodeAdded { get; init; } // series only
    public int? TmdbId { get; init; }
    public string? TrailerKey { get; init; }         // YouTube id from Jellyfin RemoteTrailers
    public bool HasBackdrop { get; init; }
    public bool HasLogo { get; init; }

    /// <summary>Changes whenever the primary image changes; clients add it as <c>&amp;tag=</c> so images can be cached for a long time. Null when there is no primary image.</summary>
    public string? PrimaryImageTag { get; init; }
}

/// <summary>Per-user view of the library. Implemented against ILibraryManager; faked in tests.</summary>
public interface ICatalog
{
    /// <summary>All movies/series (snapshot, refreshed lazily and when items are added).</summary>
    IReadOnlyList<CatalogItem> All { get; }

    /// <summary>Ids this user may see (library access + parental rating applied by Jellyfin).</summary>
    IReadOnlySet<Guid> VisibleTo(Guid userId);

    /// <summary>
    /// The library changed (item added, scan running). Cheap and safe to call thousands of times: the snapshot is only
    /// refreshed lazily, at most every ~30 s, and <see cref="Changed"/> is raised once per burst.
    /// </summary>
    void Invalidate();

    /// <summary>Like <see cref="Invalidate"/> but the next read reloads immediately (manual rebuilds, nightly task).</summary>
    void InvalidateNow() => Invalidate();

    /// <summary>Raised after the library changes (debounce on the consumer side).</summary>
    event EventHandler? Changed;
}
