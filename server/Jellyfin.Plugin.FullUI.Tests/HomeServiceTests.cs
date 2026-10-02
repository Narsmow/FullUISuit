using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Library;
using Jellyfin.Plugin.FullUI.Services;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.FullUI.Tests;

public class HomeServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fullui-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Minimal IApplicationPaths: strings become the temp dir, everything else its default.</summary>
    public class PathsProxy : DispatchProxy
    {
        public string Dir { get; set; } = string.Empty;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.ReturnType == typeof(string) ? Dir : null;
    }

    private PluginStore NewStore()
    {
        var paths = DispatchProxy.Create<IApplicationPaths, PathsProxy>();
        ((PathsProxy)(object)paths).Dir = _dir;
        return new PluginStore(paths, NullLogger<PluginStore>.Instance);
    }

    private sealed class ThrowingCatalog : ICatalog
    {
        public IReadOnlyList<CatalogItem> All => throw new InvalidOperationException("library offline");

        public IReadOnlySet<Guid> VisibleTo(Guid userId) => throw new InvalidOperationException("library offline");

        public void Invalidate()
        {
        }

        public event EventHandler? Changed { add { } remove { } }
    }

    private sealed class EmptyCatalog : ICatalog
    {
        public IReadOnlyList<CatalogItem> All => Array.Empty<CatalogItem>();

        public IReadOnlySet<Guid> VisibleTo(Guid userId) => new HashSet<Guid>();

        public void Invalidate()
        {
        }

        public event EventHandler? Changed { add { } remove { } }
    }

    [Fact]
    public void GetHome_WhenLibraryFails_ReturnsEmptyHomeInsteadOfThrowing()
    {
        using var store = NewStore();
        var svc = new HomeService(store, new ThrowingCatalog(), NullLogger<HomeService>.Instance);

        var home = svc.GetHome(Guid.NewGuid());

        Assert.NotNull(home);
        Assert.Empty(home.Rows);
        Assert.False(string.IsNullOrWhiteSpace(home.ServerName));       // works without plugin config loaded
        Assert.Null(svc.GetItem(Guid.NewGuid(), Guid.NewGuid()));
        var my = svc.GetMyServer(Guid.NewGuid());
        Assert.Empty(my.ContinueWatching);
        Assert.Empty(my.Wanted);
    }

    [Fact]
    public void GetHome_WithEmptyStoreAndLibrary_IsEmptyNotAnError()
    {
        using var store = NewStore();
        var svc = new HomeService(store, new EmptyCatalog(), NullLogger<HomeService>.Instance);

        Assert.Empty(svc.GetHome(Guid.NewGuid()).Rows);
        Assert.Null(svc.GetItem(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public void MyServer_ListsOnlyTheUsersOwnWantVotes()
    {
        using var store = NewStore();
        var me = Guid.NewGuid();
        store.Write(d =>
        {
            d.Votes.Add(new VoteEntry { UserId = me, TmdbId = 1, Vote = 1, Title = "Wanted", At = DateTime.UtcNow });
            d.Votes.Add(new VoteEntry { UserId = me, TmdbId = 2, Vote = -1, Title = "No thanks", At = DateTime.UtcNow });
            d.Votes.Add(new VoteEntry { UserId = Guid.NewGuid(), TmdbId = 3, Vote = 1, Title = "Someone else", At = DateTime.UtcNow });
        });
        var svc = new HomeService(store, new EmptyCatalog(), NullLogger<HomeService>.Instance);

        var wanted = svc.GetMyServer(me).Wanted;

        Assert.Single(wanted);
        Assert.Equal("Wanted", wanted[0].Title);
        Assert.Equal(1, wanted[0].MyVote);
    }
}
