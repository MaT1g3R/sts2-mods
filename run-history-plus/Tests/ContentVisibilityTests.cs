using RunHistoryPlus.Core;
using Xunit;

namespace RunHistoryPlus.Tests;

public class ContentVisibilityTests
{
    [Fact]
    public void IntersectSeenAndUnlocked_OnlyIncludesItemsBothSeenAndUnlocked_CaseInsensitive()
    {
        var seen = new[] { "BURNING_BLOOD", "anchor", "LOCKED_RELIC", "", "   " };
        var unlocked = new[] { "burning_blood", "ANCHOR", "UNSEEN_RELIC" };

        HashSet<string> visible = ContentVisibility.IntersectSeenAndUnlocked(seen, unlocked);

        Assert.Equal(2, visible.Count);
        Assert.Contains("BURNING_BLOOD", visible);
        Assert.Contains("ANCHOR", visible);
        Assert.DoesNotContain("LOCKED_RELIC", visible);
        Assert.DoesNotContain("UNSEEN_RELIC", visible);
    }

    [Fact]
    public void FromSeenAndUnlocked_AppliesIndependentlyAcrossRelicsCardsPotionsAndEvents()
    {
        ContentVisibility visibility = ContentVisibility.FromSeenAndUnlocked(
            seenRelicIds: new[] { "ANCHOR", "LOCKED_RELIC" },
            unlockedRelicIds: new[] { "ANCHOR", "UNSEEN_RELIC" },
            seenCardIds: new[] { "BASH", "LOCKED_CARD" },
            unlockedCardIds: new[] { "BASH", "UNSEEN_CARD" },
            seenPotionIds: new[] { "FIRE_POTION" },
            unlockedPotionIds: new[] { "FIRE_POTION", "UNSEEN_POTION" },
            seenEventIds: new[] { "NEOW" },
            unlockedEventIds: new[] { "NEOW", "UNSEEN_EVENT" });

        Assert.True(visibility.IsRelicVisible("anchor"));
        Assert.False(visibility.IsRelicVisible("LOCKED_RELIC"));
        Assert.False(visibility.IsRelicVisible("UNSEEN_RELIC"));

        Assert.True(visibility.IsCardVisible("BASH"));
        Assert.False(visibility.IsCardVisible("LOCKED_CARD"));
        Assert.False(visibility.IsCardVisible("UNSEEN_CARD"));

        Assert.True(visibility.IsPotionVisible("FIRE_POTION"));
        Assert.False(visibility.IsPotionVisible("UNSEEN_POTION"));

        Assert.True(visibility.IsEventVisible("NEOW"));
        Assert.False(visibility.IsEventVisible("UNSEEN_EVENT"));
    }

    [Fact]
    public void FilterVisible_FiltersAnyModelCollectionByCategory()
    {
        ContentVisibility visibility = ContentVisibility.FromSeenAndUnlocked(
            seenRelicIds: new[] { "ANCHOR", "LANTERN" },
            unlockedRelicIds: new[] { "ANCHOR", "LANTERN", "VAJRA" },
            seenCardIds: new[] { "STRIKE_IRONCLAD" },
            unlockedCardIds: new[] { "STRIKE_IRONCLAD", "DEFEND_IRONCLAD" },
            seenPotionIds: Array.Empty<string>(),
            unlockedPotionIds: Array.Empty<string>(),
            seenEventIds: Array.Empty<string>(),
            unlockedEventIds: Array.Empty<string>());

        var relics = new[] { ("ANCHOR", "Anchor"), ("VAJRA", "Vajra"), ("LANTERN", "Lantern") };
        var visibleRelics = visibility
            .FilterVisible(FilterContentCategory.Relic, relics, r => r.Item1)
            .Select(r => r.Item2)
            .ToList();

        Assert.Equal(new[] { "Anchor", "Lantern" }, visibleRelics);

        var cards = new[] { ("STRIKE_IRONCLAD", "Strike"), ("DEFEND_IRONCLAD", "Defend") };
        var visibleCards = visibility
            .FilterVisible(FilterContentCategory.Card, cards, c => c.Item1)
            .Select(c => c.Item2)
            .ToList();

        Assert.Equal(new[] { "Strike" }, visibleCards);
    }

    [Fact]
    public void MatchesCategory_DetectsChangesInVisibleSet()
    {
        var first = new ContentVisibility(
            visibleRelicIds: new[] { "ANCHOR", "LANTERN" },
            visibleCardIds: new[] { "BASH" },
            visiblePotionIds: Array.Empty<string>(),
            visibleEventIds: Array.Empty<string>());

        var sameRelicsDifferentCards = new ContentVisibility(
            visibleRelicIds: new[] { "lantern", "anchor" },
            visibleCardIds: new[] { "BASH", "CLEAVE" },
            visiblePotionIds: Array.Empty<string>(),
            visibleEventIds: Array.Empty<string>());

        Assert.True(first.MatchesCategory(sameRelicsDifferentCards, FilterContentCategory.Relic));
        Assert.False(first.MatchesCategory(sameRelicsDifferentCards, FilterContentCategory.Card));
        Assert.False(first.MatchesCategory(null, FilterContentCategory.Relic));
    }
}
