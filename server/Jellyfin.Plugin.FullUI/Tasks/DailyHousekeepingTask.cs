using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FullUI.Discovery;
using Jellyfin.Plugin.FullUI.Metrics;
using Jellyfin.Plugin.FullUI.Ops;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullUI.Tasks;

/// <summary>Daily: sends "it is out today" reminders and drops usage statistics older than 90 days.</summary>
public class DailyHousekeepingTask : IScheduledTask
{
    public const string TaskKey = "FullUIDaily";

    private readonly ReminderService _reminders;
    private readonly InteractionLog _events;
    private readonly ITaskRunLog _runs;
    private readonly ILogger<DailyHousekeepingTask> _log;

    public DailyHousekeepingTask(ReminderService reminders, InteractionLog events, ITaskRunLog runs, ILogger<DailyHousekeepingTask> log)
    {
        _reminders = reminders;
        _events = events;
        _runs = runs;
        _log = log;
    }

    public string Name => "FullUI: Send reminders and tidy up";

    public string Key => TaskKey;

    public string Description => "Sends 'out today' reminders for titles people asked to be reminded about and removes old usage statistics.";

    public string Category => "FullUI";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var sent = 0;
        var dropped = 0;
        var problems = 0;
        try
        {
            sent = _reminders.Process(DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            problems++;
            _log.LogWarning(ex, "FullUI: sending reminders failed");
        }

        progress.Report(60);
        try
        {
            dropped = _events.Prune(DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            problems++;
            _log.LogWarning(ex, "FullUI: tidying usage statistics failed");
        }

        _runs.Note(
            TaskKey,
            problems > 0 ? "Part of the clean-up failed. The server log has the details." : $"{sent} reminder(s) sent, {dropped} old usage line(s) removed.",
            problems > 0);
        progress.Report(100);
        return Task.CompletedTask;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(6).Ticks };
    }
}
