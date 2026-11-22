using Godot;
using RunHistoryPlus.Core;

namespace RunHistoryPlus.UI;

/// <summary>
/// Two-pane More Filters modal with a left navigation rail (filter categories, Active Filters,
/// Clear All) and a right content pane. Scoped strictly to More Filters (leaving main-screen
/// Outcome / Character / Game Type dropdowns untouched).
/// </summary>
internal sealed class MoreFiltersPanel
{
    private const string KreonBoldPath = "res://themes/kreon_bold_glyph_space_one.tres";
    private const string KreonRegularPath = "res://themes/kreon_regular_glyph_space_one.tres";

    private const string TabRelics = "relics";
    private const string TabActive = "active";

    private static readonly Color BackdropColor = new(0f, 0f, 0f, 0.65f);
    private static readonly Color PanelBgColor = new("101D24");
    private static readonly Color RailBgColor = new("0B141A");
    private static readonly Color PanelBorderColor = new("3C5B6B");
    private static readonly Color GoldColor = new("EFC851");
    private static readonly Color CreamColor = new("FFF6E2");
    private static readonly Color MutedColor = new(0.77f, 0.85f, 0.82f, 0.85f);
    private static readonly Color ButtonRestColor = new("2C434F");
    private static readonly Color ButtonHoverColor = new("3C5B6B");
    private static readonly Color ButtonPressColor = new("1F313A");
    private static readonly Color ClearHoverColor = new(0.45f, 0.16f, 0.16f, 0.95f);

    private readonly RunFilterState _state;
    private readonly Action _onMoreFiltersChanged;
    private readonly Font? _boldFont;
    private readonly Font? _regularFont;

    private readonly Control _overlay;
    private readonly Label _matchSummaryLabel;
    private readonly Button _relicsNavBtn;
    private readonly Button _activeNavBtn;
    private readonly Button _clearAllBtn;
    private readonly RelicFilterView _relicView;
    private readonly ScrollContainer _activeViewRoot;
    private readonly VBoxContainer _activeItemsHost;

    private string _activeTab = TabRelics;

    public Control Root => _overlay;
    public bool Visible => _overlay.Visible;

