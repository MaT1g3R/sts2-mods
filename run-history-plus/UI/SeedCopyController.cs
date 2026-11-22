using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using MegaCrit.Sts2.addons.mega_text;

namespace RunHistoryPlus.UI;

/// <summary>
/// Makes the Seed label on <see cref="NRunHistory"/> clickable to copy the run seed to the
/// system clipboard, with a pointing-hand cursor, colour highlight, and floating hover hint.
/// </summary>
internal sealed class SeedCopyController
{
    private const string HoverHintText = "Click to copy seed";
    private const string CopiedHintText = "Copied!";
    private const double CopiedFeedbackSeconds = 1.5;

    private static readonly Color CreamColor = new("FFF6E2");
    private static readonly Color GoldColor = new("EFC851");
    private static readonly Color CopiedColor = new("7AE87A");
    private static readonly Color TooltipBgColor = new("101D24");
    private static readonly Color TooltipBorderColor = new("3C5B6B");

    private readonly NRunHistory _screen;
    private readonly Func<string?> _getCurrentSeed;
    private readonly MegaRichTextLabel? _seedLabel;
    private readonly PanelContainer _tooltipPanel;
    private readonly Label _tooltipLabel;

    private bool _isHovered;
    private int _copiedGeneration;

    public SeedCopyController(NRunHistory screen, Func<string?> getCurrentSeed, Font? regularFont)
    {
        _screen = screen;
        _getCurrentSeed = getCurrentSeed;

        _tooltipPanel = new PanelContainer
        {
            Name = "RunHistoryPlusSeedTooltip",
            Visible = false,
            TopLevel = true,
            ZIndex = 150,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _tooltipPanel.AddThemeStyleboxOverride("panel", CreateTooltipStyle(TooltipBorderColor));

        _tooltipLabel = new Label
        {
            Text = HoverHintText,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        if (regularFont != null)
        {
            _tooltipLabel.AddThemeFontOverride("font", regularFont);
        }
        _tooltipLabel.AddThemeFontSizeOverride("font_size", 17);
        _tooltipLabel.AddThemeColorOverride("font_color", CreamColor);
        _tooltipPanel.AddChild(_tooltipLabel);
        _screen.AddChild(_tooltipPanel);

        _seedLabel = _screen.GetNodeOrNull<MegaRichTextLabel>("%SeedLabel");
        if (_seedLabel != null)
        {
            _seedLabel.MouseFilter = Control.MouseFilterEnum.Pass;
            _seedLabel.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
            _seedLabel.MouseEntered += OnSeedMouseEntered;
            _seedLabel.MouseExited += OnSeedMouseExited;
            _seedLabel.GuiInput += OnSeedGuiInput;
        }
        else
        {
            Log.Warn("[RunHistoryPlus] Could not find %SeedLabel on NRunHistory.");
        }
    }

    public void OnRunChanged()
    {
        _copiedGeneration++;
        ApplyDefaultTooltipAppearance();

        if (_isHovered && HasCopyableSeed())
        {
            ShowAndPositionTooltip();
        }
        else
        {
            _tooltipPanel.Visible = false;
        }
    }

    public void OnSubmenuHidden()
    {
        _copiedGeneration++;
        _isHovered = false;
        _tooltipPanel.Visible = false;
        ApplyDefaultTooltipAppearance();

        if (_seedLabel != null && GodotObject.IsInstanceValid(_seedLabel))
        {
            _seedLabel.AddThemeColorOverride("default_color", CreamColor);
        }
    }

    private void OnSeedMouseEntered()
    {
        if (!HasCopyableSeed() || _seedLabel == null)
        {
            return;
        }

        _isHovered = true;
        _seedLabel.AddThemeColorOverride("default_color", GoldColor);
        ApplyDefaultTooltipAppearance();
        ShowAndPositionTooltip();
    }

    private void OnSeedMouseExited()
    {
        _isHovered = false;
        _tooltipPanel.Visible = false;

        if (_seedLabel != null && GodotObject.IsInstanceValid(_seedLabel))
        {
            _seedLabel.AddThemeColorOverride("default_color", CreamColor);
        }
    }

    private void OnSeedGuiInput(InputEvent inputEvent)
    {
        if (inputEvent is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
        {
            return;
        }

        string? seed = _getCurrentSeed();
        if (string.IsNullOrWhiteSpace(seed))
        {
            return;
        }

        try
        {
            DisplayServer.ClipboardSet(seed);
            ShowCopiedFeedback();
            _seedLabel?.AcceptEvent();
        }
        catch (Exception ex)
        {
            Log.Warn($"[RunHistoryPlus] Failed to copy seed to clipboard: {ex.Message}");
        }
    }

    private void ShowCopiedFeedback()
    {
        int generation = ++_copiedGeneration;
        _tooltipLabel.Text = CopiedHintText;
        _tooltipLabel.AddThemeColorOverride("font_color", CopiedColor);
        _tooltipPanel.AddThemeStyleboxOverride("panel", CreateTooltipStyle(CopiedColor));
        ShowAndPositionTooltip();

        SceneTree? tree = _screen.GetTree();
        if (tree == null)
        {
            return;
        }

        SceneTreeTimer timer = tree.CreateTimer(CopiedFeedbackSeconds);
        timer.Timeout += () =>
        {
            if (generation != _copiedGeneration || !GodotObject.IsInstanceValid(_screen) || !GodotObject.IsInstanceValid(_tooltipPanel))
            {
                return;
            }

            ApplyDefaultTooltipAppearance();
            if (_isHovered && HasCopyableSeed())
            {
                ShowAndPositionTooltip();
            }
            else
            {
                _tooltipPanel.Visible = false;
            }
        };
    }

    private void ApplyDefaultTooltipAppearance()
    {
        _tooltipLabel.Text = HoverHintText;
        _tooltipLabel.AddThemeColorOverride("font_color", CreamColor);
        _tooltipPanel.AddThemeStyleboxOverride("panel", CreateTooltipStyle(TooltipBorderColor));
    }

    private void ShowAndPositionTooltip()
    {
        if (_seedLabel == null || !GodotObject.IsInstanceValid(_seedLabel))
        {
            return;
        }

        _tooltipPanel.ResetSize();
        Vector2 tooltipSize = _tooltipPanel.GetCombinedMinimumSize();
        Rect2 seedRect = _seedLabel.GetGlobalRect();

        float contentWidth = _seedLabel.GetContentWidth();
        float textLeftX = contentWidth > 0f
            ? seedRect.End.X - contentWidth
            : seedRect.Position.X;

        const float gap = 10f;
        float x = textLeftX - tooltipSize.X - gap;
        float y = seedRect.Position.Y + (seedRect.Size.Y - tooltipSize.Y) * 0.5f;

        Rect2 viewportRect = _screen.GetViewportRect();
        x = Mathf.Clamp(x, 8f, Math.Max(8f, viewportRect.Size.X - tooltipSize.X - 8f));
        y = Mathf.Clamp(y, 8f, Math.Max(8f, viewportRect.Size.Y - tooltipSize.Y - 8f));

        _tooltipPanel.GlobalPosition = new Vector2(x, y);
        _tooltipPanel.Visible = true;
    }

    private bool HasCopyableSeed() => !string.IsNullOrWhiteSpace(_getCurrentSeed());

    private static StyleBoxFlat CreateTooltipStyle(Color borderColor) => new()
    {
        BgColor = TooltipBgColor,
        BorderColor = borderColor,
        BorderWidthLeft = 2,
        BorderWidthTop = 2,
        BorderWidthRight = 2,
        BorderWidthBottom = 2,
        CornerRadiusTopLeft = 6,
        CornerRadiusTopRight = 6,
        CornerRadiusBottomLeft = 6,
        CornerRadiusBottomRight = 6,
        ContentMarginLeft = 12,
        ContentMarginTop = 5,
        ContentMarginRight = 12,
        ContentMarginBottom = 5,
        ShadowColor = new Color(0f, 0f, 0f, 0.55f),
        ShadowSize = 8
    };
}
