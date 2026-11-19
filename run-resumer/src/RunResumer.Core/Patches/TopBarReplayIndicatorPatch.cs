using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Runs;
using RunResumer.Replay;

namespace RunResumer.Patches;

internal static class TopBarReplayIndicatorHelper
{
    internal const string ResourceName = "RunResumer.Assets.run_resumer_icon.png";
    internal const string IndicatorNodeName = "RunResumerIndicator";
    private const string HoverTipTable = "static_hover_tips";
    private const string HoverTipTitleKey = "RUN_RESUMER_REPLAY.title";

    private static readonly StyleBoxEmpty BlankFocusStyle = new();
    private static readonly Color HoverTint = new(1.12f, 1.12f, 1.12f, 1f);
    private static Texture2D? _cachedTexture;

    public static void SyncIndicator(NTopBar topBar)
    {
        try
        {
            Control? existing = topBar.LeftContainer.GetNodeOrNull<Control>(IndicatorNodeName);
            if (!ReplaySession.IsActive)
            {
                if (existing != null)
                {
                    existing.Visible = false;
                }
                return;
            }

            if (existing != null)
            {
                existing.Visible = true;
                PositionAfterPotions(topBar, existing);
                return;
            }

            Texture2D? texture = GetOrLoadIconTexture();
            if (texture == null)
            {
                return;
            }

            Control indicator = CreateIndicatorControl(texture);
            topBar.LeftContainer.AddChildSafely(indicator);
            PositionAfterPotions(topBar, indicator);
        }
        catch (Exception ex)
        {
            Log.Warn($"[RunResumer] Failed to update top bar replay indicator: {ex.Message}");
        }
    }

    private static void PositionAfterPotions(NTopBar topBar, Control indicator)
    {
        Control? potionSlotGroup = topBar.PotionContainer?.GetParent() as Control;
        if (potionSlotGroup == null || potionSlotGroup.GetParent() != topBar.LeftContainer)
        {
            potionSlotGroup = topBar.LeftContainer.GetNodeOrNull<Control>("PotionMarginifier");
        }

        if (potionSlotGroup == null || potionSlotGroup.GetParent() != topBar.LeftContainer)
        {
            return;
        }

        int anchorIndex = potionSlotGroup.GetIndex();
        int currentIndex = indicator.GetIndex();
        int targetIndex = currentIndex < anchorIndex ? anchorIndex : anchorIndex + 1;
        if (currentIndex != targetIndex)
        {
            topBar.LeftContainer.MoveChild(indicator, targetIndex);
        }
    }

    public static void AppendToNavigableElements(NTopBar topBar, List<Control> elements)
    {
        Control? indicator = topBar.LeftContainer?.GetNodeOrNull<Control>(IndicatorNodeName);
        if (indicator == null || !indicator.IsVisibleInTree() || elements.Contains(indicator))
        {
            return;
        }

        int roomIconIndex = topBar.RoomIcon != null ? elements.IndexOf(topBar.RoomIcon) : -1;
        if (roomIconIndex >= 0)
        {
            elements.Insert(roomIconIndex, indicator);
        }
        else
        {
            elements.Add(indicator);
        }
    }

