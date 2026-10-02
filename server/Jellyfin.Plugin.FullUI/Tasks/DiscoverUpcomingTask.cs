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

    public DiscoverUpcomingTask(ComingSoonService service, ILogger<DiscoverUpcomingTask> log)
    {
        _service = service;
        _log = log;
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
        }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(3).Ticks };
    }
}
