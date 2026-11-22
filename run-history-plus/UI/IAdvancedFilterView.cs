using Godot;
using RunHistoryPlus.Core;

namespace RunHistoryPlus.UI;

/// <summary>
/// Common contract for all advanced filter views hosted in <see cref="MoreFiltersPanel"/>
/// (Relics, and future filters such as Cards, Potions, etc.).
/// Every view receives the player's current <see cref="ContentVisibility"/> snapshot so
/// unseen or locked content is never displayed.
/// </summary>
internal interface IAdvancedFilterView
{
    Control Root { get; }

    void EnsureBuilt(ContentVisibility visibility);

    void RefreshSelectionVisuals();

    void ClearHoverTips();
}
