using System;
using System.Linq;
using Jellyfin.Plugin.FullUI.Discovery;
using Jellyfin.Plugin.FullUI.Library;
using Xunit;

namespace Jellyfin.Plugin.FullUI.DiscoveryTests;

public class CatalogCastIndexTests
{
    [Fact]
    public void ServesCastAndDirectorsFromTheCatalog_AndFindsThemInSuggestions()
    {
        var matrix = new CatalogItem
        {
            Id = Guid.NewGuid(), Kind = CatalogKind.Movie, Name = "The Matrix", Year = 1999,
            Cast = new[] { "Keanu Reeves", "Carrie-Anne Moss" }, Directors = new[] { "Lana Wachowski" },
        };
        var other = new CatalogItem { Id = Guid.NewGuid(), Kind = CatalogKind.Movie, Name = "Unrelated" };
        var catalog = new FakeCatalog { Items = { matrix, other } };
        var index = new CatalogCastIndex(catalog);

        Assert.Equal(new[] { "Keanu Reeves", "Carrie-Anne Moss", "Lana Wachowski" }, index.CastOf(matrix.Id));
        Assert.Empty(index.CastOf(other.Id));
        Assert.Empty(index.CastOf(Guid.NewGuid()));

        var people = new SuggestService(catalog, index).Suggest(Guid.NewGuid(), "keanu").People;
        Assert.Contains(people, p => p.Name == "Keanu Reeves" && p.ItemIds.Contains(matrix.Id.ToString("N")));
    }

    [Fact]
    public void RebuildsWhenTheCatalogSnapshotChanges()
    {
        var a = new CatalogItem { Id = Guid.NewGuid(), Kind = CatalogKind.Movie, Name = "A", Cast = new[] { "Old Actor" } };
        var catalog = new FakeCatalog { Items = { a } };
        var index = new CatalogCastIndex(catalog);
        Assert.Equal(new[] { "Old Actor" }, index.CastOf(a.Id));

        var a2 = new CatalogItem { Id = a.Id, Kind = CatalogKind.Movie, Name = "A", Cast = new[] { "New Actor" } };
        catalog.Items = new() { a2 };

        Assert.Equal(new[] { "New Actor" }, index.CastOf(a.Id));
    }
}
