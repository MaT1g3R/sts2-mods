using Godot;

namespace RunHistoryPlus.UI;

/// <summary>
/// Placeholder modal overlay for the "More Filters" button, styled to match STS2's dark
/// teal-slate + gold UI palette and ready to host additional filters (relics, cards, etc.).
/// </summary>
internal sealed class MoreFiltersPanel
{
    private const string KreonBoldPath = "res://themes/kreon_bold_glyph_space_one.tres";
    private const string KreonRegularPath = "res://themes/kreon_regular_glyph_space_one.tres";

    private static readonly Color BackdropColor = new(0f, 0f, 0f, 0.62f);
    private static readonly Color PanelBgColor = new("122129");
    private static readonly Color PanelBorderColor = new("3C5B6B");
    private static readonly Color GoldColor = new("EFC851");
    private static readonly Color CreamColor = new("FFF6E2");
    private static readonly Color MutedColor = new(0.77f, 0.85f, 0.82f, 0.85f);
    private static readonly Color ButtonRestColor = new("2C434F");
    private static readonly Color ButtonHoverColor = new("3C5B6B");
    private static readonly Color ButtonPressColor = new("1F313A");

    private readonly Control _overlay;
    private readonly Action _onResetFilters;

    public Control Root => _overlay;
    public bool Visible => _overlay.Visible;

    public MoreFiltersPanel(Action onResetFilters)
    {
        _onResetFilters = onResetFilters;
        _overlay = BuildOverlay();
    }

    public void Show()
    {
        _overlay.Visible = true;
    }

    public void Hide()
    {
        _overlay.Visible = false;
    }

    public void Toggle()
    {
        _overlay.Visible = !_overlay.Visible;
    }

    private Control BuildOverlay()
    {
        Font? boldFont = GD.Load<Font>(KreonBoldPath);
        Font? regularFont = GD.Load<Font>(KreonRegularPath);

        Control overlay = new Control
        {
            Name = "MoreFiltersOverlay",
            Visible = false,
            ZIndex = 200,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        overlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);

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
        overlay.AddChild(backdrop);

        PanelContainer panel = new PanelContainer
        {
            Name = "Panel",
            CustomMinimumSize = new Vector2(540f, 260f),
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        panel.SetAnchorsPreset(Control.LayoutPreset.Center);
        panel.GrowHorizontal = Control.GrowDirection.Both;
        panel.GrowVertical = Control.GrowDirection.Both;
        panel.OffsetLeft = -270f;
        panel.OffsetRight = 270f;
        panel.OffsetTop = -130f;
        panel.OffsetBottom = 130f;

        StyleBoxFlat panelStyle = new StyleBoxFlat
        {
            BgColor = PanelBgColor,
            BorderColor = PanelBorderColor,
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            ContentMarginLeft = 28,
            ContentMarginTop = 24,
            ContentMarginRight = 28,
            ContentMarginBottom = 24,
            ShadowColor = new Color(0f, 0f, 0f, 0.55f),
            ShadowSize = 14
        };
        panel.AddThemeStyleboxOverride("panel", panelStyle);
        overlay.AddChild(panel);

        VBoxContainer vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 18);
        panel.AddChild(vbox);

        Label title = new Label
        {
            Text = "More Filters",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        if (boldFont != null) title.AddThemeFontOverride("font", boldFont);
        title.AddThemeFontSizeOverride("font_size", 30);
        title.AddThemeColorOverride("font_color", GoldColor);
        title.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.5f));
        title.AddThemeConstantOverride("shadow_offset_x", 2);
        title.AddThemeConstantOverride("shadow_offset_y", 2);
        vbox.AddChild(title);

        Label body = new Label
        {
            Text = "Additional filters (such as relics, cards, ascension, and modifiers) will appear here.",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (regularFont != null) body.AddThemeFontOverride("font", regularFont);
        body.AddThemeFontSizeOverride("font_size", 21);
        body.AddThemeColorOverride("font_color", MutedColor);
        vbox.AddChild(body);

        HBoxContainer buttons = new HBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Center
        };
        buttons.AddThemeConstantOverride("separation", 16);
        vbox.AddChild(buttons);

        Button resetBtn = CreateStyledButton("Reset Filters", boldFont, () =>
        {
            _onResetFilters();
            Hide();
        });
        buttons.AddChild(resetBtn);

        Button closeBtn = CreateStyledButton("Close", boldFont, Hide);
        buttons.AddChild(closeBtn);

        return overlay;
    }

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
