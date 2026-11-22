using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;
using RunHistoryPlus.Core;

namespace RunHistoryPlus.UI;

/// <summary>
/// Reads the active profile's progress and unlock state from STS2's <see cref="SaveManager"/>
/// to produce a <see cref="ContentVisibility"/> snapshot for all advanced filters.
/// </summary>
internal static class ProgressVisibilityProvider
{
    public static ContentVisibility CaptureCurrent()
    {
        try
        {
            ProgressState? progress = SaveManager.Instance?.Progress;
            if (progress == null)
            {
                return ContentVisibility.Empty;
            }

            UnlockState unlockState = SaveManager.Instance!.GenerateUnlockStateFromProgress();

            IEnumerable<string> seenRelics = progress.DiscoveredRelics.Select(id => id.Entry);
            IEnumerable<string> unlockedRelics = unlockState.Relics.Select(r => r.Id.Entry);

            IEnumerable<string> seenCards = progress.DiscoveredCards.Select(id => id.Entry);
            IEnumerable<string> unlockedCards = ModelDb.AllCardPools
                .SelectMany(pool => pool.GetUnlockedCards(unlockState, CardMultiplayerConstraint.None))
                .Select(c => c.Id.Entry);

            IEnumerable<string> seenPotions = progress.DiscoveredPotions.Select(id => id.Entry);
            IEnumerable<string> unlockedPotions = unlockState.Potions.Select(p => p.Id.Entry);

            List<string> seenEvents = progress.DiscoveredEvents.Select(id => id.Entry).ToList();

            return ContentVisibility.FromSeenAndUnlocked(
                seenRelics,
                unlockedRelics,
                seenCards,
                unlockedCards,
                seenPotions,
                unlockedPotions,
                seenEvents,
                seenEvents);
        }
        catch (Exception ex)
        {
            Log.Warn($"[RunHistoryPlus] Could not capture progress visibility snapshot: {ex.Message}");
            return ContentVisibility.Empty;
        }
    }
}