    private static Control CreateIndicatorControl(Texture2D texture)
    {
        // Match TopBarPortraitTip footprint (72x80 outer slot, 68x68 icon aligned to y=4..72).
        Control indicator = new Control
        {
            Name = IndicatorNodeName,
            CustomMinimumSize = new Vector2(72f, 80f),
            FocusMode = Control.FocusModeEnum.All,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        indicator.AddThemeStyleboxOverride(ThemeConstants.Control.Focus, BlankFocusStyle);

        TextureRect icon = new TextureRect
        {
            Name = "Icon",
            Texture = texture,
            CustomMinimumSize = new Vector2(68f, 68f),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        icon.SetAnchorsPreset(Control.LayoutPreset.Center);
        icon.OffsetLeft = -34f;
        icon.OffsetTop = -36f;
        icon.OffsetRight = 34f;
        icon.OffsetBottom = 32f;
        indicator.AddChild(icon);

        bool isHovered = false;
        bool isFocused = false;
        bool tipShown = false;

        void RefreshHoverTip()
        {
            bool shouldShow = indicator.IsVisibleInTree() && (isHovered || isFocused);
            if (shouldShow == tipShown)
            {
                return;
            }

            tipShown = shouldShow;
            icon.Modulate = shouldShow ? HoverTint : Colors.White;

            if (shouldShow)
            {
                ShowHoverTip(indicator);
            }
            else
            {
                NHoverTipSet.Remove(indicator);
            }
        }

        indicator.Connect(Control.SignalName.MouseEntered, Callable.From(() =>
        {
            isHovered = true;
            RefreshHoverTip();
        }));
        indicator.Connect(Control.SignalName.MouseExited, Callable.From(() =>
        {
            isHovered = false;
            RefreshHoverTip();
        }));
        indicator.Connect(Control.SignalName.FocusEntered, Callable.From(() =>
        {
            isFocused = true;
            RefreshHoverTip();
        }));
        indicator.Connect(Control.SignalName.FocusExited, Callable.From(() =>
        {
            isFocused = false;
            RefreshHoverTip();
        }));
        indicator.Connect(CanvasItem.SignalName.VisibilityChanged, Callable.From(() =>
        {
            if (!indicator.IsVisibleInTree())
            {
                isHovered = false;
                isFocused = false;
                RefreshHoverTip();
            }
        }));
        indicator.Connect(Node.SignalName.TreeExiting, Callable.From(() =>
        {
            if (tipShown)
            {
                tipShown = false;
                NHoverTipSet.Remove(indicator);
            }
        }));

        return indicator;
    }

    private static void ShowHoverTip(Control indicator)
    {
        try
        {
            EnsureLocalizationRegistered();
            string description = ReplaySession.ReplayedFromFloor is int floorNum
                ? $"Practice replay started from [gold]Floor {floorNum}[/gold].\nSaves, unlocks, and run history are disabled."
                : "Practice replay active.\nSaves, unlocks, and run history are disabled.";

            HoverTip tip = new HoverTip(new LocString(HoverTipTable, HoverTipTitleKey), description);
            NHoverTipSet.CreateAndShow(indicator, tip)
                ?.SetGlobalPosition(indicator.GlobalPosition + new Vector2(0f, indicator.Size.Y + 20f));
        }
        catch (Exception ex)
        {
            Log.Warn($"[RunResumer] Failed to show replay indicator hover tip: {ex.Message}");
        }
    }

    private static void EnsureLocalizationRegistered()
    {
        if (LocManager.Instance == null || LocString.Exists(HoverTipTable, HoverTipTitleKey))
        {
            return;
        }

        LocManager.Instance.GetTable(HoverTipTable).MergeWith(new Dictionary<string, string>
        {
            [HoverTipTitleKey] = "Run Resumer"
        });
    }

    private static Texture2D? GetOrLoadIconTexture()
    {
        if (_cachedTexture != null)
        {
            return _cachedTexture;
        }

        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream == null)
        {
            Log.Warn($"[RunResumer] Embedded icon resource '{ResourceName}' was not found.");
            return null;
        }

        using MemoryStream ms = new MemoryStream();
        stream.CopyTo(ms);

        Image image = new Image();
        Error err = image.LoadPngFromBuffer(ms.ToArray());
        if (err != Error.Ok)
        {
            Log.Warn($"[RunResumer] Failed to decode top bar icon PNG: {err}");
            return null;
        }

        _cachedTexture = ImageTexture.CreateFromImage(image);
        return _cachedTexture;
    }
}

[HarmonyPatch(typeof(NTopBar), nameof(NTopBar.Initialize))]
internal static class TopBarInitializeReplayIndicatorPatch
{
    public static void Postfix(NTopBar __instance, IRunState runState)
    {
        _ = runState;
        TopBarReplayIndicatorHelper.SyncIndicator(__instance);
    }
}

[HarmonyPatch(typeof(NTopBar), nameof(NTopBar.GetAllNavigableElements))]
internal static class TopBarNavigableElementsReplayIndicatorPatch
{
    public static void Postfix(NTopBar __instance, ref List<Control> __result)
    {
        TopBarReplayIndicatorHelper.AppendToNavigableElements(__instance, __result);
    }
}
