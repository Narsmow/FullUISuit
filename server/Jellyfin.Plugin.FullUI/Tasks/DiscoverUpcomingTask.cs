using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FullUI.Discovery;
using Microsoft.Extensions.Logging;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.FullUI.Tasks;

public class DiscoverUpcomingTask : IScheduledTask
{
    private readonly ComingSoonService _service;
    private readonly ILogger<DiscoverUpcomingTask> _log;
    private readonly Ops.ITaskRunLog? _runs;

    public DiscoverUpcomingTask(ComingSoonService service, ILogger<DiscoverUpcomingTask> log, Ops.ITaskRunLog? runs = null)
    {
        _service = service;
        _log = log;
        _runs = runs;
    }

    public string Name => "FullUI: Discover upcoming titles";

    public string Key => "FullUIDiscoverUpcoming";

    public string Description => "Builds per-user Coming Soon lists and trailer keys from TMDB.";

    public string Category => "FullUI";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        try
        {
            await _service.RunAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "FullUI: discovering upcoming titles failed; Coming Soon stays as it was");
            _runs?.Note(Key, "Could not refresh Coming Soon (is TMDB reachable and the key valid?). The old list stays.", problem: true);
        }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(3).Ticks };
    }
}
