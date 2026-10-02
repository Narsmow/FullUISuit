using System;
using System.Collections.Generic;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Library;

namespace Jellyfin.Plugin.FullUI.Recs;

/// <summary>Everything the engine needs for one user. No Jellyfin types, so it is trivially testable.</summary>
public sealed class RecInput
{
    public Guid UserId { get; init; }
    public IReadOnlyList<CatalogItem> Catalog { get; init; } = Array.Empty<CatalogItem>();
    public IReadOnlySet<Guid> Visible { get; init; } = new HashSet<Guid>();
    public IReadOnlyList<PlaySignal> Signals { get; init; } = Array.Empty<PlaySignal>();

    /// <summary>All users' ratings, key "{userId:N}|{itemId:N}" (see StoreData.UserItemKey).</summary>
    public IReadOnlyDictionary<string, int> Ratings { get; init; } = new Dictionary<string, int>();

    /// <summary>All users' My List entries, same key format.</summary>
    public IReadOnlySet<string> MyList { get; init; } = new HashSet<string>();

    public DateTime Now { get; init; } = DateTime.UtcNow;
    public string ServerName { get; init; } = "FullUI";
    public int TopTenWindowDays { get; init; } = 7;
    public IReadOnlySet<Guid> ExcludedUsers { get; init; } = new HashSet<Guid>();
    public IReadOnlyDictionary<string, string> RowTitles { get; init; } = new Dictionary<string, string>();

    /// <summary>Series with a next episode ready (Jellyfin Next Up), most relevant first. They join Continue Watching.</summary>
    public IReadOnlyList<Guid> NextUpSeries { get; init; } = Array.Empty<Guid>();
}

public sealed record RankedItem(CatalogItem Item, string[] Badges, int? Rank, double? Progress);

/// <summary>A composed row. <see cref="Order"/> is the contract position (see RecEngine.Order*).</summary>
public sealed record RecRow(string Id, string Title, string Type, int Order, IReadOnlyList<RankedItem> Items);
