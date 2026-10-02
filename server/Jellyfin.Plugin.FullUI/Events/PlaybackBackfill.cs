using Jellyfin.Plugin.FullUI.Compat;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullUI.Events;

/// <summary>One title's existing Jellyfin watch state for one user (movie, or an episode that rolls up to its series).</summary>
public sealed record WatchRecord(
    Guid ItemId,
    Guid? SeriesId,
    bool IsEpisode,
    long? RuntimeTicks,
    long PositionTicks,
    bool Played,
    bool IsFavorite,
    DateTime? LastPlayed,
    int? Season = null,
    int? Episode = null);

/// <summary>
/// One-time import of what Jellyfin already knows (played, resume position, favorites) so a fresh install does not
/// start cold: Continue Watching, "Because you watched" and Top Picks work from day one.
/// </summary>
public static class PlaybackBackfill
{
    /// <summary>Signals kept per (user, title). Enough for the engine; stops a 500-episode series flooding the store.</summary>
    public const int MaxSignalsPerTitle = 3;

    /// <summary>Adds signals/ratings for one user and marks them as imported. Returns the number of signals added. Idempotent.</summary>
    public static int Apply(StoreData d, Guid userId, IEnumerable<WatchRecord> records, DateTime now)
    {
        var added = 0;
        var key = userId.ToString("N");
        if (d.BackfilledUsers.Contains(key))
        {
            return 0;
        }

        var fresh = new List<PlaySignal>();
        foreach (var r in records)
        {
            if (r.IsFavorite && !r.IsEpisode)
            {
                var rk = StoreData.UserItemKey(userId, r.ItemId);
                if (!d.Ratings.ContainsKey(rk))
                {
                    d.Ratings[rk] = 2; // a Jellyfin favorite is what "Love" maps to in FullUI
                }
            }

            if (!r.Played && r.PositionTicks <= 0)
            {
                continue;
            }

            var position = r.Played && r.RuntimeTicks is long rt && rt > 0 ? rt : r.PositionTicks;
            var sig = EventTracker.BuildSignal(
                userId,
                r.ItemId,
                r.SeriesId,
                r.IsEpisode,
                r.RuntimeTicks,
                position,
                r.Played,
                r.LastPlayed ?? now.AddDays(-30),
                r.Season,
                r.Episode);
            if (sig is not null)
            {
                fresh.Add(sig);
            }
        }

        // Keep the newest few per title (an in-progress signal is always the newest if it matters).
        foreach (var grp in fresh.GroupBy(s => s.ItemId))
        {
            foreach (var s in grp.OrderByDescending(s => s.At).Take(MaxSignalsPerTitle))
            {
                d.Signals.Add(s);
                added++;
            }
        }

        d.BackfilledUsers.Add(key);
        return added;
    }
}

/// <summary>Runs <see cref="PlaybackBackfill"/> shortly after startup (the library may still be loading) for every user not yet imported.</summary>
public sealed class PlaybackBackfillService : IHostedService
{
    private static readonly BaseItemKind[] Played = { BaseItemKind.Movie, BaseItemKind.Episode };
    private static readonly BaseItemKind[] Titles = { BaseItemKind.Movie, BaseItemKind.Series };

    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IUserDataManager _userData;
    private readonly PluginStore _store;
    private readonly HomeService _home;
    private readonly ILogger<PlaybackBackfillService> _log;
    private readonly Ops.ITaskRunLog? _runs;
    private CancellationTokenSource? _cts;

    /// <summary>Key of this service in the health page's task list.</summary>
    public const string RunKey = "FullUIPlaybackBackfill";
    public const string RunName = "Import existing watch history";

