using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullUI.Discovery;

public enum RemindResult
{
    Ok,
    UnknownTitle,
    LimitReached,
}

/// <summary>"Remind me" for Coming Soon titles. Private to the user: every method takes the caller's id from the token.</summary>
public sealed class ReminderService
{
    public const int MaxRemindersPerUser = 200;
    private static readonly TimeSpan KeepNotified = TimeSpan.FromDays(60);

    private readonly PluginStore _store;
    private readonly ICatalog _catalog;
    private readonly IConfigSource _config;
    private readonly ILogger<ReminderService> _log;

    public ReminderService(PluginStore store, ICatalog catalog, IConfigSource config, ILogger<ReminderService> log)
    {
        _store = store;
        _catalog = catalog;
        _config = config;
        _log = log;
    }

    /// <summary>Turns a reminder on or off. Turning it on needs the title to be on the user's Coming Soon / vote lists (that is where the title text and date come from).</summary>
    public RemindResult Set(Guid userId, int tmdbId, string mediaType, bool on, DateTime now)
    {
        if (tmdbId <= 0 || (mediaType != "movie" && mediaType != "tv"))
        {
            return RemindResult.UnknownTitle;
        }

        var result = RemindResult.Ok;
        _store.Write(d =>
        {
            if (!on)
            {
                d.Reminders.RemoveAll(r => r.UserId == userId && r.TmdbId == tmdbId && r.MediaType == mediaType);
                return;
            }

            if (d.Reminders.Any(r => r.UserId == userId && r.TmdbId == tmdbId && r.MediaType == mediaType && !r.Notified))
            {
                return; // already set
            }

            d.Reminders.RemoveAll(r => r.UserId == userId && r.TmdbId == tmdbId && r.MediaType == mediaType); // an old, already-sent one
            if (d.Reminders.Count(r => r.UserId == userId) >= MaxRemindersPerUser)
            {
                result = RemindResult.LimitReached;
                return;
            }

            ComingSoonEntry? entry = null;
            if (d.ComingSoon.TryGetValue(userId.ToString("N"), out var list))
            {
                entry = list.Where(e => e.TmdbId == tmdbId && e.MediaType == mediaType)
                    .OrderByDescending(e => e.SeasonNumber is not null && ComingSoonRanker.IsUpcoming(e.ReleaseDate, now))
                    .ThenByDescending(e => ComingSoonRanker.IsUpcoming(e.ReleaseDate, now))
                    .FirstOrDefault();
            }

            if (entry is not null)
            {
                d.Reminders.Add(new ReminderEntry
                {
                    UserId = userId,
                    TmdbId = tmdbId,
                    MediaType = mediaType,
                    Title = entry.Title,
                    PosterPath = entry.PosterPath,
                    ReleaseDate = entry.ReleaseDate,
                    SeasonNumber = entry.SeasonNumber,
                    CreatedAt = now,
                });
                return;
            }

            var vote = d.Votes.Where(v => v.UserId == userId && v.TmdbId == tmdbId && v.MediaType == mediaType).OrderByDescending(v => v.At).FirstOrDefault();
            if (vote is null || string.IsNullOrWhiteSpace(vote.Title))
            {
                result = RemindResult.UnknownTitle;
                return;
            }

            d.Reminders.Add(new ReminderEntry
            {
                UserId = userId,
                TmdbId = tmdbId,
                MediaType = mediaType,
                Title = vote.Title,
                PosterPath = vote.PosterPath,
                ReleaseDate = vote.ReleaseDate,
                CreatedAt = now,
            });
        });
        return result;
    }

    /// <summary>The caller's own reminders, soonest release first.</summary>
    public IReadOnlyList<ReminderEntry> List(Guid userId)
        => _store.Read(d => d.Reminders.Where(r => r.UserId == userId)
            .OrderBy(r => r.ReleaseDate ?? "9999", StringComparer.Ordinal).ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .Select(Copy).ToList());

    private static ReminderEntry Copy(ReminderEntry r) => new()
    {
        UserId = r.UserId,
        TmdbId = r.TmdbId,
        MediaType = r.MediaType,
        Title = r.Title,
        PosterPath = r.PosterPath,
        ReleaseDate = r.ReleaseDate,
        SeasonNumber = r.SeasonNumber,
        CreatedAt = r.CreatedAt,
        Notified = r.Notified,
    };

    /// <summary>
    /// Sends the "it is out" notifications that are due. Runs daily and whenever the library changes. A reminder fires once,
    /// either on its release day ("X is out today") or as soon as the title is in the library and visible to that user
    /// ("X is now on {server}"). Returns how many notifications were created. Never throws.
    /// </summary>
    public int Process(DateTime now)
    {
        try
        {
            return ProcessCore(now);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "FullUI: sending reminders failed");
            return 0;
        }
    }

    private int ProcessCore(DateTime now)
    {
        var due = _store.Read(d => d.Reminders.Where(r => !r.Notified).Select(Copy).ToList());
        if (due.Count == 0)
        {
            Prune(now);
            return 0;
        }

        var server = string.IsNullOrWhiteSpace(_config.Current.ServerName) ? "FullUI" : _config.Current.ServerName.Trim();
        var library = _catalog.All;
        var byKey = new Dictionary<string, CatalogItem>();
        foreach (var i in library.Where(i => i.TmdbId is > 0))
        {
            byKey.TryAdd(ComingSoonRanker.Key(ComingSoonRanker.MediaTypeOf(i), i.TmdbId!.Value), i);
        }

        var sent = 0;
        foreach (var g in due.GroupBy(r => r.UserId))
        {
            try
            {
                IReadOnlySet<Guid>? visible = null;
                var fire = new List<(ReminderEntry R, string Text, Guid? ItemId)>();
                foreach (var r in g)
                {
                    if (r.SeasonNumber is null && byKey.TryGetValue(ComingSoonRanker.Key(r.MediaType, r.TmdbId), out var item))
                    {
                        visible ??= _catalog.VisibleTo(g.Key);
                        if (visible.Contains(item.Id))
                        {
                            fire.Add((r, $"{r.Title} is now on {server}", item.Id));
                            continue;
                        }
                    }

                    if (DateTime.TryParse(r.ReleaseDate, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var rel)
                        && rel.Date <= now.ToUniversalTime().Date)
                    {
                        fire.Add((r, rel.Date == now.ToUniversalTime().Date ? $"{r.Title} is out today" : $"{r.Title} is out now", null));
                    }
                }

                if (fire.Count == 0)
                {
                    continue;
                }

                _store.Write(d =>
                {
                    foreach (var (r, text, itemId) in fire)
                    {
                        var live = d.Reminders.FirstOrDefault(x => x.UserId == r.UserId && x.TmdbId == r.TmdbId && x.MediaType == r.MediaType && !x.Notified);
                        if (live is null)
                        {
                            continue; // removed meanwhile
                        }

                        live.Notified = true;
                        d.Notifications.Add(new NotificationEntry { UserId = r.UserId, Text = text, ItemId = itemId, At = now });
                        sent++;
                    }
                });
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "FullUI: could not send reminders for one user");
            }
        }

        Prune(now);
        return sent;
    }

    private void Prune(DateTime now)
    {
        var cutoff = now - KeepNotified;
        if (!_store.Read(d => d.Reminders.Any(r => r.Notified && r.CreatedAt < cutoff)))
        {
            return;
        }

        _store.Write(d => d.Reminders.RemoveAll(r => r.Notified && r.CreatedAt < cutoff));
    }
}
