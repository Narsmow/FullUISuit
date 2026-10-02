using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.FullUI.Discovery;

namespace Jellyfin.Plugin.FullUI.Metrics;

/// <summary>One event as the browser sends it. Everything is optional on the wire; <see cref="EventValidator"/> decides what is usable.</summary>
public sealed record ClientEvent(string? Type, string? RowType, string? ItemId, string? Query, JsonElement? At);

public sealed record EventsRequest(ClientEvent[]? Events);

public sealed record EventsResult(int Accepted, int Rejected);

/// <summary>Checks and cleans client-sent events. The user id never comes from here: the controller supplies it from the token.</summary>
public static class EventValidator
{
    public const int MaxBatch = 200;
    public const int MaxQueryLength = 100;
    private static readonly Regex RowTypeOk = new("^[a-z0-9][a-z0-9_\\-]{0,39}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static StoredEvent? Validate(Guid userId, ClientEvent? e, DateTime now)
    {
        if (e is null || EventTypes.Canonical(e.Type) is not { } type)
        {
            return null;
        }

        var row = e.RowType?.Trim();
        if (!string.IsNullOrEmpty(row) && !RowTypeOk.IsMatch(row))
        {
            return null;
        }

        string? item = null;
        if (!string.IsNullOrWhiteSpace(e.ItemId))
        {
            if (!Guid.TryParse(e.ItemId.Trim(), out var g))
            {
                return null;
            }

            item = g.ToString("N");
        }

        string? query = null;
        if (!string.IsNullOrWhiteSpace(e.Query))
        {
            query = CleanQuery(e.Query);
        }

        // Search events without a query carry no information.
        if ((type is "searchIssued") && query is null)
        {
            return null;
        }

        return new StoredEvent(userId, type, string.IsNullOrEmpty(row) ? null : row.ToLowerInvariant(), item, query, ParseTime(e.At, now));
    }

    public static string? CleanQuery(string raw)
    {
        var chars = raw.Where(c => !char.IsControl(c)).ToArray();
        var s = Regex.Replace(new string(chars), @"\s+", " ").Trim();
        if (s.Length == 0)
        {
            return null;
        }

        return s.Length > MaxQueryLength ? s[..MaxQueryLength] : s;
    }

    /// <summary>The client's clock is not trusted: a time outside [1 day ago, 5 minutes ahead] (or no time) becomes "now".</summary>
    public static DateTime ParseTime(JsonElement? at, DateTime now)
    {
        DateTime? parsed = null;
        try
        {
            if (at is { } el)
            {
                if (el.ValueKind == JsonValueKind.String && DateTime.TryParse(el.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d))
                {
                    parsed = d;
                }
                else if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var ms) && ms > 0 && ms < 253402300799000)
                {
                    parsed = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
                }
            }
        }
        catch (Exception)
        {
            parsed = null;
        }

        return parsed is { } p && p >= now.AddDays(-1) && p <= now.AddMinutes(5) ? p : now;
    }
}

/// <summary>Per-user sliding limit so a buggy or hostile page cannot flood the log.</summary>
public sealed class EventRateLimiter
{
    public const int MaxEventsPerMinute = 1000;

    private readonly object _lock = new();
    private readonly Dictionary<Guid, (DateTime Start, int Count)> _windows = new();

    /// <summary>True when <paramref name="count"/> more events fit in this user's current minute (and reserves them).</summary>
    public bool TryTake(Guid userId, int count, DateTime now)
    {
        lock (_lock)
        {
            if (_windows.Count > 5000)
            {
                foreach (var k in _windows.Where(kv => now - kv.Value.Start > TimeSpan.FromMinutes(2)).Select(kv => kv.Key).ToList())
                {
                    _windows.Remove(k);
                }
            }

            _windows.TryGetValue(userId, out var w);
            if (w.Start == default || now - w.Start >= TimeSpan.FromMinutes(1))
            {
                w = (now, 0);
            }

            if (w.Count + count > MaxEventsPerMinute)
            {
                _windows[userId] = w;
                return false;
            }

            _windows[userId] = (w.Start, w.Count + count);
            return true;
        }
    }
}

public sealed record RowMetric(string RowType, int Shown, int Clicks, int Plays, int Expanded, double TakeRate, double PlaysPerShown);

public sealed record SearchMetric(string Query, int Count, int Users);

public sealed record DayCount(string Day, int Events);

public sealed record MetricsReport(
    bool Collecting,
    DateTime GeneratedAt,
    int WindowDays,
    int TotalEvents,
    int ActiveUsers,
    IReadOnlyList<RowMetric> Rows,
    IReadOnlyList<string> NeverClickedRows,
    int PlaysFromRows,
    int PlaysTotal,
    IReadOnlyList<SearchMetric> TopSearches,
    IReadOnlyList<DayCount> EventsPerDay);

