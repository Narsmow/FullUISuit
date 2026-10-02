using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.FullUI.Data;

/// <summary>
/// Versioned upgrades of <c>store.json</c>. A file with no <see cref="StoreData.Version"/> (everything written before
/// versioning existed) is version 1. Each step upgrades from version N to N+1 in memory; the store is saved afterwards.
/// Add a step here (and bump <see cref="CurrentVersion"/>) whenever a change cannot be expressed as "a new collection
/// that defaults to empty".
/// </summary>
public static class StoreMigrations
{
    public const int CurrentVersion = 2;

    private static readonly (int From, string What, Action<StoreData> Apply)[] Steps =
    {
        (1, "version 2: reminders, onboarding, hidden titles, task runs", Version2),
    };

    /// <summary>Upgrades <paramref name="data"/> in place. Returns what happened so the caller can log it.</summary>
    public static MigrationResult Migrate(StoreData data)
    {
        var from = data.Version <= 0 ? 1 : data.Version;
        if (from > CurrentVersion)
        {
            // Written by a newer FullUI. Leave it alone; the caller keeps a backup copy before ever saving.
            EnsureCollections(data);
            return new MigrationResult(from, from, Changed: false, FromFuture: true, Applied: Array.Empty<string>());
        }

        var applied = new List<string>();
        foreach (var step in Steps.Where(s => s.From >= from).OrderBy(s => s.From))
        {
            step.Apply(data);
            applied.Add(step.What);
        }

        EnsureCollections(data);
        var changed = data.Version != CurrentVersion;
        data.Version = CurrentVersion;
        return new MigrationResult(from, CurrentVersion, changed, FromFuture: false, applied);
    }

    private static void Version2(StoreData d)
    {
        // Nothing to convert: version 2 only adds collections. (Kept as a real step so the framework is exercised.)
        EnsureCollections(d);
    }

    /// <summary>A hand-edited or damaged file can contain explicit nulls; never let a null collection reach the rest of the code.</summary>
    internal static void EnsureCollections(StoreData d)
    {
        d.Signals ??= new();
        d.Ratings ??= new();
        d.MyList ??= new();
        d.Votes ??= new();
        d.Statuses ??= new();
        d.Notifications ??= new();
        d.ComingSoon ??= new();
        d.TrailerKeys ??= new();
        d.BackfilledUsers ??= new();
        d.RowTitles ??= new();
        d.Reminders ??= new();
        d.Onboarding ??= new();
        d.HiddenContinue ??= new();
        d.TaskRuns ??= new();
        d.RowTitleStamps ??= new();
    }
}

public sealed record MigrationResult(int From, int To, bool Changed, bool FromFuture, IReadOnlyList<string> Applied);
