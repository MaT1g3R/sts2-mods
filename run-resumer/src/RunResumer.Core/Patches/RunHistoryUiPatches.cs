using System;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using MegaCrit.Sts2.Core.Runs;
using RunResumer.Replay;
using RunResumer.Storage;

namespace RunResumer.Patches;

[HarmonyPatch(typeof(NMapPointHistoryEntry), nameof(NMapPointHistoryEntry._Ready))]
internal static class MapPointHistoryEntryReadyPatch
{
    private sealed class RightClickState
    {
        public bool PressedInside;
    }

    private static readonly ConditionalWeakTable<NMapPointHistoryEntry, RightClickState> States = new();

    private static readonly AccessTools.FieldRef<NMapPointHistoryEntry, RunHistory> RunHistoryRef =
        AccessTools.FieldRefAccess<NMapPointHistoryEntry, RunHistory>("_runHistory");

    private static readonly Func<NClickableControl, bool> IsFocusedGetter =
        AccessTools.MethodDelegate<Func<NClickableControl, bool>>(
            AccessTools.PropertyGetter(typeof(NClickableControl), "IsFocused"));

    public static void Postfix(NMapPointHistoryEntry __instance)
    {
        RightClickState state = States.GetOrCreateValue(__instance);

        __instance.Connect(
            NClickableControl.SignalName.MousePressed,
            Callable.From<InputEvent>(ev =>
            {
                if (ev is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Right)
                {
                    state.PressedInside = __instance.IsEnabled && __instance.IsVisibleInTree() && IsFocusedGetter(__instance);
                }
            }));

        __instance.Connect(
            NClickableControl.SignalName.MouseReleased,
            Callable.From<InputEvent>(ev =>
            {
                if (ev is not InputEventMouseButton mb || mb.ButtonIndex != MouseButton.Right)
                {
                    return;
                }

                bool shouldLaunch = state.PressedInside && __instance.IsEnabled && __instance.IsVisibleInTree() && IsFocusedGetter(__instance);
                state.PressedInside = false;
                if (!shouldLaunch)
                {
                    return;
                }

                try
                {
                    RunHistory history = RunHistoryRef(__instance);
                    ReplaySession.TryLaunchFromHistory(history, __instance.FloorNum);
                }
                catch (Exception ex)
                {
                    Log.Error($"[RunResumer] Error handling right-click on floor {__instance.FloorNum}: {ex}");
                }
            }));
    }
}

[HarmonyPatch(typeof(NHoverTipSet), nameof(NHoverTipSet.CreateAndShowMapPointHistory))]
internal static class MapPointHistoryHoverTipHintPatch
{
    private static readonly AccessTools.FieldRef<NMapPointHistoryEntry, RunHistory> RunHistoryRef =
        AccessTools.FieldRefAccess<NMapPointHistoryEntry, RunHistory>("_runHistory");

    public static void Postfix(Control owner, NMapPointHistoryHoverTip historyHoverTip)
    {
        if (owner is not NMapPointHistoryEntry entry)
        {
            return;
        }

        try
        {
            RunHistory history = RunHistoryRef(entry);
            bool hasSnapshot = RunArchiveStorage.HasFloor(history, entry.FloorNum);

            string text;
            Color color;
            if (hasSnapshot && ReplaySession.IsInActiveRun())
            {
                text = "Return to Main Menu to replay";
                color = new Color(0.68f, 0.66f, 0.62f);
            }
            else if (hasSnapshot)
            {
                text = $"Right-click to replay from Floor {entry.FloorNum}";
                color = new Color(0.937255f, 0.784314f, 0.317647f);
            }
            else if (history.Players.Count > 1)
            {
                text = "Replay unavailable (multiplayer)";
                color = new Color(0.68f, 0.66f, 0.62f);
            }
            else
            {
                text = "No replay snapshot saved";
                color = new Color(0.68f, 0.66f, 0.62f);
            }

            VBoxContainer? topContainer = historyHoverTip.GetNodeOrNull<VBoxContainer>("TextContainer/TopContainer");
            if (topContainer == null)
            {
                return;
            }

            Label? template = historyHoverTip.GetNodeOrNull<Label>("%Title");
            Label hintLabel = new Label
            {
                Name = "RunResumerHint",
                Text = text,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };

            if (template != null)
            {
                Font? font = template.GetThemeFont("font");
                if (font != null)
                {
                    hintLabel.AddThemeFontOverride("font", font);
                }
                hintLabel.AddThemeFontSizeOverride("font_size", 18);
                hintLabel.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.35f));
                hintLabel.AddThemeConstantOverride("shadow_offset_x", 2);
                hintLabel.AddThemeConstantOverride("shadow_offset_y", 2);
            }

            hintLabel.AddThemeColorOverride("font_color", color);
            topContainer.AddChild(hintLabel);
        }
        catch (Exception ex)
        {
            Log.Warn($"[RunResumer] Failed to append hover tip hint: {ex.Message}");
        }
    }
}