    public PlaybackBackfillService(
        ILibraryManager library,
        IUserManager users,
        IUserDataManager userData,
        PluginStore store,
        HomeService home,
        ILogger<PlaybackBackfillService> log,
        Ops.ITaskRunLog? runs = null)
    {
        _library = library;
        _users = users;
        _userData = userData;
        _store = store;
        _home = home;
        _log = log;
        _runs = runs;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(90), ct).ConfigureAwait(false);
                while (!ct.IsCancellationRequested)
                {
                    RunOnce();
                    await Task.Delay(TimeSpan.FromHours(6), ct).ConfigureAwait(false); // picks up users created later
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "FullUI: importing existing watch history stopped");
            }
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>Imports every user that has not been imported yet. Never throws. Each run is recorded for the health page.</summary>
    public void RunOnce()
    {
        var start = DateTime.UtcNow;
        try
        {
            var pending = UserManagerCompat.GetUserIds(_users)
                .Where(id => !_store.Read(d => d.BackfilledUsers.Contains(id.ToString("N"))))
                .ToList();
            var imported = 0;
            var records = 0;
            foreach (var userId in pending)
            {
                try
                {
                    var found = Collect(userId);
                    var added = 0;
                    _store.Write(d => added = PlaybackBackfill.Apply(d, userId, found, DateTime.UtcNow));
                    _home.Invalidate(userId);
                    imported++;
                    records += added;
                    _log.LogInformation("FullUI: imported {Count} existing watch records for one user", added);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "FullUI: could not import existing watch history for one user; will try again later");
                }
            }

            var failed = pending.Count - imported;
            if (failed > 0)
            {
                Record(start, Ops.TaskOutcome.Problem, $"{failed} of {pending.Count} user(s) could not be imported. It will try again in a few hours.");
            }
            else
            {
                Record(start, Ops.TaskOutcome.Success, pending.Count == 0 ? "Nothing new to import." : $"Imported {records} watch record(s) for {imported} user(s).");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "FullUI: importing existing watch history failed");
            Record(start, Ops.TaskOutcome.Failed, "Importing existing watch history failed. It will try again in a few hours.");
        }
    }

    private void Record(DateTime start, string outcome, string message) => _runs?.Record(RunKey, RunName, start, DateTime.UtcNow, outcome, message);

    private List<WatchRecord> Collect(Guid userId)
    {
        var user = _users.GetUserById(userId) ?? throw new InvalidOperationException("user not found");
        var seen = new HashSet<Guid>();
        var records = new List<WatchRecord>();

        void Add(BaseItem item, bool favoriteOnly)
        {
            if (!seen.Add(item.Id))
            {
                return;
            }

            var data = _userData.GetUserData(user, item);
            var ep = item as Episode;
            records.Add(new WatchRecord(
                item.Id,
                ep?.SeriesId,
                ep is not null,
                item.RunTimeTicks,
                favoriteOnly ? 0 : data?.PlaybackPositionTicks ?? 0,
                !favoriteOnly && (data?.Played ?? false),
                data?.IsFavorite ?? false,
                data?.LastPlayedDate,
                ep?.ParentIndexNumber,
                ep?.IndexNumber));
        }

        foreach (var item in _library.GetItemList(new InternalItemsQuery(user) { IncludeItemTypes = Played, Recursive = true, IsPlayed = true, IsVirtualItem = false }))
        {
            Add(item, false);
        }

        foreach (var item in _library.GetItemList(new InternalItemsQuery(user) { IncludeItemTypes = Played, Recursive = true, IsResumable = true, IsVirtualItem = false }))
        {
            Add(item, false);
        }

        foreach (var item in _library.GetItemList(new InternalItemsQuery(user) { IncludeItemTypes = Titles, Recursive = true, IsFavorite = true }))
        {
            if (seen.Contains(item.Id))
            {
                // Already added as played: just make sure its favorite flag is carried.
                var idx = records.FindIndex(r => r.ItemId == item.Id);
                if (idx >= 0)
                {
                    records[idx] = records[idx] with { IsFavorite = true };
                }

                continue;
            }

            Add(item, true);
        }

        return records;
    }
}