    public MoreFiltersPanel(RunFilterState state, Action onMoreFiltersChanged)
    {
        _state = state;
        _onMoreFiltersChanged = onMoreFiltersChanged;
        _boldFont = GD.Load<Font>(KreonBoldPath);
        _regularFont = GD.Load<Font>(KreonRegularPath);

        _relicView = new RelicFilterView(_state, HandleFilterChanged, _boldFont, _regularFont);

        _overlay = new Control
        {
            Name = "MoreFiltersOverlay",
            Visible = false,
            ZIndex = 200,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        _overlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);

        ColorRect backdrop = new ColorRect
        {
            Name = "Backdrop",
            Color = BackdropColor,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        backdrop.GuiInput += inputEvent =>
        {
            if (inputEvent is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
            {
                Hide();
                backdrop.AcceptEvent();
            }
        };
        _overlay.AddChild(backdrop);

        PanelContainer panel = new PanelContainer
        {
            Name = "Panel",
            CustomMinimumSize = new Vector2(1180f, 720f),
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        panel.SetAnchorsPreset(Control.LayoutPreset.Center);
        panel.GrowHorizontal = Control.GrowDirection.Both;
        panel.GrowVertical = Control.GrowDirection.Both;
        panel.OffsetLeft = -590f;
        panel.OffsetRight = 590f;
        panel.OffsetTop = -360f;
        panel.OffsetBottom = 360f;
        panel.AddThemeStyleboxOverride("panel", CreatePanelStyle(PanelBgColor, PanelBorderColor, 20));
        _overlay.AddChild(panel);

        VBoxContainer outerVbox = new VBoxContainer();
        outerVbox.AddThemeConstantOverride("separation", 14);
        panel.AddChild(outerVbox);

        // --- Modal Header ---
        HBoxContainer headerRow = new HBoxContainer();
        headerRow.AddThemeConstantOverride("separation", 16);
        outerVbox.AddChild(headerRow);

        Label title = new Label
        {
            Text = "More Filters",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (_boldFont != null) title.AddThemeFontOverride("font", _boldFont);
        title.AddThemeFontSizeOverride("font_size", 28);
        title.AddThemeColorOverride("font_color", GoldColor);
        title.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.5f));
        title.AddThemeConstantOverride("shadow_offset_x", 2);
        title.AddThemeConstantOverride("shadow_offset_y", 2);
        headerRow.AddChild(title);

        _matchSummaryLabel = new Label
        {
            Text = "",
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        if (_regularFont != null) _matchSummaryLabel.AddThemeFontOverride("font", _regularFont);
        _matchSummaryLabel.AddThemeFontSizeOverride("font_size", 19);
        _matchSummaryLabel.AddThemeColorOverride("font_color", MutedColor);
        headerRow.AddChild(_matchSummaryLabel);

        Button closeBtn = CreateStyledButton("Close", _boldFont, Hide, new Vector2(110f, 40f));
        headerRow.AddChild(closeBtn);

        // --- Body: Left Navigation Rail + Right Content Pane ---
        HBoxContainer bodyRow = new HBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        bodyRow.AddThemeConstantOverride("separation", 16);
        outerVbox.AddChild(bodyRow);

        PanelContainer railPanel = new PanelContainer
        {
            CustomMinimumSize = new Vector2(236f, 0f),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        railPanel.AddThemeStyleboxOverride("panel", CreatePanelStyle(RailBgColor, new Color("253A45"), 12));
        bodyRow.AddChild(railPanel);

        VBoxContainer railVbox = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        railVbox.AddThemeConstantOverride("separation", 8);
        railPanel.AddChild(railVbox);

        Label categoriesHeader = new Label
        {
            Text = "Filter Categories",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        if (_boldFont != null) categoriesHeader.AddThemeFontOverride("font", _boldFont);
        categoriesHeader.AddThemeFontSizeOverride("font_size", 17);
        categoriesHeader.AddThemeColorOverride("font_color", MutedColor);
        railVbox.AddChild(categoriesHeader);

        _relicsNavBtn = CreateNavButton("Relics", () => SelectTab(TabRelics));
        railVbox.AddChild(_relicsNavBtn);

        HSeparator sep = new HSeparator();
        railVbox.AddChild(sep);

        _activeNavBtn = CreateNavButton("Active Filters", () => SelectTab(TabActive));
        railVbox.AddChild(_activeNavBtn);

        Control spacer = new Control
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        railVbox.AddChild(spacer);

        _clearAllBtn = CreateStyledButton("Clear All Filters", _boldFont, ClearAllMoreFilters, new Vector2(210f, 44f));
        _clearAllBtn.AddThemeStyleboxOverride("hover", CreateButtonStyle(ClearHoverColor));
        railVbox.AddChild(_clearAllBtn);

        // --- Right Content Pane ---
        Control contentPane = new Control
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        bodyRow.AddChild(contentPane);

        _relicView.Root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        contentPane.AddChild(_relicView.Root);

        _activeViewRoot = new ScrollContainer
        {
            Name = "ActiveFiltersView",
            Visible = false,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        _activeViewRoot.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        contentPane.AddChild(_activeViewRoot);

        _activeItemsHost = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        _activeItemsHost.AddThemeConstantOverride("separation", 12);
        _activeViewRoot.AddChild(_activeItemsHost);

        SelectTab(TabRelics);
        RefreshNavAndActiveView();
    }

    public void Show()
    {
        _relicView.EnsureBuilt();
        RefreshNavAndActiveView();
        _overlay.Visible = true;
    }

    public void Hide()
    {
        _relicView.ClearHoverTips();
        _overlay.Visible = false;
    }

    public void Toggle()
    {
        if (_overlay.Visible)
        {
            Hide();
        }
        else
        {
            Show();
        }
    }

    public void UpdateMatchSummary(string text)
    {
        _matchSummaryLabel.Text = text;
    }

    public void RefreshState()
    {
        if (_overlay.Visible)
        {
            _relicView.RefreshSelectionVisuals();
        }
        RefreshNavAndActiveView();
    }

    private void SelectTab(string tabId)
    {
        _activeTab = tabId;
        _relicView.ClearHoverTips();
        _relicView.Root.Visible = tabId == TabRelics;
        _activeViewRoot.Visible = tabId == TabActive;

        if (tabId == TabActive)
        {
            RebuildActiveFiltersView();
        }

        RefreshNavButtonStyles();
    }

    private void HandleFilterChanged()
    {
        RefreshNavAndActiveView();
        _onMoreFiltersChanged();
    }

    private void ClearAllMoreFilters()
    {
        if (!_state.HasMoreFilters)
        {
            return;
        }

        _state.ClearMoreFilters();
        _relicView.RefreshSelectionVisuals();
        RefreshNavAndActiveView();
        _onMoreFiltersChanged();
    }

    private void RefreshNavAndActiveView()
    {
        int relicCount = _state.RequiredRelicIds.Count;
        int totalMore = _state.ActiveMoreFilterCount;

        _relicsNavBtn.Text = relicCount > 0 ? $"Relics ({relicCount})" : "Relics";
        _activeNavBtn.Text = totalMore > 0 ? $"Active Filters ({totalMore})" : "Active Filters";
        _clearAllBtn.Disabled = totalMore == 0;

        RefreshNavButtonStyles();

        if (_activeTab == TabActive)
        {
            RebuildActiveFiltersView();
        }
    }

    private void RefreshNavButtonStyles()
    {
        ApplyNavStyle(_relicsNavBtn, _activeTab == TabRelics);
        ApplyNavStyle(_activeNavBtn, _activeTab == TabActive);
    }

    private void RebuildActiveFiltersView()
    {
        foreach (Node child in _activeItemsHost.GetChildren())
        {
            _activeItemsHost.RemoveChild(child);
            child.QueueFree();
        }

        if (!_state.HasMoreFilters)
        {
            Label empty = new Label
            {
                Text = "No additional filters are active.\nSelect a category on the left to add filters.",
                HorizontalAlignment = HorizontalAlignment.Center,
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            };
            if (_regularFont != null) empty.AddThemeFontOverride("font", _regularFont);
            empty.AddThemeFontSizeOverride("font_size", 21);
            empty.AddThemeColorOverride("font_color", MutedColor);
            _activeItemsHost.AddChild(empty);
            return;
        }

        if (_state.RequiredRelicIds.Count > 0)
        {
            string modeText = _state.RelicMatchMode == MultiMatchMode.All ? "Match All" : "Match Any";
            Label groupHeader = new Label
            {
                Text = $"Relics ({modeText})",
                HorizontalAlignment = HorizontalAlignment.Left
            };
            if (_boldFont != null) groupHeader.AddThemeFontOverride("font", _boldFont);
            groupHeader.AddThemeFontSizeOverride("font_size", 22);
            groupHeader.AddThemeColorOverride("font_color", GoldColor);
            _activeItemsHost.AddChild(groupHeader);

            HFlowContainer pills = new HFlowContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            pills.AddThemeConstantOverride("h_separation", 10);
            pills.AddThemeConstantOverride("v_separation", 10);
            _activeItemsHost.AddChild(pills);

            var sortedRelics = _state.RequiredRelicIds
                .Select(id =>
                {
                    _relicView.TryGetRelicDisplayInfo(id, out string name, out Texture2D? icon);
                    return (Id: id, Name: name, Icon: icon);
                })
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var (id, name, icon) in sortedRelics)
            {
                pills.AddChild(CreateActiveRelicPill(id, name, icon));
            }
        }
    }

    private Control CreateActiveRelicPill(string relicId, string displayName, Texture2D? icon)
    {
        PanelContainer pill = new PanelContainer();
        pill.AddThemeStyleboxOverride("panel", CreatePanelStyle(new Color("1D313C"), GoldColor, 8));

        HBoxContainer row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        pill.AddChild(row);

        if (icon != null)
        {
            TextureRect iconRect = new TextureRect
            {
                Texture = icon,
                CustomMinimumSize = new Vector2(32f, 32f),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            row.AddChild(iconRect);
        }

        Label nameLabel = new Label
        {
            Text = displayName,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (_regularFont != null) nameLabel.AddThemeFontOverride("font", _regularFont);
        nameLabel.AddThemeFontSizeOverride("font_size", 19);
        nameLabel.AddThemeColorOverride("font_color", CreamColor);
        row.AddChild(nameLabel);

        string capturedId = relicId;
        Button removeBtn = CreateStyledButton("×", _boldFont, () =>
        {
            _state.RequiredRelicIds.Remove(capturedId);
            _relicView.RefreshSelectionVisuals();
            HandleFilterChanged();
        }, new Vector2(30f, 30f));
        removeBtn.AddThemeStyleboxOverride("hover", CreateButtonStyle(ClearHoverColor));
        row.AddChild(removeBtn);

        return pill;
    }

    private Button CreateNavButton(string text, Action onPressed)
    {
        Button btn = CreateStyledButton(text, _boldFont, onPressed, new Vector2(210f, 44f));
        btn.Alignment = HorizontalAlignment.Left;
        return btn;
    }

    private static void ApplyNavStyle(Button btn, bool active)
    {
        Color bg = active ? new Color("355768") : new Color("182831");
        Color border = active ? GoldColor : new Color("2C434F");

        StyleBoxFlat normal = new StyleBoxFlat
        {
            BgColor = bg,
            BorderColor = border,
            BorderWidthLeft = active ? 3 : 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            ContentMarginLeft = 14,
            ContentMarginTop = 8,
            ContentMarginRight = 12,
            ContentMarginBottom = 8
        };

        btn.AddThemeStyleboxOverride("normal", normal);
        btn.AddThemeColorOverride("font_color", active ? GoldColor : CreamColor);
    }

    private static StyleBoxFlat CreatePanelStyle(Color bg, Color border, int margin) => new()
    {
        BgColor = bg,
        BorderColor = border,
        BorderWidthLeft = 2,
        BorderWidthTop = 2,
        BorderWidthRight = 2,
        BorderWidthBottom = 2,
        CornerRadiusTopLeft = 8,
        CornerRadiusTopRight = 8,
        CornerRadiusBottomLeft = 8,
        CornerRadiusBottomRight = 8,
        ContentMarginLeft = margin,
        ContentMarginTop = margin,
        ContentMarginRight = margin,
        ContentMarginBottom = margin,
        ShadowColor = new Color(0f, 0f, 0f, 0.55f),
        ShadowSize = 14
    };

    internal static Button CreateStyledButton(string text, Font? font, Action onPressed, Vector2? minSize = null)
    {
        Button btn = new Button
        {
            Text = text,
            CustomMinimumSize = minSize ?? new Vector2(180f, 44f),
            FocusMode = Control.FocusModeEnum.None
        };

        if (font != null) btn.AddThemeFontOverride("font", font);
        btn.AddThemeFontSizeOverride("font_size", 20);
        btn.AddThemeColorOverride("font_color", CreamColor);
        btn.AddThemeColorOverride("font_hover_color", GoldColor);
        btn.AddThemeColorOverride("font_pressed_color", GoldColor);

        btn.AddThemeStyleboxOverride("normal", CreateButtonStyle(ButtonRestColor));
        btn.AddThemeStyleboxOverride("hover", CreateButtonStyle(ButtonHoverColor));
        btn.AddThemeStyleboxOverride("pressed", CreateButtonStyle(ButtonPressColor));
        btn.Pressed += onPressed;
        return btn;
    }

    private static StyleBoxFlat CreateButtonStyle(Color bg) => new()
    {
        BgColor = bg,
        BorderColor = new Color("4A6E80"),
        BorderWidthLeft = 1,
        BorderWidthTop = 1,
        BorderWidthRight = 1,
        BorderWidthBottom = 1,
        CornerRadiusTopLeft = 4,
        CornerRadiusTopRight = 4,
        CornerRadiusBottomLeft = 4,
        CornerRadiusBottomRight = 4,
        ContentMarginLeft = 14,
        ContentMarginTop = 8,
        ContentMarginRight = 14,
        ContentMarginBottom = 8
    };
}
