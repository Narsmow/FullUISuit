using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullUI.Library;

/// <summary>
/// <see cref="ICatalog"/> backed by Jellyfin's library. The title snapshot is rebuilt lazily after
/// <see cref="Invalidate"/>; per-user visibility comes from a per-user library query so that library
/// access and parental limits are enforced by Jellyfin itself.
/// </summary>
public sealed class JellyfinCatalog : ICatalog
{
    private static readonly TimeSpan VisibleTtl = TimeSpan.FromMinutes(10);
    private static readonly BaseItemKind[] Kinds = { BaseItemKind.Movie, BaseItemKind.Series };

    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly ILogger<JellyfinCatalog> _log;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<Guid, (DateTime At, IReadOnlySet<Guid> Ids)> _visible = new();
    private IReadOnlyList<CatalogItem> _items = Array.Empty<CatalogItem>();
    private bool _stale = true;
    private int _generation;

    public JellyfinCatalog(ILibraryManager library, IUserManager users, ILogger<JellyfinCatalog> log)
    {
        _library = library;
        _users = users;
        _log = log;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<CatalogItem> All
    {
        get
        {
            lock (_lock)
            {
                if (_stale)
                {
                    try
                    {
                        _items = Load();
                        _stale = false;
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "FullUI: catalog refresh failed; keeping previous snapshot");
                    }
                }

                return _items;
            }
        }
    }

    public IReadOnlySet<Guid> VisibleTo(Guid userId)
    {
        if (_visible.TryGetValue(userId, out var hit) && DateTime.UtcNow - hit.At < VisibleTtl)
        {
            return hit.Ids;
        }

        try
        {
            var gen = _generation;
            var user = _users.GetUserById(userId);
            if (user is null)
            {
                return new HashSet<Guid>();
            }

            var query = new InternalItemsQuery(user)
            {
                IncludeItemTypes = Kinds,
                Recursive = true,
            };
            var ids = _library.GetItemList(query).Select(i => i.Id).ToHashSet();
            if (gen == _generation)
            {
                _visible[userId] = (DateTime.UtcNow, ids);
            }

            return ids;
        }
        catch (Exception ex)
        {
            // Fail closed: if access cannot be determined, show nothing rather than leak restricted titles.
            _log.LogError(ex, "FullUI: could not determine visible titles for {User}", userId);
            return new HashSet<Guid>();
        }
    }

    public void Invalidate()
    {
        lock (_lock)
        {
            _stale = true;
            _generation++;
        }

        _visible.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public static string? ParseYouTubeKey(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        string? key = null;
        if (host.EndsWith("youtu.be", StringComparison.Ordinal))
        {
            key = uri.AbsolutePath.Trim('/').Split('/')[0];
        }
        else if (host.EndsWith("youtube.com", StringComparison.Ordinal) || host.EndsWith("youtube-nocookie.com", StringComparison.Ordinal))
        {
            var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2)).FirstOrDefault(p => p[0] == "v" && p.Length == 2);
            key = query?[1] ?? (uri.AbsolutePath.StartsWith("/embed/", StringComparison.Ordinal) ? uri.AbsolutePath[7..].Split('/')[0] : null);
        }

        return !string.IsNullOrEmpty(key) && key.Length >= 6 && key.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_') ? key : null;
    }

    private IReadOnlyList<CatalogItem> Load()
    {
        var latest = new Dictionary<Guid, DateTime>();
        var episodes = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            Recursive = true,
            OrderBy = new[] { (ItemSortBy.DateCreated, Jellyfin.Database.Implementations.Enums.SortOrder.Descending) },
        });
        foreach (var ep in episodes.OfType<Episode>())
        {
            if (ep.SeriesId != Guid.Empty && !latest.ContainsKey(ep.SeriesId))
            {
                latest[ep.SeriesId] = ep.DateCreated;
            }
        }

        var items = _library.GetItemList(new InternalItemsQuery { IncludeItemTypes = Kinds, Recursive = true });
        var result = new List<CatalogItem>(items.Count);
        foreach (var i in items)
        {
            try
            {
                var isSeries = i is Series;
                result.Add(new CatalogItem
                {
                    Id = i.Id,
                    Kind = isSeries ? CatalogKind.Series : CatalogKind.Movie,
                    Name = i.Name ?? string.Empty,
                    Year = i.ProductionYear,
                    Rating = i.CommunityRating,
                    OfficialRating = i.OfficialRating,
                    Overview = i.Overview,
                    RuntimeMinutes = i.RunTimeTicks is long t && t > 0 ? (int)(t / TimeSpan.TicksPerMinute) : null,
                    Genres = i.Genres ?? Array.Empty<string>(),
                    Studios = i.Studios ?? Array.Empty<string>(),
                    Tags = i.Tags ?? Array.Empty<string>(),
                    DateAdded = i.DateCreated,
                    LatestEpisodeAdded = isSeries && latest.TryGetValue(i.Id, out var l) ? l : null,
                    TmdbId = i.ProviderIds.TryGetValue("Tmdb", out var tmdb) && int.TryParse(tmdb, out var tid) ? tid : null,
                    TrailerKey = i.RemoteTrailers?.Select(r => ParseYouTubeKey(r.Url)).FirstOrDefault(k => k is not null),
                    HasBackdrop = i.HasImage(ImageType.Backdrop),
                    HasLogo = i.HasImage(ImageType.Logo),
                });
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "FullUI: skipping unreadable library item {Item}", i.Id);
            }
        }

        _log.LogInformation("FullUI: catalog loaded with {Count} titles", result.Count);
        return result;
    }
}
