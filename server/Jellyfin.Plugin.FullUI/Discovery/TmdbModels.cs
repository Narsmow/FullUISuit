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
