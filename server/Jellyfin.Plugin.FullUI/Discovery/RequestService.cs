using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Library;

namespace Jellyfin.Plugin.FullUI.Discovery;

public sealed record RequestRow(
    int TmdbId,
    string MediaType,
    string Title,
    string? PosterPath,
    string? ReleaseDate,
    int WantCount,
    IReadOnlyList<string> Voters,
    string Status,
    string Note,
    string TmdbUrl,
    DateTime LastVoteAt);

/// <summary>Admin view over votes: aggregation, status edits, CSV, and the "now in library" sync.</summary>
public sealed class RequestService
{
    public static readonly string[] Statuses = { "Requested", "Getting it", "Added" };

    private readonly PluginStore _store;
    private readonly IUserDirectory _users;
    private readonly IConfigSource _config;

    public RequestService(PluginStore store, IUserDirectory users, IConfigSource config)
    {
        _store = store;
        _users = users;
        _config = config;
    }

    public IReadOnlyList<RequestRow> Aggregate(string? sort)
    {
        var (votes, statuses) = _store.Read(d => (
            d.Votes.Where(v => v.Vote > 0).Select(v => v).ToList(),
            d.Statuses.ToDictionary(kv => kv.Key, kv => kv.Value)));
        var rows = new List<RequestRow>();
        foreach (var g in votes.GroupBy(v => (v.MediaType, v.TmdbId)))
        {
            var ordered = g.OrderByDescending(v => v.At).ToList();
            var meta = ordered.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v.Title)) ?? ordered[0];
            var voters = ordered.Select(v => _users.NameOf(v.UserId)).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            statuses.TryGetValue(VoteService.StatusKey(g.Key.MediaType, g.Key.TmdbId), out var st);
            rows.Add(new RequestRow(
                g.Key.TmdbId,
                g.Key.MediaType,
                string.IsNullOrWhiteSpace(meta.Title) ? $"TMDB {g.Key.TmdbId}" : meta.Title,
                ordered.Select(v => v.PosterPath).FirstOrDefault(p => !string.IsNullOrEmpty(p)),
                ordered.Select(v => v.ReleaseDate).FirstOrDefault(p => !string.IsNullOrEmpty(p)),
                g.Select(v => v.UserId).Distinct().Count(),
                voters,
                st?.Status ?? "Requested",
                st?.Note ?? string.Empty,
                $"https://www.themoviedb.org/{g.Key.MediaType}/{g.Key.TmdbId}",
                ordered[0].At));
        }

        return string.Equals(sort, "recent", StringComparison.OrdinalIgnoreCase)
            ? rows.OrderByDescending(r => r.LastVoteAt).ThenByDescending(r => r.WantCount).ToList()
            : rows.OrderByDescending(r => r.WantCount).ThenByDescending(r => r.LastVoteAt).ToList();
    }

    public bool SetStatus(int tmdbId, string mediaType, string status, string? note, DateTime? now = null)
    {
        if (tmdbId <= 0 || (mediaType != "movie" && mediaType != "tv") || !Statuses.Contains(status))
        {
            return false;
        }

        var n = (note ?? string.Empty).Trim();
        if (n.Length > 500)
        {
            n = n[..500];
        }

        _store.Write(d => d.Statuses[VoteService.StatusKey(mediaType, tmdbId)] = new RequestStatusEntry
        {
            Status = status,
            Note = n,
            UpdatedAt = now ?? DateTime.UtcNow,
        });
        return true;
    }

    /// <summary>Marks wanted titles that now exist in the library as "Added" and notifies their voters. Idempotent.</summary>
    public int Sync(IReadOnlyList<CatalogItem> library, DateTime? now = null)
    {
        var notify = _config.Current.RequestNotifications;
        var at = now ?? DateTime.UtcNow;
        var changed = 0;
        var byKey = new Dictionary<string, CatalogItem>();
        foreach (var i in library)
        {
            if (i.TmdbId is > 0)
            {
                byKey.TryAdd(ComingSoonRanker.Key(ComingSoonRanker.MediaTypeOf(i), i.TmdbId.Value), i);
            }
        }

        _store.Write(d =>
        {
            foreach (var g in d.Votes.Where(v => v.Vote > 0).GroupBy(v => VoteService.StatusKey(v.MediaType, v.TmdbId)).ToList())
            {
                if (!byKey.TryGetValue(g.Key, out var item))
                {
                    continue;
                }

                d.Statuses.TryGetValue(g.Key, out var st);
                if (st is { Status: "Added" })
                {
                    continue;
                }

                d.Statuses[g.Key] = new RequestStatusEntry { Status = "Added", Note = st?.Note ?? string.Empty, UpdatedAt = at };
                changed++;
                if (!notify)
                {
                    continue;
                }

                foreach (var uid in g.Select(v => v.UserId).Distinct())
                {
                    d.Notifications.Add(new NotificationEntry
                    {
                        UserId = uid,
                        Text = $"{item.Name} is now available",
                        ItemId = item.Id,
                        At = at,
                    });
                }
            }
        });
        return changed;
    }

    /// <summary>
    /// Deletes everything stored for users that no longer exist in Jellyfin (votes, notifications, ratings, My List,
    /// Coming Soon, play signals). Does nothing if the user list is empty (that means "could not read it", not "everyone left").
    /// </summary>
    /// <returns>Number of users purged.</returns>
    public int PurgeDeletedUsers()
    {
        var known = _users.UserIds.Select(u => u.ToString("N")).ToHashSet();
        if (known.Count == 0)
        {
            return 0;
        }

        var purged = 0;
        _store.Write(d =>
        {
            static string? UserOfKey(string key) => key.Length > 33 && key[32] == '|' ? key[..32] : null;

            var stored = new HashSet<string>();
            stored.UnionWith(d.Votes.Select(v => v.UserId.ToString("N")));
            stored.UnionWith(d.Notifications.Select(n => n.UserId.ToString("N")));
            stored.UnionWith(d.Signals.Select(sig => sig.UserId.ToString("N")));
            stored.UnionWith(d.ComingSoon.Keys);
            stored.UnionWith(d.Ratings.Keys.Select(UserOfKey).OfType<string>());
            stored.UnionWith(d.MyList.Select(UserOfKey).OfType<string>());
            stored.UnionWith(d.Reminders.Select(r => r.UserId.ToString("N")));
            stored.UnionWith(d.Onboarding.Keys);
            stored.UnionWith(d.HiddenContinue.Select(UserOfKey).OfType<string>());
            var gone = stored.Where(u => !known.Contains(u)).ToHashSet();
            if (gone.Count == 0)
            {
                return;
            }

            d.Votes.RemoveAll(v => gone.Contains(v.UserId.ToString("N")));
            d.Notifications.RemoveAll(n => gone.Contains(n.UserId.ToString("N")));
            d.Signals.RemoveAll(sig => gone.Contains(sig.UserId.ToString("N")));
            d.Reminders.RemoveAll(r => gone.Contains(r.UserId.ToString("N")));
            d.HiddenContinue.RemoveWhere(k => UserOfKey(k) is { } u && gone.Contains(u));
            foreach (var u in gone)
            {
                d.Onboarding.Remove(u);
                d.ComingSoon.Remove(u);
                d.BackfilledUsers.Remove(u);
            }

            foreach (var k in d.Ratings.Keys.Where(k => UserOfKey(k) is { } u && gone.Contains(u)).ToList())
            {
                d.Ratings.Remove(k);
            }

            d.MyList.RemoveWhere(k => UserOfKey(k) is { } u && gone.Contains(u));
            purged = gone.Count;
        });
        return purged;
    }

    public static string ToCsv(IEnumerable<RequestRow> rows)
    {
        var sb = new StringBuilder();
        sb.Append("Title,Type,TmdbId,Votes,Status,Note,Voters,TmdbUrl,LastVote\r\n");
        foreach (var r in rows)
        {
            sb.Append(string.Join(',', new[]
            {
                CsvCell(r.Title),
                CsvCell(r.MediaType),
                CsvCell(r.TmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                CsvCell(r.WantCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                CsvCell(r.Status),
                CsvCell(r.Note),
                CsvCell(string.Join("; ", r.Voters)),
                CsvCell(r.TmdbUrl),
                CsvCell(r.LastVoteAt.ToString("u", System.Globalization.CultureInfo.InvariantCulture)),
            }));
            sb.Append("\r\n");
        }

        return sb.ToString();
    }

    /// <summary>Quotes a CSV field; neutralises spreadsheet formulas by prefixing a single quote.</summary>
    public static string CsvCell(string? value)
    {
        var s = value ?? string.Empty;
        if (s.Length > 0 && (s[0] is '=' or '+' or '-' or '@' or '\t' or '\r'))
        {
            s = "'" + s;
        }

        if (s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
        {
            s = "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        return s;
    }
}
