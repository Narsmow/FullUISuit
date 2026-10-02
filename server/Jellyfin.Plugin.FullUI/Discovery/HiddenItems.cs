using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Services;

namespace Jellyfin.Plugin.FullUI.Discovery;

/// <summary>
/// Titles a user removed from Continue Watching. The recommendation engine consults this so a hidden title stays out of
/// the Continue Watching row only (it can still appear in other rows, like on other streaming apps). Private per user.
/// </summary>
public interface IHiddenItems
{
    bool IsHidden(Guid userId, Guid itemId);

    /// <summary>A copy of the user's hidden item ids.</summary>
    IReadOnlySet<Guid> HiddenFor(Guid userId);
}

/// <summary>Lets this module tell the home screen cache that a user's data changed, without depending on the engine's classes.</summary>
public interface IHomeInvalidator
{
    void Invalidate(Guid userId);
}

public sealed class HomeServiceInvalidator : IHomeInvalidator
{
    private readonly HomeService _home;

    public HomeServiceInvalidator(HomeService home)
    {
        _home = home;
    }

    public void Invalidate(Guid userId) => _home.Invalidate(userId);
}

public sealed class HiddenItemsService : IHiddenItems
{
    public const int MaxHiddenPerUser = 1000;

    private readonly PluginStore _store;

    public HiddenItemsService(PluginStore store)
    {
        _store = store;
    }

    public bool IsHidden(Guid userId, Guid itemId) => _store.Read(d => d.HiddenContinue.Contains(StoreData.UserItemKey(userId, itemId)));

    public IReadOnlySet<Guid> HiddenFor(Guid userId)
    {
        var prefix = userId.ToString("N") + "|";
        return _store.Read(d => d.HiddenContinue
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Select(k => Guid.TryParse(k.AsSpan(prefix.Length), out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToHashSet());
    }

    /// <summary>Returns false when the user already hid the maximum number of titles.</summary>
    public bool Hide(Guid userId, Guid itemId)
    {
        var ok = true;
        var key = StoreData.UserItemKey(userId, itemId);
        var prefix = userId.ToString("N") + "|";
        _store.Write(d =>
        {
            if (d.HiddenContinue.Contains(key))
            {
                return;
            }

            if (d.HiddenContinue.Count(k => k.StartsWith(prefix, StringComparison.Ordinal)) >= MaxHiddenPerUser)
            {
                ok = false;
                return;
            }

            d.HiddenContinue.Add(key);
        });
        return ok;
    }

    public void Unhide(Guid userId, Guid itemId)
    {
        var key = StoreData.UserItemKey(userId, itemId);
        _store.Write(d => d.HiddenContinue.Remove(key));
    }
}
