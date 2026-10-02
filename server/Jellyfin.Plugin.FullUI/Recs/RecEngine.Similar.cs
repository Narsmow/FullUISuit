using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.FullUI.Library;

namespace Jellyfin.Plugin.FullUI.Recs;

public sealed partial class RecEngine
{
    /// <summary>Added to the similarity of a title that belongs to the same collection (BoxSet) as the seed: siblings always rank first.</summary>
    private const double SiblingBonus = 1.0;

    /// <summary>
    /// "More like this" for one title: its nearest neighbours by content (genre, cast, studio, tags), AI embeddings when present, and
    /// the other members of its collection. Goes through the same eligibility rule as every row (visible to the user, not thumbed
    /// down, not finished, not a dropped show); the title itself (and any duplicate edition of it) never appears.
    /// Returns an empty list when the title is not in the catalog.
    /// </summary>
    public static IReadOnlyList<RankedItem> Similar(RecInput input, Guid itemId, int count)
        => count <= 0 ? Array.Empty<RankedItem>() : new RecEngine(input).SimilarTo(itemId, count);

    private IReadOnlyList<RankedItem> SimilarTo(Guid itemId, int count)
    {
        if (!_idx.ById.TryGetValue(itemId, out var seed))
        {
            return Array.Empty<RankedItem>();
        }

        var seedId = Canon(itemId);
        var same = new HashSet<Guid> { itemId, seedId, seed.Id };

        // Percentiles for the match % are taken over the same pool the home rows use.
        _scoreSorted = Candidates().Select(Score).OrderBy(x => x).ToArray();

        var ranked = new List<(CatalogItem Item, double Sim, bool Sibling)>();
        foreach (var c in _catalog)
        {
            if (same.Contains(c.Id) || !Eligible(c))
            {
                continue;
            }

            var sibling = seed.CollectionId is Guid col && c.CollectionId == col;
            var sim = Similarity(seed, c);
            if (sibling || sim >= MinSeedSimilarity)
            {
                ranked.Add((c, sim + (sibling ? SiblingBonus : 0), sibling));
            }
        }

        return ranked
            .OrderByDescending(x => x.Sim)
            .ThenByDescending(x => x.Item.Rating ?? 0)
            .ThenBy(x => x.Item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Item.Id)
            .Take(count)
            .Select(x => new RankedItem(
                x.Item,
                Badges(x.Item, _in.Now),
                null,
                null,
                MatchFor(x.Item),
                x.Sibling ? $"Also in {x.Item.CollectionName ?? seed.CollectionName ?? "this collection"}" : $"Similar to {seed.Name}"))
            .ToList();
    }
}
