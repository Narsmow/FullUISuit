using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.FullUI.Ai;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Discovery;
using Jellyfin.Plugin.FullUI.Library;
using Jellyfin.Plugin.FullUI.Metrics;

namespace Jellyfin.Plugin.FullUI.Ops;

public sealed record ServiceStatus(bool Enabled, string Status, string Color, string Message);

public sealed record TaskStatusRow(string Key, string Name, DateTime? LastStartUtc, DateTime? LastEndUtc, string Outcome, string Color, string Status, string Message);

public sealed record FileInfoRow(string Name, long Bytes, int Count);

public sealed record HealthReport(
    string Overall,
    string Summary,
    string PluginVersion,
    string SupportedJellyfin,
    int StoreFormat,
    ServiceStatus Injection,
    ServiceStatus Tmdb,
    ServiceStatus Ollama,
    int CatalogItems,
    IReadOnlyList<TaskStatusRow> Tasks,
    IReadOnlyList<FileInfoRow> Files,
    IReadOnlyDictionary<string, int> Counts,
    IReadOnlyList<FriendlyError> RecentErrors,
    DateTime GeneratedAt);

/// <summary>Everything the admin "FullUI Health" page shows, in plain English with a colour per line. Never throws; never contains secrets.</summary>
public sealed class HealthService
{
    public const string SupportedJellyfin = "10.11.6 and later";

    /// <summary>Tasks that exist even if they never ran, so the page can say "has not run yet" instead of hiding them.</summary>
    internal static readonly (string Key, string Name, bool Daily)[] KnownTasks =
    {
        ("FullUIRebuildRecs", "Rebuild FullUI recommendations", true),
        ("FullUIDiscoverUpcoming", "Discover upcoming titles", true),
        ("FullUIBuildEmbeddings", "Build AI index", true),
        ("FullUISyncRequests", "Sync requested titles", false),
        ("FullUIDaily", "Send reminders and tidy up", true),
        ("FullUIMaterializePlaylists", "Write recommendation playlists", true),
        (Events.EventTracker.RunKey, Events.EventTracker.RunName, false),
        (Events.PlaybackBackfillService.RunKey, Events.PlaybackBackfillService.RunName, false),
    };

    private readonly PluginStore _store;
    private readonly ICatalog _catalog;
    private readonly ITaskRunLog _runs;
    private readonly IConfigSource _config;
    private readonly ITmdbClient _tmdb;
    private readonly IOllamaClient _ollama;
    private readonly InteractionLog _events;

    public HealthService(PluginStore store, ICatalog catalog, ITaskRunLog runs, IConfigSource config, ITmdbClient tmdb, IOllamaClient ollama, InteractionLog events)
    {
        _store = store;
        _catalog = catalog;
        _runs = runs;
        _config = config;
        _tmdb = tmdb;
        _ollama = ollama;
        _events = events;
    }

    public HealthReport Build(DateTime now)
    {
        var cfg = _config.Current;
        var injection = Describe(() =>
        {
            var s = WebInjection.Status;
            return s.Registered
                ? new ServiceStatus(true, "Working", "green", "The FullUI home screen is hooked into the Jellyfin web page.")
                : new ServiceStatus(true, "Not active", "red", s.Message);
        });

        var tmdb = Describe(() =>
        {
            if (!_tmdb.Configured)
            {
                return new ServiceStatus(false, "Not set up", "grey", "No TMDB key entered. Coming Soon, trailers and requests need one (free from themoviedb.org).");
            }

            return _tmdb is TmdbClient { KeyRejected: true }
                ? new ServiceStatus(true, "Key rejected", "red", "TMDB refused the key. Copy it again from themoviedb.org/settings/api and press Test in the FullUI settings.")
                : new ServiceStatus(true, "Set up", "green", "A TMDB key is saved. Press Test in the FullUI settings to check it.");
        });

        var ollama = Describe(() => !cfg.OllamaEnabled
            ? new ServiceStatus(false, "Off", "grey", "AI search is switched off. Normal keyword search works.")
            : _ollama.Enabled
                ? new ServiceStatus(true, "On", "green", "AI search is on. Press Test in the FullUI settings to check the connection.")
                : new ServiceStatus(true, "Needs attention", "amber", "AI search is on but no address is set."));

        var catalogItems = 0;
        try
        {
            catalogItems = _catalog.All.Count;
        }
        catch (Exception)
        {
            ErrorLog.Add("The library list could not be read.");
        }

        var tasks = TaskRows(now);
        var (counts, store, emb) = Counts();
        var files = new List<FileInfoRow>
        {
            new("store.json", store.Bytes, store.Count),
            new("embeddings.json", emb.Bytes, emb.Count),
            new("events.jsonl", SafeLong(() => _events.SizeBytes), counts.GetValueOrDefault("events")),
        };

        var recent = ErrorLog.Recent();
        var problems = new List<string>();
        if (injection.Color == "red")
        {
            problems.Add("the FullUI home screen is not hooked in");
        }

        if (tmdb.Color == "red")
        {
            problems.Add("the TMDB key was refused");
        }

        problems.AddRange(tasks.Where(t => t.Color == "red").Select(t => $"\"{t.Name}\" failed"));
        var attention = tasks.Count(t => t.Color == "amber") + (ollama.Color == "amber" ? 1 : 0);
        var overall = problems.Count > 0 ? "problem" : attention > 0 ? "attention" : "ok";
        var summary = overall == "ok"
            ? "Everything looks fine."
            : problems.Count > 0 ? "Needs a look: " + string.Join("; ", problems) + "." : "Mostly fine, but a few things deserve a look (shown in yellow below).";

        return new HealthReport(
            overall,
            summary,
            typeof(Plugin).Assembly.GetName().Version?.ToString(3) ?? "unknown",
            SupportedJellyfin,
            StoreMigrations.CurrentVersion,
            injection,
            tmdb,
            ollama,
            catalogItems,
            tasks,
            files,
            counts,
            recent,
            now);
    }

