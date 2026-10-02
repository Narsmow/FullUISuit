using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.FullUI.Compat;

/// <summary>
/// Jellyfin 10.11.6-10.11.8 expose <c>IUserManager.UsersIds</c> (property), while 10.11.11 removed it in favour of
/// <c>GetUsersIds()</c>. A plugin compiled against either form throws MissingMethodException on the other, so we bind at
/// runtime to whichever exists. Keep every user enumeration going through here.
/// </summary>
public static class UserManagerCompat
{
    private static readonly object Gate = new();
    private static Func<IUserManager, IEnumerable<Guid>>? _cached;
    private static Type? _cachedFor;

    public static IReadOnlyList<Guid> GetUserIds(IUserManager users)
    {
        var f = Resolve(users.GetType());
        return f(users).ToList();
    }

    private static Func<IUserManager, IEnumerable<Guid>> Resolve(Type runtimeType)
    {
        lock (Gate)
        {
            if (_cached is not null && _cachedFor == runtimeType)
            {
                return _cached;
            }

            // Look at the interface (what Jellyfin declares), not only the implementing class.
            var iface = typeof(IUserManager);
            var method = iface.GetMethod("GetUsersIds", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
            Func<IUserManager, IEnumerable<Guid>>? f = null;
            if (method is not null)
            {
                f = u => AsGuids(method.Invoke(u, null));
            }
            else
            {
                var prop = iface.GetProperty("UsersIds", BindingFlags.Public | BindingFlags.Instance);
                if (prop is not null)
                {
                    f = u => AsGuids(prop.GetValue(u));
                }
            }

            _cached = f ?? (_ => Array.Empty<Guid>());
            _cachedFor = runtimeType;
            return _cached;
        }
    }

    private static IEnumerable<Guid> AsGuids(object? value) =>
        value is IEnumerable e ? e.Cast<object>().Select(o => (Guid)o) : Array.Empty<Guid>();
}
