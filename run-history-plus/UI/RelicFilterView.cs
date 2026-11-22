using Godot;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using RunHistoryPlus.Core;

namespace RunHistoryPlus.UI;

/// <summary>
/// Relic Filter view inside the More Filters modal.
/// Displays every relic in the game grouped by rarity (matching STS2's Relic Collection),
/// greyed out until selected, with a live search box, All/Any toggle, and native STS2 hover tips.
/// </summary>
internal sealed class RelicFilterView
{
    private static readonly RelicRarity[] RarityOrder =
    {
        RelicRarity.Starter,
        RelicRarity.Common,
        RelicRarity.Uncommon,
        RelicRarity.Rare,
        RelicRarity.Shop,
        RelicRarity.Event,
        RelicRarity.Ancient
    };

    private static readonly Color GoldColor = new("EFC851");
    private static readonly Color CreamColor = new("FFF6E2");
    private static readonly Color MutedColor = new(0.75f, 0.83f, 0.81f, 0.85f);
    private static readonly Color UnselectedIconModulate = new(0.42f, 0.42f, 0.45f, 0.58f);
    private static readonly Color UnselectedHoverModulate = new(0.82f, 0.82f, 0.85f, 0.90f);
    private static readonly Color SelectedBgColor = new(0.22f, 0.34f, 0.40f, 0.92f);
    private static readonly Color UnselectedBgColor = new(0.08f, 0.14f, 0.18f, 0.65f);
    private static readonly Color SelectedBorderColor = new("EFC851");
    private static readonly Color UnselectedBorderColor = new(0.20f, 0.31f, 0.37f, 0.70f);
    private static readonly Color InputBgColor = new("0B151B");
    private static readonly Color InputBorderColor = new("3C5B6B");

    private sealed class RelicTile
    {
        public required string RelicId;
        public required string DisplayName;
        public required RelicModel Model;
        public required PanelContainer Container;
        public required TextureRect IconRect;
        public bool IsHovered;
    }

    private sealed class RaritySection
    {
        public required Control Header;
        public required HFlowContainer Grid;
        public required List<RelicTile> Tiles;
    }

    private readonly RunFilterState _state;
    private readonly Action _onChanged;
    private readonly Font? _boldFont;
    private readonly Font? _regularFont;

    private readonly VBoxContainer _root;
    private readonly LineEdit _searchInput;
    private readonly Button _clearSearchBtn;
    private readonly Button _matchAllBtn;
    private readonly Button _matchAnyBtn;
    private readonly Label _selectionCountLabel;
    private readonly VBoxContainer _sectionsHost;
    private readonly Label _noSearchResultsLabel;

    private readonly List<RaritySection> _sections = new();
    private readonly Dictionary<string, RelicTile> _tilesById = new(StringComparer.OrdinalIgnoreCase);

    private bool _gridBuilt;

    public Control Root => _root;

