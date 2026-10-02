using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
public sealed class JellyfinCatalog : ICatalog, IDisposable
{
    /// <summary>
    /// How long a user's list of visible titles is trusted. Permission or parental-rating changes in Jellyfin take at most
    /// this long to show up (kept short on purpose: this list is the privacy filter for every row, search and rating).
    /// </summary>
    public static readonly TimeSpan VisibleTtl = TimeSpan.FromMinutes(2);

    /// <summary>A library scan raises ItemAdded for every file; the snapshot is rebuilt at most this often.</summary>
    public static readonly TimeSpan MinReload = TimeSpan.FromSeconds(30);

    /// <summary>Quiet period before <see cref="Changed"/> is raised after a burst of invalidations.</summary>
    public static readonly TimeSpan ChangedDebounce = TimeSpan.FromSeconds(10);

    private static readonly BaseItemKind[] Kinds = { BaseItemKind.Movie, BaseItemKind.Series };

    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly ILogger<JellyfinCatalog> _log;
    private readonly object _loadLock = new();
    private readonly ConcurrentDictionary<Guid, (DateTime At, IReadOnlySet<Guid> Ids)> _visible = new();
    private readonly Timer _changedTimer;
    private volatile IReadOnlyList<CatalogItem> _items = Array.Empty<CatalogItem>();
    private volatile bool _stale = true;
    private volatile bool _force;
    private volatile bool _everLoaded;
    private long _loadedAtTicks;
    private int _generation;
    private int _visibleGeneration;
    private int _changedPending;

    public JellyfinCatalog(ILibraryManager library, IUserManager users, ILogger<JellyfinCatalog> log)
    {
        _library = library;
        _users = users;
        _log = log;
        _changedTimer = new Timer(_ => RaiseChanged(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public event EventHandler? Changed;

    public IReadOnlyList<CatalogItem> All
    {
        get
        {
            // Lock-free fast path: nothing changed since the last load.
            if (!_stale)
            {
                return _items;
            }

            // Changed, but a scan may be adding thousands of items: keep serving the snapshot we have.
            if (_everLoaded && !_force && DateTime.UtcNow.Ticks - Interlocked.Read(ref _loadedAtTicks) < MinReload.Ticks)
            {
                return _items;
            }

            // Only one thread loads; the library query runs outside any lock other callers need.
            if (_everLoaded)
            {
                if (!Monitor.TryEnter(_loadLock))
                {
                    return _items;
                }
            }
            else
            {
                Monitor.Enter(_loadLock);
            }

            try
            {
                if (_stale && (!_everLoaded || _force || DateTime.UtcNow.Ticks - Interlocked.Read(ref _loadedAtTicks) >= MinReload.Ticks))
                {
                    var gen = Volatile.Read(ref _generation);
                    _force = false;
                    try
                    {
                        _items = Load();
                        _everLoaded = true;
                        _stale = gen != Volatile.Read(ref _generation);
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "FullUI: catalog refresh failed; keeping previous snapshot");
                    }

                    Interlocked.Exchange(ref _loadedAtTicks, DateTime.UtcNow.Ticks);
                }

                return _items;
            }
            finally
            {
                Monitor.Exit(_loadLock);
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
            var gen = Volatile.Read(ref _visibleGeneration);
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
            if (gen == Volatile.Read(ref _visibleGeneration))
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
        Interlocked.Increment(ref _generation);
        _stale = true;

        // Tell consumers once per burst, shortly after the burst ends, instead of once per added file.
        if (Interlocked.Exchange(ref _changedPending, 1) == 0)
        {
            try
            {
                _changedTimer.Change(ChangedDebounce, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public void InvalidateNow()
    {
        Interlocked.Increment(ref _generation);
        _force = true;
        _stale = true;
        RaiseChanged();
    }

    public void Dispose() => _changedTimer.Dispose();

    public static string? ParseYouTubeKey(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        string? key = null;
        if (IsHost(host, "youtu.be"))
        {
            key = uri.AbsolutePath.Trim('/').Split('/')[0];
        }
        else if (IsHost(host, "youtube.com") || IsHost(host, "youtube-nocookie.com"))
        {
            var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2)).FirstOrDefault(p => p[0] == "v" && p.Length == 2);
            key = query?[1] ?? (uri.AbsolutePath.StartsWith("/embed/", StringComparison.Ordinal) ? uri.AbsolutePath[7..].Split('/')[0] : null);
        }

        return !string.IsNullOrEmpty(key) && key.Length >= 6 && key.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_') ? key : null;
    }

    /// <summary>A short stable token that changes when the item's primary image file or its modification time changes.</summary>
    internal static string? ImageTagOf(BaseItem item)
    {
        try
        {
            var info = item.GetImageInfo(ImageType.Primary, 0);
            return info is null ? null : ImageTag(info.Path, info.DateModified);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string? ImageTag(string? path, DateTime modified)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(path + "|" + modified.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>The host is the domain itself or a subdomain of it ("evilyoutu.be" is neither).</summary>
    private static bool IsHost(string host, string domain)
        => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);

    private void RaiseChanged()
    {
        Interlocked.Exchange(ref _changedPending, 0);
        Interlocked.Increment(ref _visibleGeneration);
        _visible.Clear();
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "FullUI: a library-changed listener failed");
        }
    }

    private IReadOnlyList<CatalogItem> Load()
    {
        var latest = new Dictionary<Guid, DateTime>();
        var episodes = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            Recursive = true,
            IsVirtualItem = false, // "missing episode" placeholders must not count as newly added episodes
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
                    PrimaryImageTag = ImageTagOf(i),
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
