using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FullUI.Library;
using Jellyfin.Plugin.FullUI.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullUI.Tasks;

/// <summary>Daily: drops all recommendation caches and re-warms every user's home.</summary>
public sealed class RebuildRecsTask : IScheduledTask
{
    private readonly ICatalog _catalog;
    private readonly HomeService _home;
    private readonly IUserManager _users;
    private readonly ILogger<RebuildRecsTask> _log;

    public RebuildRecsTask(ICatalog catalog, HomeService home, IUserManager users, ILogger<RebuildRecsTask> log)
    {
        _catalog = catalog;
        _home = home;
        _users = users;
        _log = log;
    }

    public string Name => "Rebuild FullUI recommendations";

    public string Key => "FullUIRebuildRecs";

    public string Description => "Refreshes the library snapshot and every user's personalized home rows.";

    public string Category => "FullUI";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        List<Guid> users;
        try
        {
            _catalog.InvalidateNow();
            _home.Invalidate();
            _ = _catalog.All;
            users = _users.GetUsersIds().ToList();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FullUI: recommendation rebuild could not start; will retry at the next run");
            return Task.CompletedTask;
        }

        for (var i = 0; i < users.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                _home.GetHome(users[i]);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "FullUI: warming home for {User} failed", users[i]);
            }

            progress.Report(100.0 * (i + 1) / users.Count);
        }

        return Task.CompletedTask;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(4).Ticks,
        };
    }
}
