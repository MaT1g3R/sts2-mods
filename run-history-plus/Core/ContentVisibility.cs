namespace RunHistoryPlus.Core;

public enum FilterContentCategory
{
    Relic,
    Card,
    Potion,
    Event
}

/// <summary>
/// Immutable snapshot of discovered (seen) and unlocked content entry IDs per category.
/// Shared by all advanced filter views (Relics, and future filters such as Cards, Potions, Events)
/// so players never see locked or undiscovered content as spoilers.
/// </summary>
public sealed class ContentVisibility
{
    public static ContentVisibility Empty { get; } = new(
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<string>());

    private readonly HashSet<string> _visibleRelics;
    private readonly HashSet<string> _visibleCards;
    private readonly HashSet<string> _visiblePotions;
    private readonly HashSet<string> _visibleEvents;

    public IReadOnlySet<string> VisibleRelicIds => _visibleRelics;
    public IReadOnlySet<string> VisibleCardIds => _visibleCards;
    public IReadOnlySet<string> VisiblePotionIds => _visiblePotions;
    public IReadOnlySet<string> VisibleEventIds => _visibleEvents;

    public ContentVisibility(
        IEnumerable<string> visibleRelicIds,
        IEnumerable<string> visibleCardIds,
        IEnumerable<string> visiblePotionIds,
        IEnumerable<string> visibleEventIds)
    {
        _visibleRelics = ToNormalizedSet(visibleRelicIds);
        _visibleCards = ToNormalizedSet(visibleCardIds);
        _visiblePotions = ToNormalizedSet(visiblePotionIds);
        _visibleEvents = ToNormalizedSet(visibleEventIds);
    }

    public static ContentVisibility FromSeenAndUnlocked(
        IEnumerable<string> seenRelicIds,
        IEnumerable<string> unlockedRelicIds,
        IEnumerable<string> seenCardIds,
        IEnumerable<string> unlockedCardIds,
        IEnumerable<string> seenPotionIds,
        IEnumerable<string> unlockedPotionIds,
        IEnumerable<string> seenEventIds,
        IEnumerable<string> unlockedEventIds)
    {
        return new ContentVisibility(
            IntersectSeenAndUnlocked(seenRelicIds, unlockedRelicIds),
            IntersectSeenAndUnlocked(seenCardIds, unlockedCardIds),
            IntersectSeenAndUnlocked(seenPotionIds, unlockedPotionIds),
            IntersectSeenAndUnlocked(seenEventIds, unlockedEventIds));
    }

    /// <summary>
    /// Returns the case-insensitive set of entry IDs that have been both seen (discovered) and unlocked.
    /// </summary>
    public static HashSet<string> IntersectSeenAndUnlocked(
        IEnumerable<string> seenEntryIds,
        IEnumerable<string> unlockedEntryIds)
    {
        var unlocked = ToNormalizedSet(unlockedEntryIds);
        var visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string id in seenEntryIds)
        {
            if (!string.IsNullOrWhiteSpace(id) && unlocked.Contains(id))
            {
                visible.Add(id);
            }
        }

        return visible;
    }

    public bool IsVisible(FilterContentCategory category, string entryId)
    {
        if (string.IsNullOrWhiteSpace(entryId))
        {
            return false;
        }

        return GetSet(category).Contains(entryId);
    }

    public bool IsRelicVisible(string entryId) => IsVisible(FilterContentCategory.Relic, entryId);
    public bool IsCardVisible(string entryId) => IsVisible(FilterContentCategory.Card, entryId);
    public bool IsPotionVisible(string entryId) => IsVisible(FilterContentCategory.Potion, entryId);
    public bool IsEventVisible(string entryId) => IsVisible(FilterContentCategory.Event, entryId);

    public IEnumerable<T> FilterVisible<T>(
        FilterContentCategory category,
        IEnumerable<T> items,
        Func<T, string> entryIdSelector)
    {
        HashSet<string> visibleSet = GetSet(category);
        foreach (T item in items)
        {
            string id = entryIdSelector(item);
            if (!string.IsNullOrWhiteSpace(id) && visibleSet.Contains(id))
            {
                yield return item;
            }
        }
    }

    public bool MatchesCategory(ContentVisibility? other, FilterContentCategory category)
    {
        if (other == null)
        {
            return false;
        }

        HashSet<string> current = GetSet(category);
        HashSet<string> previous = other.GetSet(category);
        return current.Count == previous.Count && current.SetEquals(previous);
    }

    private HashSet<string> GetSet(FilterContentCategory category) => category switch
    {
        FilterContentCategory.Relic => _visibleRelics,
        FilterContentCategory.Card => _visibleCards,
        FilterContentCategory.Potion => _visiblePotions,
        FilterContentCategory.Event => _visibleEvents,
        _ => _visibleRelics
    };

    private static HashSet<string> ToNormalizedSet(IEnumerable<string> ids)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in ids)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                set.Add(id);
            }
        }
        return set;
    }
}
