using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.FullUI.Library;

namespace Jellyfin.Plugin.FullUI.Discovery;

/// <summary>
/// Real <see cref="ICastIndex"/>: serves the actors and directors the catalog already loaded, so search and typeahead can
/// find "Keanu Reeves" or "Nolan". Rebuilt only when the catalog snapshot changes.
/// </summary>
public sealed class CatalogCastIndex : ICastIndex
{
    private readonly ICatalog _catalog;
    private readonly object _gate = new();
    private IReadOnlyList<CatalogItem>? _builtFor;
    private Dictionary<Guid, IReadOnlyList<string>> _map = new();

    public CatalogCastIndex(ICatalog catalog)
    {
        _catalog = catalog;
    }

    public IReadOnlyList<string> CastOf(Guid itemId)
    {
        try
        {
            var all = _catalog.All;
            lock (_gate)
            {
                if (!ReferenceEquals(all, _builtFor))
                {
                    _map = all.ToDictionary(
                        c => c.Id,
                        c => (IReadOnlyList<string>)c.Cast.Concat(c.Directors).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
                    _builtFor = all;
                }

                return _map.TryGetValue(itemId, out var people) ? people : Array.Empty<string>();
            }
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }
}
