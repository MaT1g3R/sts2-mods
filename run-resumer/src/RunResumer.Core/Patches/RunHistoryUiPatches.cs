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
        public Callable PressedCallable;
        public Callable ReleasedCallable;
    }

    private static readonly ConditionalWeakTable<NMapPointHistoryEntry, RightClickState> States = new();
    private static readonly List<WeakReference<NMapPointHistoryEntry>> TrackedEntries = new();

    private static readonly AccessTools.FieldRef<NMapPointHistoryEntry, RunHistory> RunHistoryRef =
        AccessTools.FieldRefAccess<NMapPointHistoryEntry, RunHistory>("_runHistory");

    private static readonly Func<NClickableControl, bool> IsFocusedGetter =
        AccessTools.MethodDelegate<Func<NClickableControl, bool>>(
            AccessTools.PropertyGetter(typeof(NClickableControl), "IsFocused"));

    public static void Postfix(NMapPointHistoryEntry __instance)
    {
        AttachEntry(__instance);
    }

    internal static void AttachEntry(NMapPointHistoryEntry entry)
    {
        if (States.TryGetValue(entry, out _))
        {
            return;
        }

        RightClickState state = new RightClickState();
        state.PressedCallable = Callable.From<InputEvent>(ev =>
        {
            if (ev is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Right)
            {
                state.PressedInside = entry.IsEnabled && entry.IsVisibleInTree() && IsFocusedGetter(entry);
            }
        });
        state.ReleasedCallable = Callable.From<InputEvent>(ev =>
        {
            if (ev is not InputEventMouseButton mb || mb.ButtonIndex != MouseButton.Right)
            {
                return;
            }

            bool shouldLaunch = state.PressedInside && entry.IsEnabled && entry.IsVisibleInTree() && IsFocusedGetter(entry);
            state.PressedInside = false;
            if (!shouldLaunch)
            {
                return;
            }

            try
            {
                RunHistory history = RunHistoryRef(entry);
                ReplaySession.TryLaunchFromHistory(history, entry.FloorNum);
            }
            catch (Exception ex)
            {
                Log.Error($"[RunResumer] Error handling right-click on floor {entry.FloorNum}: {ex}");
            }
        });

        States.Add(entry, state);
        TrackedEntries.Add(new WeakReference<NMapPointHistoryEntry>(entry));

        entry.Connect(NClickableControl.SignalName.MousePressed, state.PressedCallable);
        entry.Connect(NClickableControl.SignalName.MouseReleased, state.ReleasedCallable);
    }

    internal static void DetachAll()
    {
        for (int i = TrackedEntries.Count - 1; i >= 0; i--)
        {
            if (TrackedEntries[i].TryGetTarget(out NMapPointHistoryEntry? entry) &&
                GodotObject.IsInstanceValid(entry) &&
                States.TryGetValue(entry, out RightClickState? state))
            {
                if (entry.IsConnected(NClickableControl.SignalName.MousePressed, state.PressedCallable))
                {
                    entry.Disconnect(NClickableControl.SignalName.MousePressed, state.PressedCallable);
                }
                if (entry.IsConnected(NClickableControl.SignalName.MouseReleased, state.ReleasedCallable))
                {
                    entry.Disconnect(NClickableControl.SignalName.MouseReleased, state.ReleasedCallable);
                }
                States.Remove(entry);
            }
        }

        TrackedEntries.Clear();
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
