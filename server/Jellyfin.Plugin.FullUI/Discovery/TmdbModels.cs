using System.Collections.Generic;

namespace Jellyfin.Plugin.FullUI.Discovery;

public sealed record TmdbTitle(
    int Id,
    string MediaType,              // "movie" | "tv"
    string Title,
    string? Overview,
    string? PosterPath,
    string? BackdropPath,
    string? ReleaseDate,
    double VoteAverage,
    IReadOnlyList<int> GenreIds);

public sealed record TmdbVideo(string Site, string Type, string Key, bool Official, string? Language);

public sealed record TmdbGenre(int Id, string Name);

/// <summary>An age rating TMDB lists for a title in one country (for example US / PG-13, or US / TV-MA).</summary>
public sealed record TmdbCertification(string Country, string Rating);

/// <summary>A show whose next episode is the first episode of a new season, airing on <see cref="AirDate"/> (yyyy-MM-dd).</summary>
public sealed record TmdbNewSeason(TmdbTitle Show, int SeasonNumber, string AirDate);