/// <summary>Reads the interaction log and answers aggregate questions. Admin-facing results never contain per-user raw events.</summary>
public sealed class MetricsService : IRowEngagementProvider
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);
    private const int TopSearchCount = 20;

    private readonly InteractionLog _log;
    private readonly IConfigSource _config;
    private readonly object _lock = new();
    private IReadOnlyList<StoredEvent>? _events;
    private long _eventsVersion = -1;
    private DateTime _eventsAt;
    private Dictionary<Guid, Dictionary<string, double>>? _engagement;

    public MetricsService(InteractionLog log, IConfigSource config)
    {
        _log = log;
        _config = config;
    }

    private IReadOnlyList<StoredEvent> Events(DateTime now)
    {
        lock (_lock)
        {
            if (_events is null || _eventsVersion != _log.Version || now - _eventsAt > CacheFor)
            {
                _events = _log.ReadAll();
                _eventsVersion = _log.Version;
                _eventsAt = now;
                _engagement = null;
            }

            return _events;
        }
    }

    public MetricsReport Report(DateTime now, int windowDays = 30)
    {
        windowDays = Math.Clamp(windowDays, 1, 90);
        var from = now.Date.AddDays(-(windowDays - 1));
        var ev = Events(now).Where(e => e.At >= from).ToList();

        var rows = new Dictionary<string, (int Shown, int Clicks, int Plays, int Expanded)>(StringComparer.OrdinalIgnoreCase);
        var playsTotal = 0;
        var playsFromRows = 0;
        foreach (var e in ev)
        {
            if (e.Type == "playStarted")
            {
                playsTotal++;
            }

            if (string.IsNullOrEmpty(e.RowType))
            {
                continue;
            }

            rows.TryGetValue(e.RowType, out var r);
            switch (e.Type)
            {
                case "rowShown": r.Shown++; break;
                case "cardClicked": r.Clicks++; break;
                case "cardExpanded": r.Expanded++; break;
                case "playStarted": r.Plays++; playsFromRows++; break;
            }

            rows[e.RowType] = r;
        }

        var rowList = rows
            .Select(kv => new RowMetric(
                kv.Key,
                kv.Value.Shown,
                kv.Value.Clicks,
                kv.Value.Plays,
                kv.Value.Expanded,
                kv.Value.Shown == 0 ? 0 : Math.Round((double)(kv.Value.Clicks + kv.Value.Plays) / kv.Value.Shown, 4),
                kv.Value.Shown == 0 ? 0 : Math.Round((double)kv.Value.Plays / kv.Value.Shown, 4)))
            .OrderByDescending(r => r.TakeRate)
            .ThenBy(r => r.RowType, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var searches = ev.Where(e => e.Type == "searchIssued" && !string.IsNullOrEmpty(e.Query))
            .GroupBy(e => e.Query!.ToLowerInvariant())
            .Select(g => new SearchMetric(g.Key, g.Count(), g.Select(x => x.UserId).Distinct().Count()))
            .OrderByDescending(s => s.Count)
            .ThenBy(s => s.Query, StringComparer.Ordinal)
            .Take(TopSearchCount)
            .ToList();

        var perDay = Enumerable.Range(0, windowDays)
            .Select(i => from.AddDays(i))
            .Select(d => new DayCount(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ev.Count(e => e.At.Date == d)))
            .ToList();

        return new MetricsReport(
            _config.Current.CollectInteractionMetrics,
            now,
            windowDays,
            ev.Count,
            ev.Select(e => e.UserId).Distinct().Count(),
            rowList,
            rowList.Where(r => r.Shown > 0 && r.Clicks + r.Plays == 0).Select(r => r.RowType).ToList(),
            playsFromRows,
            playsTotal,
            searches,
            perDay);
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, double> GetEngagement(Guid userId)
    {
        if (!_config.Current.CollectInteractionMetrics)
        {
            return new Dictionary<string, double>();
        }

        var now = DateTime.UtcNow;
        var all = Events(now);
        lock (_lock)
        {
            _engagement ??= BuildEngagement(all, now);
            return _engagement.TryGetValue(userId, out var mine) ? new Dictionary<string, double>(mine) : new Dictionary<string, double>();
        }
    }

    /// <summary>
    /// Per user and row type: a smoothed take rate in 0..1, (clicks + plays + prior) / (shown + weight). With few
    /// impressions the score stays near the 0.1 prior, so one lucky click cannot reorder somebody's home screen.
    /// </summary>
    internal static Dictionary<Guid, Dictionary<string, double>> BuildEngagement(IReadOnlyList<StoredEvent> events, DateTime now)
    {
        const double priorRate = 0.1;
        const double priorWeight = 10;
        var cutoff = now.AddDays(-60);
        var acc = new Dictionary<(Guid, string), (int Shown, int Acted)>();
        foreach (var e in events)
        {
            if (e.At < cutoff || string.IsNullOrEmpty(e.RowType))
            {
                continue;
            }

            var key = (e.UserId, e.RowType);
            acc.TryGetValue(key, out var v);
            if (e.Type == "rowShown")
            {
                v.Shown++;
            }
            else if (e.Type is "cardClicked" or "playStarted")
            {
                v.Acted++;
            }
            else
            {
                continue;
            }

            acc[key] = v;
        }

        var result = new Dictionary<Guid, Dictionary<string, double>>();
        foreach (var ((user, row), v) in acc)
        {
            if (v.Shown == 0)
            {
                continue;
            }

            if (!result.TryGetValue(user, out var map))
            {
                result[user] = map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            }

            map[row] = Math.Round(Math.Min(1.0, ((v.Acted) + (priorRate * priorWeight)) / (v.Shown + priorWeight)), 4);
        }

        return result;
    }
}

/// <summary>
/// How much a user engages with each kind of home row, so the recommendation engine can order rows per user.
/// Row type ids are the ones clients send in <c>rowType</c> (continue, toppicks, because, top10, trending, ...).
/// </summary>
public interface IRowEngagementProvider
{
    /// <summary>Row type -> engagement score in 0..1 (higher = this user acts on that row more). Empty when there is no data or collection is off.</summary>
    IReadOnlyDictionary<string, double> GetEngagement(Guid userId);
}
