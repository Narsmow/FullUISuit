using System;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.FullUI.Compat;
using MediaBrowser.Controller.Library;
using Xunit;

namespace Jellyfin.Plugin.FullUI.ApiTests;

public class UserManagerCompatTests
{
    /// <summary>Answers whichever user-id member the compiled interface declares (UsersIds on 10.11.6-8, GetUsersIds later).</summary>
    public class Proxy : DispatchProxy
    {
        public Guid[] Ids { get; set; } = Array.Empty<Guid>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name is "get_UsersIds" or "GetUsersIds"
                ? Ids.AsEnumerable()
                : targetMethod?.ReturnType.IsValueType == true ? Activator.CreateInstance(targetMethod.ReturnType) : null;
    }

    [Fact]
    public void ReturnsUserIdsRegardlessOfWhichJellyfinApiShapeIsCompiledAgainst()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var users = DispatchProxy.Create<IUserManager, Proxy>();
        ((Proxy)(object)users).Ids = ids;

        Assert.Equal(ids, UserManagerCompat.GetUserIds(users));
    }

    [Fact]
    public void EmptyWhenNoUsers()
    {
        var users = DispatchProxy.Create<IUserManager, Proxy>();
        Assert.Empty(UserManagerCompat.GetUserIds(users));
    }
}
