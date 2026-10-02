using Jellyfin.Plugin.FullUI.Compat;
using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.FullUI.Discovery;

public interface IUserDirectory
{
    IReadOnlyList<Guid> UserIds { get; }

    /// <summary>User name for display in the admin UI, or null when the user no longer exists.</summary>
    string? NameOf(Guid userId);
}

public sealed class JellyfinUserDirectory : IUserDirectory
{
    private readonly IUserManager _users;

    public JellyfinUserDirectory(IUserManager users)
    {
        _users = users;
    }

    public IReadOnlyList<Guid> UserIds => UserManagerCompat.GetUserIds(_users);

    public string? NameOf(Guid userId)
    {
        var u = _users.GetUserById(userId);
        return u?.Username;
    }
}