    public RelicFilterView(RunFilterState state, Action onChanged, Font? boldFont, Font? regularFont)
    {
        _state = state;
        _onChanged = onChanged;
        _boldFont = boldFont;
        _regularFont = regularFont;

        _root = new VBoxContainer
        {
            Name = "RelicFilterView",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        _root.AddThemeConstantOverride("separation", 12);

        // --- Header toolbar: Search input + Match All / Any toggle + selected count ---
        HBoxContainer toolbar = new HBoxContainer();
        toolbar.AddThemeConstantOverride("separation", 10);
        _root.AddChild(toolbar);

        _searchInput = new LineEdit
        {
            Name = "RelicSearchInput",
            PlaceholderText = "Search relics by name...",
            CustomMinimumSize = new Vector2(280f, 40f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CaretBlink = true
        };
        if (_regularFont != null) _searchInput.AddThemeFontOverride("font", _regularFont);
        _searchInput.AddThemeFontSizeOverride("font_size", 19);
        _searchInput.AddThemeColorOverride("font_color", CreamColor);
        _searchInput.AddThemeColorOverride("font_placeholder_color", new Color(0.65f, 0.72f, 0.72f, 0.65f));
        _searchInput.AddThemeColorOverride("caret_color", GoldColor);
        _searchInput.AddThemeStyleboxOverride("normal", CreateInputStyle(InputBorderColor));
        _searchInput.AddThemeStyleboxOverride("focus", CreateInputStyle(GoldColor));
        _searchInput.TextChanged += OnSearchTextChanged;
        toolbar.AddChild(_searchInput);

        _clearSearchBtn = MoreFiltersPanel.CreateStyledButton("×", _boldFont, () =>
        {
            _searchInput.Text = string.Empty;
            ApplySearchFilter(string.Empty);
        }, new Vector2(40f, 40f));
        _clearSearchBtn.Visible = false;
        toolbar.AddChild(_clearSearchBtn);

        _matchAllBtn = MoreFiltersPanel.CreateStyledButton("Match All", _boldFont, () => SetMatchMode(MultiMatchMode.All), new Vector2(114f, 40f));
        _matchAnyBtn = MoreFiltersPanel.CreateStyledButton("Match Any", _boldFont, () => SetMatchMode(MultiMatchMode.Any), new Vector2(114f, 40f));
        toolbar.AddChild(_matchAllBtn);
        toolbar.AddChild(_matchAnyBtn);

        _selectionCountLabel = new Label
        {
            Text = "0 selected",
            VerticalAlignment = VerticalAlignment.Center,
            CustomMinimumSize = new Vector2(96f, 40f),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        if (_regularFont != null) _selectionCountLabel.AddThemeFontOverride("font", _regularFont);
        _selectionCountLabel.AddThemeFontSizeOverride("font_size", 17);
        _selectionCountLabel.AddThemeColorOverride("font_color", MutedColor);
        toolbar.AddChild(_selectionCountLabel);

        // --- Scrollable relic grid grouped by rarity ---
        ScrollContainer scroll = new ScrollContainer
        {
            Name = "RelicGridScroll",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        _root.AddChild(scroll);

        MarginContainer scrollPad = new MarginContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        scrollPad.AddThemeConstantOverride("margin_right", 10);
        scrollPad.AddThemeConstantOverride("margin_bottom", 8);
        scroll.AddChild(scrollPad);

        _sectionsHost = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        _sectionsHost.AddThemeConstantOverride("separation", 14);
        scrollPad.AddChild(_sectionsHost);

        _noSearchResultsLabel = new Label
        {
            Text = "No relics match your search.",
            Visible = false,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        if (_regularFont != null) _noSearchResultsLabel.AddThemeFontOverride("font", _regularFont);
        _noSearchResultsLabel.AddThemeFontSizeOverride("font_size", 20);
        _noSearchResultsLabel.AddThemeColorOverride("font_color", MutedColor);
        _sectionsHost.AddChild(_noSearchResultsLabel);

        RefreshMatchModeButtons();
    }

    public void EnsureBuilt()
    {
        if (_gridBuilt)
        {
            RefreshSelectionVisuals();
            return;
        }

        _gridBuilt = true;
        BuildRelicGrid();
        RefreshSelectionVisuals();
    }

    public bool TryGetRelicDisplayInfo(string relicId, out string displayName, out Texture2D? icon)
    {
        if (_tilesById.TryGetValue(relicId, out RelicTile? tile))
        {
            displayName = tile.DisplayName;
            icon = tile.IconRect.Texture;
            return true;
        }

        displayName = FormatFallbackName(relicId);
        icon = null;
        return false;
    }

    public void RefreshSelectionVisuals()
    {
        foreach (RelicTile tile in _tilesById.Values)
        {
            UpdateTileVisual(tile);
        }

        _selectionCountLabel.Text = $"{_state.RequiredRelicIds.Count} selected";
        RefreshMatchModeButtons();
    }

    public void ClearHoverTips()
    {
        foreach (RelicTile tile in _tilesById.Values)
        {
            if (tile.IsHovered)
            {
                tile.IsHovered = false;
                NHoverTipSet.Remove(tile.Container);
                UpdateTileVisual(tile);
            }
        }
    }

    private void BuildRelicGrid()
    {
        List<RelicModel> allRelics;
        try
        {
            allRelics = ModelDb.AllRelics
                .GroupBy(r => r.Id.Entry, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Error($"[RunHistoryPlus] Failed to enumerate ModelDb.AllRelics: {ex}");
            return;
        }

        var byRarity = allRelics.GroupBy(r => r.Rarity).ToDictionary(g => g.Key, g => g.ToList());

        foreach (RelicRarity rarity in RarityOrder)
        {
            if (!byRarity.TryGetValue(rarity, out List<RelicModel>? relics) || relics.Count == 0)
            {
                continue;
            }

            var ordered = relics
                .Select(r => (Model: r, Title: GetRelicTitle(r)))
                .OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Label header = new Label
            {
                Text = GetRarityLabel(rarity),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            if (_boldFont != null) header.AddThemeFontOverride("font", _boldFont);
            header.AddThemeFontSizeOverride("font_size", 20);
            header.AddThemeColorOverride("font_color", GoldColor);
            header.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.5f));
            header.AddThemeConstantOverride("shadow_offset_x", 2);
            header.AddThemeConstantOverride("shadow_offset_y", 1);
            _sectionsHost.AddChild(header);

            HFlowContainer grid = new HFlowContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            grid.AddThemeConstantOverride("h_separation", 8);
            grid.AddThemeConstantOverride("v_separation", 8);
            _sectionsHost.AddChild(grid);

            var sectionTiles = new List<RelicTile>(ordered.Count);
            foreach (var (model, title) in ordered)
            {
                RelicTile tile = CreateRelicTile(model, title);
                grid.AddChild(tile.Container);
                sectionTiles.Add(tile);
                _tilesById[tile.RelicId] = tile;
            }

            _sections.Add(new RaritySection
            {
                Header = header,
                Grid = grid,
                Tiles = sectionTiles
            });
        }
    }

    private RelicTile CreateRelicTile(RelicModel model, string title)
    {
        PanelContainer container = new PanelContainer
        {
            CustomMinimumSize = new Vector2(64f, 64f),
            MouseFilter = Control.MouseFilterEnum.Stop,
            TooltipText = string.Empty
        };

        Texture2D? iconTexture = null;
        try
        {
            iconTexture = model.Icon;
        }
        catch
        {
            // Leave null if texture missing
        }

        TextureRect iconRect = new TextureRect
        {
            Texture = iconTexture,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        container.AddChild(iconRect);

        RelicTile tile = new RelicTile
        {
            RelicId = model.Id.Entry,
            DisplayName = title,
            Model = model,
            Container = container,
            IconRect = iconRect
        };

        container.MouseEntered += () =>
        {
            tile.IsHovered = true;
            UpdateTileVisual(tile);
            try
            {
                NHoverTipSet? tipSet = NHoverTipSet.CreateAndShow(
                    container,
                    tile.Model.HoverTips,
                    HoverTip.GetHoverTipAlignment(container));
                if (tipSet != null)
                {
                    tipSet.ZAsRelative = false;
                    tipSet.ZIndex = 500;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[RunHistoryPlus] Could not show hover tip for relic '{tile.RelicId}': {ex.Message}");
            }
        };

        container.MouseExited += () =>
        {
            tile.IsHovered = false;
            NHoverTipSet.Remove(container);
            UpdateTileVisual(tile);
        };

        container.GuiInput += inputEvent =>
        {
            if (inputEvent is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
            {
                ToggleRelic(tile);
                container.AcceptEvent();
            }
        };

        UpdateTileVisual(tile);
        return tile;
    }

    private void ToggleRelic(RelicTile tile)
    {
        if (!_state.RequiredRelicIds.Remove(tile.RelicId))
        {
            _state.RequiredRelicIds.Add(tile.RelicId);
        }

        UpdateTileVisual(tile);
        _selectionCountLabel.Text = $"{_state.RequiredRelicIds.Count} selected";
        _onChanged();
    }

    private void SetMatchMode(MultiMatchMode mode)
    {
        if (_state.RelicMatchMode == mode)
        {
            return;
        }

        _state.RelicMatchMode = mode;
        RefreshMatchModeButtons();
        if (_state.RequiredRelicIds.Count > 1)
        {
            _onChanged();
        }
        else
        {
            // Still notify so Active Filters view reflects updated mode label
            _onChanged();
        }
    }

    private void RefreshMatchModeButtons()
    {
        ApplyToggleStyle(_matchAllBtn, _state.RelicMatchMode == MultiMatchMode.All);
        ApplyToggleStyle(_matchAnyBtn, _state.RelicMatchMode == MultiMatchMode.Any);
    }

    private void OnSearchTextChanged(string newText)
    {
        _clearSearchBtn.Visible = !string.IsNullOrEmpty(newText);
        ApplySearchFilter(newText);
    }

    private void ApplySearchFilter(string query)
    {
        string trimmed = query.Trim();
        int visibleTotal = 0;

        foreach (RaritySection section in _sections)
        {
            int visibleInSection = 0;
            foreach (RelicTile tile in section.Tiles)
            {
                bool matches = string.IsNullOrEmpty(trimmed) ||
                    tile.DisplayName.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                    tile.RelicId.Contains(trimmed, StringComparison.OrdinalIgnoreCase);

                tile.Container.Visible = matches;
                if (matches)
                {
                    visibleInSection++;
                }
            }

            section.Header.Visible = visibleInSection > 0;
            section.Grid.Visible = visibleInSection > 0;
            visibleTotal += visibleInSection;
        }

        _noSearchResultsLabel.Visible = visibleTotal == 0 && !string.IsNullOrEmpty(trimmed);
    }

    private void UpdateTileVisual(RelicTile tile)
    {
        bool selected = _state.RequiredRelicIds.Contains(tile.RelicId);

        tile.IconRect.Modulate = selected
            ? Colors.White
            : tile.IsHovered ? UnselectedHoverModulate : UnselectedIconModulate;

        StyleBoxFlat style = new StyleBoxFlat
        {
            BgColor = selected ? SelectedBgColor : UnselectedBgColor,
            BorderColor = selected ? SelectedBorderColor : (tile.IsHovered ? new Color("5C8599") : UnselectedBorderColor),
            BorderWidthLeft = selected ? 2 : 1,
            BorderWidthTop = selected ? 2 : 1,
            BorderWidthRight = selected ? 2 : 1,
            BorderWidthBottom = selected ? 2 : 1,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
            ContentMarginLeft = 4,
            ContentMarginTop = 4,
            ContentMarginRight = 4,
            ContentMarginBottom = 4
        };
        tile.Container.AddThemeStyleboxOverride("panel", style);
    }

    private static void ApplyToggleStyle(Button btn, bool active)
    {
        Color bg = active ? new Color("355768") : new Color("1E303A");
        Color border = active ? GoldColor : new Color("3C5B6B");

        StyleBoxFlat style = new StyleBoxFlat
        {
            BgColor = bg,
            BorderColor = border,
            BorderWidthLeft = active ? 2 : 1,
            BorderWidthTop = active ? 2 : 1,
            BorderWidthRight = active ? 2 : 1,
            BorderWidthBottom = active ? 2 : 1,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            ContentMarginLeft = 10,
            ContentMarginTop = 6,
            ContentMarginRight = 10,
            ContentMarginBottom = 6
        };

        btn.AddThemeStyleboxOverride("normal", style);
        btn.AddThemeColorOverride("font_color", active ? GoldColor : CreamColor);
    }

    private static StyleBoxFlat CreateInputStyle(Color borderColor) => new()
    {
        BgColor = InputBgColor,
        BorderColor = borderColor,
        BorderWidthLeft = 2,
        BorderWidthTop = 2,
        BorderWidthRight = 2,
        BorderWidthBottom = 2,
        CornerRadiusTopLeft = 4,
        CornerRadiusTopRight = 4,
        CornerRadiusBottomLeft = 4,
        CornerRadiusBottomRight = 4,
        ContentMarginLeft = 10,
        ContentMarginTop = 6,
        ContentMarginRight = 10,
        ContentMarginBottom = 6
    };

    private static string GetRarityLabel(RelicRarity rarity) => rarity switch
    {
        RelicRarity.Starter => "Starter",
        RelicRarity.Common => "Common",
        RelicRarity.Uncommon => "Uncommon",
        RelicRarity.Rare => "Rare",
        RelicRarity.Shop => "Shop",
        RelicRarity.Event => "Event",
        RelicRarity.Ancient => "Ancient",
        _ => rarity.ToString()
    };

    private static string GetRelicTitle(RelicModel model)
    {
        try
        {
            string title = model.Title.GetFormattedText();
            if (!string.IsNullOrWhiteSpace(title))
            {
                return title;
            }
        }
        catch
        {
            // Fallback below
        }

        return FormatFallbackName(model.Id.Entry);
    }

    private static string FormatFallbackName(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
        {
            return "Unknown Relic";
        }

        return string.Join(" ", entry
            .Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
    }
}
