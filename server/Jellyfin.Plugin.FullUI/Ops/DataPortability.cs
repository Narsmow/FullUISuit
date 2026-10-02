using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Discovery;
using Jellyfin.Plugin.FullUI.Library;
using Jellyfin.Plugin.FullUI.Metrics;

namespace Jellyfin.Plugin.FullUI.Ops;

public sealed class ExportVote
{
    public int TmdbId { get; set; }
    public string MediaType { get; set; } = "movie";
    public int Vote { get; set; }
    public string? Title { get; set; }
    public string? PosterPath { get; set; }
    public string? BackdropPath { get; set; }
    public string? ReleaseDate { get; set; }
    public string? Overview { get; set; }
    public DateTime At { get; set; }
}

public sealed class ExportNotification
{
    public Guid Id { get; set; }
    public string? Text { get; set; }
    public string? ItemId { get; set; }
    public DateTime At { get; set; }
    public bool Read { get; set; }
}

public sealed class ExportReminder
{
    public int TmdbId { get; set; }
    public string MediaType { get; set; } = "movie";
    public string? Title { get; set; }
    public string? PosterPath { get; set; }
    public string? ReleaseDate { get; set; }
    public int? SeasonNumber { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool Notified { get; set; }
}

public sealed class ExportOnboarding
{
    public bool Completed { get; set; }
    public bool Skipped { get; set; }
    public DateTime At { get; set; }
    public List<string>? Genres { get; set; }
}

public sealed class ExportUser
{
    public string? Name { get; set; }
    public Dictionary<string, int>? Ratings { get; set; }
    public List<string>? MyList { get; set; }
    public List<ExportVote>? Votes { get; set; }
    public List<ExportNotification>? Notifications { get; set; }
    public List<ExportReminder>? Reminders { get; set; }
    public ExportOnboarding? Onboarding { get; set; }
    public List<string>? HiddenContinue { get; set; }
}

public sealed class ExportStatus
{
    public string? Status { get; set; }
    public string? Note { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>The backup file. Plain JSON so it can be read, kept and restored by hand.</summary>
public sealed class ExportDocument
{
    public const string FormatName = "fullui-export";
    public const int FormatVersion = 1;

    public string? Format { get; set; }
    public int Version { get; set; }
    public DateTime ExportedAt { get; set; }
    public Dictionary<string, ExportUser>? Users { get; set; }
    public Dictionary<string, ExportStatus>? Statuses { get; set; }
}

public sealed record ImportSummary(
    bool DryRun,
    bool Valid,
    string Message,
    int Users,
    int Ratings,
    int MyList,
    int Votes,
    int Notifications,
    int Reminders,
    int Onboarding,
    int Hidden,
    int Requests,
    IReadOnlyList<string> Skipped);

public sealed record PurgeOutcome(bool ConfirmRequired, string? Token, int ExpiresInSeconds, string Message, IReadOnlyDictionary<string, int> Summary);

/// <summary>Backup (export), restore (import) and "delete my data" (purge). Strict about what it accepts and honest about what it did.</summary>
public sealed class DataPortability
{
    public const int MaxUsers = 500;
    public const int MaxEntriesPerList = 50_000;
    public const int MaxTextLength = 500;
    private static readonly TimeSpan TokenLife = TimeSpan.FromMinutes(5);

    private readonly PluginStore _store;
    private readonly ICatalog _catalog;
    private readonly IUserDirectory _users;
    private readonly InteractionLog _events;
    private readonly IHomeInvalidator _home;
    private readonly object _lock = new();
    private readonly Dictionary<string, (string Target, DateTime Expires)> _tokens = new();

    public DataPortability(PluginStore store, ICatalog catalog, IUserDirectory users, InteractionLog events, IHomeInvalidator home)
    {
        _store = store;
        _catalog = catalog;
        _users = users;
        _events = events;
        _home = home;
    }

    // ---- export ---------------------------------------------------------------------------------------------------

    /// <summary>Everything FullUI keeps for one user (<paramref name="onlyUser"/>) or for everybody (null).</summary>
    public ExportDocument Export(Guid? onlyUser, DateTime now)
    {
        return _store.Read(d =>
        {
            var doc = new ExportDocument { Format = ExportDocument.FormatName, Version = ExportDocument.FormatVersion, ExportedAt = now, Users = new(), Statuses = new() };
            var ids = new HashSet<string>();
            ids.UnionWith(d.Ratings.Keys.Select(UserOfKey).OfType<string>());
            ids.UnionWith(d.MyList.Select(UserOfKey).OfType<string>());
            ids.UnionWith(d.HiddenContinue.Select(UserOfKey).OfType<string>());
            ids.UnionWith(d.Votes.Select(v => v.UserId.ToString("N")));
            ids.UnionWith(d.Notifications.Select(n => n.UserId.ToString("N")));
            ids.UnionWith(d.Reminders.Select(r => r.UserId.ToString("N")));
            ids.UnionWith(d.Onboarding.Keys);
            if (onlyUser is { } one)
            {
                ids.RemoveWhere(i => i != one.ToString("N"));
            }

            foreach (var u in ids.OrderBy(i => i, StringComparer.Ordinal))
            {
                var prefix = u + "|";
                var uid = Guid.TryParse(u, out var g) ? g : Guid.Empty;
                doc.Users![u] = new ExportUser
                {
                    Name = uid == Guid.Empty ? null : _users.NameOf(uid),
                    Ratings = d.Ratings.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary(kv => kv.Key[prefix.Length..], kv => kv.Value),
                    MyList = d.MyList.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Select(k => k[prefix.Length..]).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                    HiddenContinue = d.HiddenContinue.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Select(k => k[prefix.Length..]).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                    Votes = d.Votes.Where(v => v.UserId == uid).Select(v => new ExportVote { TmdbId = v.TmdbId, MediaType = v.MediaType, Vote = v.Vote, Title = v.Title, PosterPath = v.PosterPath, BackdropPath = v.BackdropPath, ReleaseDate = v.ReleaseDate, Overview = v.Overview, At = v.At }).ToList(),
                    Notifications = d.Notifications.Where(n => n.UserId == uid).Select(n => new ExportNotification { Id = n.Id, Text = n.Text, ItemId = n.ItemId?.ToString("N"), At = n.At, Read = n.Read }).ToList(),
                    Reminders = d.Reminders.Where(r => r.UserId == uid).Select(r => new ExportReminder { TmdbId = r.TmdbId, MediaType = r.MediaType, Title = r.Title, PosterPath = r.PosterPath, ReleaseDate = r.ReleaseDate, SeasonNumber = r.SeasonNumber, CreatedAt = r.CreatedAt, Notified = r.Notified }).ToList(),
                    Onboarding = d.Onboarding.TryGetValue(u, out var ob) ? new ExportOnboarding { Completed = ob.Completed, Skipped = ob.Skipped, At = ob.At, Genres = ob.Genres.ToList() } : null,
                };
            }

            if (onlyUser is null)
            {
                foreach (var kv in d.Statuses)
                {
                    doc.Statuses![kv.Key] = new ExportStatus { Status = kv.Value.Status, Note = kv.Value.Note, UpdatedAt = kv.Value.UpdatedAt };
                }
            }

            return doc;
        });
    }

    // ---- import ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Checks a backup and (unless <paramref name="dryRun"/>) merges it in. Strict: wrong format, unknown users, unknown titles,
    /// out-of-range values and over-long text are skipped and counted, never half-applied. Nothing is written for a dry run.
    /// </summary>
    public ImportSummary Import(ExportDocument? doc, bool dryRun)
    {
        var skipped = new List<string>();
        void Skip(string why) => skipped.Add(why);

        if (doc is null || doc.Format != ExportDocument.FormatName)
        {
            return Invalid(dryRun, "This is not a FullUI backup file.");
        }

        if (doc.Version != ExportDocument.FormatVersion)
        {
            return Invalid(dryRun, $"This backup uses format {doc.Version}, which this FullUI cannot read.");
        }

        var docUsers = doc.Users ?? new Dictionary<string, ExportUser>();
        if (docUsers.Count > MaxUsers)
        {
            return Invalid(dryRun, $"The backup lists more than {MaxUsers} users, which is not plausible. Nothing was imported.");
        }

        var total = docUsers.Values.Sum(u => (u.Ratings?.Count ?? 0) + (u.MyList?.Count ?? 0) + (u.Votes?.Count ?? 0) + (u.Notifications?.Count ?? 0) + (u.Reminders?.Count ?? 0) + (u.HiddenContinue?.Count ?? 0));
        if (total > MaxEntriesPerList || (doc.Statuses?.Count ?? 0) > MaxEntriesPerList)
        {
            return Invalid(dryRun, "The backup is far larger than FullUI would ever write. Nothing was imported.");
        }

        var known = _catalog.All.Select(i => i.Id).ToHashSet();
        var plan = new List<Action<StoreData>>();
        int ratings = 0, myList = 0, votes = 0, notes = 0, reminders = 0, onboarding = 0, hidden = 0, requests = 0, users = 0;
        int unknownUsers = 0, unknownItems = 0, invalid = 0;

        foreach (var (userKey, u) in docUsers)
        {
            if (!Guid.TryParse(userKey, out var uid) || uid == Guid.Empty || _users.NameOf(uid) is null)
            {
                unknownUsers++;
                continue;
            }

            users++;
            var un = uid.ToString("N");
            foreach (var (itemKey, rating) in u.Ratings ?? new())
            {
                if (!Guid.TryParse(itemKey, out var item) || !known.Contains(item))
                {
                    unknownItems++;
                }
                else if (rating is not (-1 or 1 or 2))
                {
                    invalid++;
                }
                else
                {
                    var key = StoreData.UserItemKey(uid, item);
                    plan.Add(d => d.Ratings[key] = rating);
                    ratings++;
                }
            }

            foreach (var itemKey in (u.MyList ?? new()).Distinct())
            {
                if (!Guid.TryParse(itemKey, out var item) || !known.Contains(item))
                {
                    unknownItems++;
                }
                else
                {
                    var key = StoreData.UserItemKey(uid, item);
                    plan.Add(d => d.MyList.Add(key));
                    myList++;
                }
            }

            foreach (var itemKey in (u.HiddenContinue ?? new()).Distinct())
            {
                if (!Guid.TryParse(itemKey, out var item) || !known.Contains(item))
                {
                    unknownItems++;
                }
                else
                {
                    var key = StoreData.UserItemKey(uid, item);
                    plan.Add(d => d.HiddenContinue.Add(key));
                    hidden++;
                }
            }

            foreach (var v in u.Votes ?? new())
            {
                if (v.TmdbId <= 0 || (v.MediaType != "movie" && v.MediaType != "tv") || v.Vote is not (-1 or 1) || TooLong(v.Title, v.Overview, v.PosterPath, v.BackdropPath, v.ReleaseDate))
                {
                    invalid++;
                    continue;
                }

                var copy = v;
                plan.Add(d =>
                {
                    d.Votes.RemoveAll(x => x.UserId == uid && x.TmdbId == copy.TmdbId && x.MediaType == copy.MediaType);
                    d.Votes.Add(new VoteEntry { UserId = uid, TmdbId = copy.TmdbId, MediaType = copy.MediaType, Vote = copy.Vote, Title = copy.Title ?? string.Empty, PosterPath = copy.PosterPath, BackdropPath = copy.BackdropPath, ReleaseDate = copy.ReleaseDate, Overview = copy.Overview, At = copy.At });
                });
                votes++;
            }

            foreach (var n in u.Notifications ?? new())
            {
                Guid? itemId = null;
                if (n.ItemId is not null)
                {
                    if (!Guid.TryParse(n.ItemId, out var parsed))
                    {
                        invalid++;
                        continue;
                    }

                    itemId = parsed;
                }

                if (string.IsNullOrWhiteSpace(n.Text) || TooLong(n.Text) || n.Id == Guid.Empty)
                {
                    invalid++;
                    continue;
                }

                var copy = n;
                plan.Add(d =>
                {
                    if (!d.Notifications.Any(x => x.Id == copy.Id))
                    {
                        d.Notifications.Add(new NotificationEntry { Id = copy.Id, UserId = uid, Text = copy.Text!, ItemId = itemId, At = copy.At, Read = copy.Read });
                    }
                });
                notes++;
            }

            foreach (var r in u.Reminders ?? new())
            {
                if (r.TmdbId <= 0 || (r.MediaType != "movie" && r.MediaType != "tv") || string.IsNullOrWhiteSpace(r.Title) || TooLong(r.Title, r.PosterPath, r.ReleaseDate))
                {
                    invalid++;
                    continue;
                }

                var copy = r;
                plan.Add(d =>
                {
                    d.Reminders.RemoveAll(x => x.UserId == uid && x.TmdbId == copy.TmdbId && x.MediaType == copy.MediaType);
                    d.Reminders.Add(new ReminderEntry { UserId = uid, TmdbId = copy.TmdbId, MediaType = copy.MediaType, Title = copy.Title!, PosterPath = copy.PosterPath, ReleaseDate = copy.ReleaseDate, SeasonNumber = copy.SeasonNumber, CreatedAt = copy.CreatedAt, Notified = copy.Notified });
                });
                reminders++;
            }

            if (u.Onboarding is { } ob)
            {
                var genres = (ob.Genres ?? new()).Where(g => !string.IsNullOrWhiteSpace(g) && g.Length <= 60).Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList();
                plan.Add(d => d.Onboarding[un] = new OnboardingState { Completed = ob.Completed, Skipped = ob.Skipped, At = ob.At, Genres = genres });
                onboarding++;
            }
        }

        foreach (var (key, st) in doc.Statuses ?? new())
        {
            var parts = key.Split(':');
            if (parts.Length != 2 || (parts[0] != "movie" && parts[0] != "tv") || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0
                || st.Status is null || !RequestService.Statuses.Contains(st.Status) || TooLong(st.Note))
            {
                invalid++;
                continue;
            }

            var copy = st;
            plan.Add(d => d.Statuses[key] = new RequestStatusEntry { Status = copy.Status!, Note = copy.Note ?? string.Empty, UpdatedAt = copy.UpdatedAt });
            requests++;
        }

        if (unknownUsers > 0)
        {
            Skip($"{unknownUsers} user(s) in the backup do not exist on this server (their data was left out).");
        }

        if (unknownItems > 0)
        {
            Skip($"{unknownItems} title(s) are not in this library (left out).");
        }

        if (invalid > 0)
        {
            Skip($"{invalid} entr{(invalid == 1 ? "y was" : "ies were")} not valid (left out).");
        }

        if (!dryRun && plan.Count > 0)
        {
            _store.Write(d =>
            {
                foreach (var step in plan)
                {
                    step(d);
                }
            });
            foreach (var uid in docUsers.Keys.Select(k => Guid.TryParse(k, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty))
            {
                SafeInvalidate(uid);
            }
        }

        var verb = dryRun ? "would be imported" : "were imported";
        return new ImportSummary(dryRun, true, $"{plan.Count} item(s) {verb}.", users, ratings, myList, votes, notes, reminders, onboarding, hidden, requests, skipped);
    }

    private static ImportSummary Invalid(bool dryRun, string message)
        => new(dryRun, false, message, 0, 0, 0, 0, 0, 0, 0, 0, 0, new[] { message });

    private static bool TooLong(params string?[] values) => values.Any(v => v is not null && v.Length > MaxTextLength);

    // ---- purge ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Two steps. Without a token this only describes what would be deleted and hands out a token (valid 5 minutes, single use,
    /// bound to the same target). With the token it deletes. <paramref name="target"/> is a user id or "all".
    /// </summary>
    public PurgeOutcome? Purge(string? target, string? token, DateTime now)
    {
        Guid? user = null;
        var all = string.Equals(target?.Trim(), "all", StringComparison.OrdinalIgnoreCase);
        if (!all)
        {
            if (!Guid.TryParse(target, out var g) || g == Guid.Empty)
            {
                return null;
            }

            user = g;
        }

        var normalized = all ? "all" : user!.Value.ToString("N");
        if (string.IsNullOrEmpty(token))
        {
            var t = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
            lock (_lock)
            {
                foreach (var k in _tokens.Where(kv => kv.Value.Expires < now).Select(kv => kv.Key).ToList())
                {
                    _tokens.Remove(k);
                }

                _tokens[t] = (normalized, now + TokenLife);
            }

            return new PurgeOutcome(true, t, (int)TokenLife.TotalSeconds,
                all ? "This permanently deletes ALL FullUI data (ratings, My List, votes, request statuses, notifications, reminders, AI index, usage statistics). Send the token to confirm."
                    : "This permanently deletes everything FullUI stores for this user. Send the token to confirm.",
                Summarize(user));
        }

        lock (_lock)
        {
            if (!_tokens.Remove(token, out var entry) || entry.Target != normalized || entry.Expires < now)
            {
                return new PurgeOutcome(true, null, 0, "That confirmation has expired or does not match. Ask for a new one.", new Dictionary<string, int>());
            }
        }

        var summary = Summarize(user);
        DoPurge(user);
        return new PurgeOutcome(false, null, 0, all ? "All FullUI data was deleted." : "That user's FullUI data was deleted.", summary);
    }

    private IReadOnlyDictionary<string, int> Summarize(Guid? user)
        => _store.Read(d =>
        {
            if (user is null)
            {
                return new Dictionary<string, int>
                {
                    ["signals"] = d.Signals.Count, ["ratings"] = d.Ratings.Count, ["myList"] = d.MyList.Count, ["votes"] = d.Votes.Count,
                    ["requests"] = d.Statuses.Count, ["notifications"] = d.Notifications.Count, ["reminders"] = d.Reminders.Count,
                };
            }

            var u = user.Value;
            var prefix = u.ToString("N") + "|";
            return new Dictionary<string, int>
            {
                ["signals"] = d.Signals.Count(s => s.UserId == u),
                ["ratings"] = d.Ratings.Keys.Count(k => k.StartsWith(prefix, StringComparison.Ordinal)),
                ["myList"] = d.MyList.Count(k => k.StartsWith(prefix, StringComparison.Ordinal)),
                ["votes"] = d.Votes.Count(v => v.UserId == u),
                ["notifications"] = d.Notifications.Count(n => n.UserId == u),
                ["reminders"] = d.Reminders.Count(r => r.UserId == u),
            };
        });

    private void DoPurge(Guid? user)
    {
        if (user is null)
        {
            _store.Write(d =>
            {
                d.Signals.Clear();
                d.Ratings.Clear();
                d.MyList.Clear();
                d.Votes.Clear();
                d.Statuses.Clear();
                d.Notifications.Clear();
                d.ComingSoon.Clear();
                d.TrailerKeys.Clear();
                d.RowTitles.Clear();
                d.RowTitleStamps.Clear();
                d.Reminders.Clear();
                d.Onboarding.Clear();
                d.HiddenContinue.Clear();
                // BackfilledUsers stays: it only says "Jellyfin's own history was already looked at"; clearing it would pull that history back in.
            });
            _store.WriteEmbeddings(e => e.Clear());
            _events.Delete(null);
            foreach (var id in _users.UserIds)
            {
                SafeInvalidate(id);
            }

            return;
        }

        var u = user.Value;
        var prefix = u.ToString("N") + "|";
        _store.Write(d =>
        {
            d.Signals.RemoveAll(s => s.UserId == u);
            foreach (var k in d.Ratings.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                d.Ratings.Remove(k);
            }

            d.MyList.RemoveWhere(k => k.StartsWith(prefix, StringComparison.Ordinal));
            d.HiddenContinue.RemoveWhere(k => k.StartsWith(prefix, StringComparison.Ordinal));
            d.Votes.RemoveAll(v => v.UserId == u);
            d.Notifications.RemoveAll(n => n.UserId == u);
            d.Reminders.RemoveAll(r => r.UserId == u);
            d.ComingSoon.Remove(u.ToString("N"));
            d.Onboarding.Remove(u.ToString("N"));
        });
        _events.Delete(u);
        SafeInvalidate(u);
    }

    private void SafeInvalidate(Guid id)
    {
        try
        {
            _home.Invalidate(id);
        }
        catch (Exception)
        {
            // The cache expires by itself; never fail a restore or delete because of it.
        }
    }

    private static string? UserOfKey(string key) => key.Length > 33 && key[32] == '|' ? key[..32] : null;
}
