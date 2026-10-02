using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Discovery;
using Jellyfin.Plugin.FullUI.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Jellyfin.Plugin.FullUI.Tests.Kit;

namespace Jellyfin.Plugin.FullUI.Tests;

/// <summary>"Remove from Continue Watching" must reach the engine, and must only affect that row (streaming-app behaviour).</summary>
public class HiddenWiringTests
{
    private sealed class FakeHidden : IHiddenItems
    {
        public HashSet<Guid> Ids { get; } = new();

        public bool IsHidden(Guid userId, Guid itemId) => Ids.Contains(itemId);

        public IReadOnlySet<Guid> HiddenFor(Guid userId) => Ids.ToHashSet();
    }

    [Fact]
    public void HiddenTitle_LeavesContinueWatching_ButStaysInMyList()
    {
        using var ts = new TempStore();
        var half = Movie(1, "Half Watched", new[] { "Drama" }, runtime: 100);
        var catalog = new SimCatalog { Items = Many(10, 30, "Filler", "Drama").Append(half).ToList() };
        var me = Guid.NewGuid();
        ts.Store.Write(d =>
        {
            d.Signals.Add(new PlaySignal { UserId = me, ItemId = half.Id, At = DateTime.UtcNow.AddDays(-2), Completion = 0.5, Completed = false });
            d.MyList.Add(StoreData.UserItemKey(me, half.Id));
        });
        var hidden = new FakeHidden();
        var svc = new HomeService(ts.Store, catalog, NullLogger<HomeService>.Instance, new SimConfig(), hidden: hidden);

        var before = svc.GetHomeStrict(me);
        Assert.Contains(before.Rows.First(r => r.Id == "continue").Items, c => c.Name == "Half Watched");

        hidden.Ids.Add(half.Id);
        svc.Invalidate(me);
        var after = svc.GetHomeStrict(me);

        Assert.DoesNotContain(after.Rows, r => r.Id == "continue" && r.Items.Any(c => c.Name == "Half Watched"));
        Assert.Contains(after.Rows.First(r => r.Id == "mylist").Items, c => c.Name == "Half Watched");
    }

    [Fact]
    public void HiddenTitle_DoesNotStopItSeedingBecauseYouWatched()
    {
        using var ts = new TempStore();
        var seed = Movie(1, "Loved It", new[] { "Action" }, runtime: 100);
        var catalog = new SimCatalog { Items = Many(10, 30, "Act", "Action").Append(seed).ToList() };
        var me = Guid.NewGuid();
        ts.Store.Write(d => d.Signals.Add(new PlaySignal { UserId = me, ItemId = seed.Id, At = DateTime.UtcNow.AddDays(-3), Completion = 1, Completed = true }));
        var hidden = new FakeHidden();
        hidden.Ids.Add(seed.Id);
        var svc = new HomeService(ts.Store, catalog, NullLogger<HomeService>.Instance, new SimConfig(), hidden: hidden);

        var home = svc.GetHomeStrict(me);

        Assert.Contains(home.Rows, r => r.Id.StartsWith("because-", StringComparison.Ordinal) && r.Title == "Because you watched Loved It");
    }
}
