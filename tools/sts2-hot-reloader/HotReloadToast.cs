using Godot;
using MegaCrit.Sts2.Core.Nodes;

namespace Sts2HotReloader;

/// <summary>
/// Non-blocking corner toast banner for live hot-reload status feedback.
/// </summary>
internal static class HotReloadToast
{
    private const string LayerName = "Sts2HotReloaderToastLayer";
    private const string KreonBoldPath = "res://themes/kreon_bold_glyph_space_one.tres";

    private static readonly Color BgColor = new(0.06f, 0.11f, 0.14f, 0.92f);
    private static readonly Color SuccessBorder = new("7AE87A");
    private static readonly Color ErrorBorder = new("FF6464");
    private static readonly Color InfoBorder = new("EFC851");
    private static readonly Color TextColor = new("FFF6E2");

    private static PanelContainer? _panel;
    private static Label? _label;
    private static int _generation;

    public static void Show(string message, bool isError = false, bool isInfo = false)
    {
        NGame? game = NGame.Instance;
        if (game == null || !GodotObject.IsInstanceValid(game))
        {
            return;
        }

        EnsureUi(game);
        if (_panel == null || _label == null || !GodotObject.IsInstanceValid(_panel))
        {
            return;
        }

        Color border = isError ? ErrorBorder : isInfo ? InfoBorder : SuccessBorder;
        _panel.AddThemeStyleboxOverride("panel", CreateStyle(border));
        _label.Text = message;
        _panel.Visible = true;

        int gen = ++_generation;
        double duration = isError ? 5.0 : 2.8;
        SceneTreeTimer? timer = game.GetTree()?.CreateTimer(duration);
        if (timer != null)
        {
            timer.Timeout += () =>
            {
                if (_generation == gen && _panel != null && GodotObject.IsInstanceValid(_panel))
                {
                    _panel.Visible = false;
                }
            };
        }
    }

    private static void EnsureUi(NGame game)
    {
        if (_panel != null && GodotObject.IsInstanceValid(_panel))
        {
            return;
        }

        CanvasLayer layer = game.GetNodeOrNull<CanvasLayer>(LayerName) ?? new CanvasLayer
        {
            Name = LayerName,
            Layer = 120
        };
        if (layer.GetParent() == null)
        {
            game.AddChild(layer);
        }

        _panel = new PanelContainer
        {
            Name = "HotReloadToastPanel",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _panel.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        _panel.AnchorLeft = 0.5f;
        _panel.AnchorRight = 0.5f;
        _panel.AnchorTop = 1f;
        _panel.AnchorBottom = 1f;
        _panel.GrowHorizontal = Control.GrowDirection.Both;
        _panel.GrowVertical = Control.GrowDirection.Begin;
        _panel.OffsetTop = -64f;
        _panel.OffsetBottom = -18f;
        _panel.AddThemeStyleboxOverride("panel", CreateStyle(SuccessBorder));
        layer.AddChild(_panel);

        _label = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        Font? boldFont = ResourceLoader.Exists(KreonBoldPath) ? GD.Load<Font>(KreonBoldPath) : null;
        if (boldFont != null)
        {
            _label.AddThemeFontOverride("font", boldFont);
        }
        _label.AddThemeFontSizeOverride("font_size", 19);
        _label.AddThemeColorOverride("font_color", TextColor);
        _panel.AddChild(_label);
    }

    private static StyleBoxFlat CreateStyle(Color border) => new()
    {
        BgColor = BgColor,
        BorderColor = border,
        BorderWidthLeft = 2,
        BorderWidthTop = 2,
        BorderWidthRight = 2,
        BorderWidthBottom = 2,
        CornerRadiusTopLeft = 6,
        CornerRadiusTopRight = 6,
        CornerRadiusBottomLeft = 6,
        CornerRadiusBottomRight = 6,
        ContentMarginLeft = 18,
        ContentMarginTop = 8,
        ContentMarginRight = 18,
        ContentMarginBottom = 8,
        ShadowColor = new Color(0f, 0f, 0f, 0.5f),
        ShadowSize = 8
    };
}
