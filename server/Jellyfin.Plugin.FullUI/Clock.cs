using System;

namespace Jellyfin.Plugin.FullUI;

/// <summary>
/// The "current time" for request handling. Always the real UTC clock in production; the contract-fixture test pins it so the
/// JSON it writes is identical on every run.
/// </summary>
internal static class Clock
{
    private static Func<DateTime> _source = () => DateTime.UtcNow;

    public static DateTime UtcNow => _source();

    /// <summary>Test hook: pins the clock until the returned object is disposed.</summary>
    internal static IDisposable Fix(DateTime utc)
    {
        var previous = _source;
        _source = () => utc;
        return new Restore(previous);
    }

    private sealed class Restore : IDisposable
    {
        private readonly Func<DateTime> _previous;

        public Restore(Func<DateTime> previous) => _previous = previous;

        public void Dispose() => _source = _previous;
    }
}