    private IReadOnlyList<TaskStatusRow> TaskRows(DateTime now)
    {
        var latest = SafeList(() => _runs.Latest()).ToDictionary(r => r.Key, r => r, StringComparer.Ordinal);
        var rows = new List<TaskStatusRow>();
        foreach (var (key, name, daily) in KnownTasks)
        {
            latest.TryGetValue(key, out var r);
            rows.Add(Row(key, name, daily, r, now));
            latest.Remove(key);
        }

        // Anything else recorded (the engine's own services, future tasks).
        foreach (var r in latest.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(Row(r.Key, r.Name, false, r, now));
        }

        return rows;
    }

    internal static TaskStatusRow Row(string key, string name, bool daily, TaskRunRecord? r, DateTime now)
    {
        if (r is null)
        {
            return new TaskStatusRow(key, name, null, null, "Never", "grey", "Has not run yet", string.Empty);
        }

        var (color, status) = r.Outcome switch
        {
            TaskOutcome.Success => ("green", "Worked"),
            TaskOutcome.Problem => ("amber", "Finished, but something went wrong"),
            TaskOutcome.Cancelled => ("amber", "Was stopped before it finished"),
            _ => ("red", "Failed"),
        };
        if (color == "green" && daily && now - r.EndUtc > TimeSpan.FromDays(3))
        {
            (color, status) = ("amber", "Has not run for a few days");
        }

        return new TaskStatusRow(key, name, r.StartUtc, r.EndUtc, r.Outcome, color, status, r.Message);
    }

    private (IReadOnlyDictionary<string, int> Counts, (long Bytes, int Count) Store, (long Bytes, int Count) Emb) CountsCore()
    {
        var (storeBytes, embBytes) = _store.FileSizes();
        var embCount = 0;
        try
        {
            embCount = _store.ReadEmbeddings(e => e.Count);
        }
        catch (Exception)
        {
            // unreadable: shown as 0
        }

        var events = 0;
        try
        {
            events = _events.ReadAll().Count;
        }
        catch (Exception)
        {
        }

        var counts = _store.Read(d => new Dictionary<string, int>
        {
            ["users"] = d.Ratings.Keys.Select(k => k.Length > 32 ? k[..32] : k).Concat(d.MyList.Select(k => k.Length > 32 ? k[..32] : k)).Concat(d.Signals.Select(s => s.UserId.ToString("N"))).Distinct().Count(),
            ["signals"] = d.Signals.Count,
            ["ratings"] = d.Ratings.Count,
            ["myList"] = d.MyList.Count,
            ["votes"] = d.Votes.Count,
            ["requests"] = d.Statuses.Count,
            ["notifications"] = d.Notifications.Count,
            ["reminders"] = d.Reminders.Count,
            ["hiddenContinue"] = d.HiddenContinue.Count,
            ["onboarding"] = d.Onboarding.Count,
            ["embeddings"] = embCount,
            ["events"] = events,
        });
        var storeCount = counts["signals"] + counts["ratings"] + counts["myList"] + counts["votes"] + counts["notifications"] + counts["reminders"];
        return (counts, (storeBytes, storeCount), (embBytes, embCount));
    }

    private (IReadOnlyDictionary<string, int> Counts, (long Bytes, int Count) Store, (long Bytes, int Count) Emb) Counts()
    {
        try
        {
            return CountsCore();
        }
        catch (Exception)
        {
            ErrorLog.Add("The stored data could not be counted.");
            return (new Dictionary<string, int>(), (0, 0), (0, 0));
        }
    }

    private static long SafeLong(Func<long> f)
    {
        try
        {
            return f();
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static IReadOnlyList<T> SafeList<T>(Func<IReadOnlyList<T>> f)
    {
        try
        {
            return f();
        }
        catch (Exception)
        {
            return Array.Empty<T>();
        }
    }

    private static ServiceStatus Describe(Func<ServiceStatus> f)
    {
        try
        {
            return f();
        }
        catch (Exception)
        {
            return new ServiceStatus(false, "Unknown", "grey", "Could not check this.");
        }
    }
}
