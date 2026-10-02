using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullUI.Data;

/// <summary>
/// Thread-safe JSON-file store with debounced atomic saves. Chosen over SQLite to avoid shipping a native
/// dependency inside a plugin load context; fine for household-scale data.
/// </summary>
public sealed class PluginStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly object _lock = new();
    private readonly string _path;
    private readonly ILogger<PluginStore> _log;
    private readonly Timer _saveTimer;
    private StoreData _data = new();
    private bool _dirty;

    public PluginStore(IApplicationPaths paths, ILogger<PluginStore> log)
    {
        _log = log;
        var dir = Path.Combine(paths.DataPath, "fullui");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "store.json");
        _saveTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        Load();
    }

    /// <summary>Run a read-only function under the lock. Do not leak mutable collections out of it.</summary>
    public T Read<T>(Func<StoreData, T> f)
    {
        lock (_lock)
        {
            return f(_data);
        }
    }

    /// <summary>Mutate under the lock; schedules a debounced save.</summary>
    public void Write(Action<StoreData> f)
    {
        lock (_lock)
        {
            f(_data);
            _dirty = true;
        }

        _saveTimer.Change(TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
    }

    public void Flush()
    {
        string? text = null;
        lock (_lock)
        {
            if (!_dirty)
            {
                return;
            }

            var cutoff = DateTime.UtcNow.AddDays(-400);
            _data.Signals.RemoveAll(s => s.At < cutoff);
            text = JsonSerializer.Serialize(_data, Json);
            _dirty = false;
        }

        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FullUI: failed to save store");
            lock (_lock)
            {
                _dirty = true;
            }
        }
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        Flush();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                _data = JsonSerializer.Deserialize<StoreData>(File.ReadAllText(_path), Json) ?? new StoreData();
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FullUI: store unreadable, starting empty (old file kept as .bad)");
            try
            {
                File.Move(_path, _path + ".bad", overwrite: true);
            }
            catch (IOException)
            {
            }

            _data = new StoreData();
        }
    }
}
