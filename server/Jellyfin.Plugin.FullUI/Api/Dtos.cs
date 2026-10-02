using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.FullUI.Api;

/// <summary>Light card sent to every client (web + Fire TV). Clients build image URLs from Id + flags.</summary>
public sealed record ItemCard(
    string Id,
    string Name,
    string Type,                  // "Movie" | "Series"
    int? Year,
    float? Rating,
    string? Rated,
    string? Overview,
    string[] Genres,
    int? RuntimeMinutes,
    string[] Badges,
    string? TrailerKey,
    int? TmdbId,
    double? Progress,             // 0..1 when resumable, else null
    bool HasBackdrop,
    bool HasLogo,
    int MyRating,                 // -1, 0 (none), 1, 2
    bool InMyList,
    int? Rank,                    // 1..10 in Top 10 rows
    string? ImageTag = null,      // primary image tag: add &tag= to image URLs for long-lived caching
    int? MatchPercent = null,     // 1..99, calibrated from the ranker score; null when unknown (cold start)
    string? Reason = null,        // short plain-English why, e.g. "Because you watched Dark"
    string? SeriesLabel = null,   // continue-watching label such as "S2:E5", null for movies
    int? MinutesLeft = null);     // minutes remaining when resumable

public sealed record ComingSoonCard(
    int TmdbId,
    string MediaType,             // "movie" | "tv"
    string Title,
    string? Overview,
    string? PosterPath,           // TMDB path; client uses https://image.tmdb.org/t/p/w342{path}
    string? BackdropPath,
    string? ReleaseDate,
    string? TrailerKey,
    int MyVote,                   // 1 want, -1 not for me, 0 none
    bool Upcoming = false,        // release date is today or later (a true "Coming Soon" title); false = already released, still requestable
    bool Reminded = false);       // the caller asked to be reminded

public sealed record HomeRow(
    string Id,
    string Title,
    string Type,                  // continue|toppicks|because|top10|trending|mylist|recent|genre|hidden|again|newseasons|comingsoon
    IReadOnlyList<ItemCard> Items,
    IReadOnlyList<ComingSoonCard>? ComingSoon = null);

public sealed record HomeResponse(string ServerName, string AccentColor, IReadOnlyList<HomeRow> Rows);

public sealed record RateRequest(Guid ItemId, int Rating);

public sealed record MyListRequest(Guid ItemId, bool Add);

public sealed record VoteRequest(
    int TmdbId,
    string MediaType,
    int Vote,
    string? Title,
    string? PosterPath,
    string? BackdropPath,
    string? ReleaseDate,
    string? Overview);

public sealed record SearchResponse(string Mode, IReadOnlyList<ItemCard> Items);

public sealed record NotificationDto(Guid Id, string Text, DateTime At, bool Read, string? ItemId);

public sealed record MarkReadRequest(Guid[]? Ids);
