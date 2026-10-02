using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullUI;

/// <summary>
/// Hooks the FullUI bundle into the Jellyfin web page after startup. The File Transformation plugin may load later than
/// this one (and only becomes usable once it has been constructed), so a few delayed attempts are made and every outcome is logged.
/// The latest result is visible on the plugin settings page (WebInjection.Status).
/// </summary>
public sealed class WebInjectionHostedService : IHostedService, IDisposable
{
    private static readonly TimeSpan[] DefaultDelays =
    {
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(3),
        TimeSpan.FromMinutes(10),
    };

    private readonly ILogger<WebInjectionHostedService> _log;
    private readonly Func<InjectionResult> _register;
    private readonly TimeSpan[] _delays;
    private CancellationTokenSource? _cts;
    private Task _loop = Task.CompletedTask;

    public WebInjectionHostedService(ILogger<WebInjectionHostedService> log)
        : this(log, WebInjection.TryRegister, DefaultDelays)
    {
    }

    internal WebInjectionHostedService(ILogger<WebInjectionHostedService> log, Func<InjectionResult> register, TimeSpan[] delays)
    {
        _log = log;
        _register = register;
        _delays = delays;
    }

    /// <summary>Completes when the retry loop has finished (registered, or all attempts used). For tests.</summary>
    public Task Completion => _loop;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _cts?.Cancel();
            await _loop.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Shutting down: nothing useful to report.
        }
    }

    public void Dispose() => _cts?.Dispose();

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            for (var attempt = 0; attempt <= _delays.Length; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(_delays[attempt - 1], ct).ConfigureAwait(false);
                }

                InjectionResult result;
                try
                {
                    result = _register();
                }
                catch (Exception ex)
                {
                    result = new InjectionResult(InjectionOutcome.Failed, "The FullUI screen is not active: hooking into the Jellyfin web page failed.", ex.GetType().Name);
                    WebInjection.Status.Record(false, result.Message);
                }

                if (result.Outcome == InjectionOutcome.Registered)
                {
                    _log.LogInformation("FullUI: web interface hooked in via File Transformation (attempt {Attempt})", attempt + 1);
                    return;
                }

                if (result.Outcome == InjectionOutcome.Incompatible || attempt == _delays.Length)
                {
                    _log.LogWarning("FullUI: web interface is NOT active. {Message} ({Detail})", result.Message, result.Technical ?? "no detail");
                    return;
                }

                _log.LogInformation("FullUI: web interface not hooked in yet (attempt {Attempt}); will retry. {Detail}", attempt + 1, result.Technical ?? result.Message);
            }
        }
        catch (OperationCanceledException)
        {
            // Jellyfin is shutting down.
        }
    }
}
