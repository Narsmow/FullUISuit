using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.FullUI.Api;
using Jellyfin.Plugin.FullUI.Services;
using Xunit;

namespace Jellyfin.Plugin.FullUI.Tests;

public class RowOrderingTests
{
    private static HomeRow Row(string id, string type) => new(id, id, type, Array.Empty<ItemCard>());

    private static readonly IReadOnlyList<HomeRow> Rows = new[]
    {
        Row("continue", "continue"), Row("toppicks", "toppicks"), Row("because-1", "because"), Row("trending", "trending"),
        Row("mylist", "mylist"), Row("genre-drama", "genre"), Row("recent", "recent"),
    };

    [Fact]
    public void NoEngagementData_KeepsContractOrder()
    {
        Assert.Equal(Rows.Select(r => r.Id), RowOrdering.Apply(Rows, null).Select(r => r.Id));
        Assert.Equal(Rows.Select(r => r.Id), RowOrdering.Apply(Rows, new Dictionary<string, double>()).Select(r => r.Id));
    }

    [Fact]
    public void MoreEngagedRowsMoveUp_AnchorsNeverMove()
    {
        var engagement = new Dictionary<string, double> { ["recent"] = 0.9, ["trending"] = 0.6, ["because"] = 0.2, ["genre"] = 0.4 };

        var ids = RowOrdering.Apply(Rows, engagement).Select(r => r.Id).ToList();

        // Movable slots were [because-1, trending, genre-drama, recent] -> sorted by score: recent, trending, genre, because
        Assert.Equal(new[] { "continue", "toppicks", "recent", "trending", "mylist", "genre-drama", "because-1" }, ids);
    }

    [Fact]
    public void OnlyOneKnownKind_DoesNotReorder()
    {
        var ids = RowOrdering.Apply(Rows, new Dictionary<string, double> { ["recent"] = 0.9 }).Select(r => r.Id);
        Assert.Equal(Rows.Select(r => r.Id), ids);
    }
}
