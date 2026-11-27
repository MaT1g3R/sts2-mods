using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using Sts2Calculator.UI;

namespace Sts2Calculator.Patches;

internal static class TopBarPanelRegistry
{
	private static readonly ConditionalWeakTable<NTopBar, CalculatorPanel> _panels = new();
	private static readonly List<WeakReference<NTopBar>> _tracked = new();

	public static void Register(NTopBar topBar, CalculatorPanel panel)
	{
		if (!_panels.TryGetValue(topBar, out _))
		{
			_tracked.Add(new WeakReference<NTopBar>(topBar));
		}
		_panels.AddOrUpdate(topBar, panel);
	}

	public static bool TryGet(NTopBar topBar, out CalculatorPanel? panel) => _panels.TryGetValue(topBar, out panel);

	public static List<NTopBar> TakeAllLive()
	{
		var live = new List<NTopBar>();
		for (int i = _tracked.Count - 1; i >= 0; i--)
		{
			if (_tracked[i].TryGetTarget(out NTopBar? topBar) && GodotObject.IsInstanceValid(topBar))
			{
				live.Add(topBar);
				_panels.Remove(topBar);
			}
		}
		_tracked.Clear();
		return live;
	}
}

/// <summary>
/// Injects the Calculator TopBar button to the left of the Map and Deck buttons
/// and attaches the floating Calculator panel.
/// </summary>
[HarmonyPatch(typeof(NTopBar), nameof(NTopBar._Ready))]
public static class TopBarReadyPatch
{
	public static void Postfix(NTopBar __instance)
	{
		AttachToTopBar(__instance);
	}

	internal static void AttachToTopBar(NTopBar topBar)
	{
		try
		{
			var rightAlignedStuff = topBar.Map?.GetParent() as HBoxContainer
				?? topBar.GetNodeOrNull<HBoxContainer>("RightAlignedStuff");

			if (rightAlignedStuff == null)
			{
				Log.Warn($"[{Sts2CalculatorEntry.ModId}] Could not find RightAlignedStuff in NTopBar.");
				return;
			}

			if (rightAlignedStuff.GetNodeOrNull<Control>(CalculatorTopBarButton.NodeName) != null)
			{
				return;
			}

			var calcButton = new CalculatorTopBarButton();
			var calcPanel = new CalculatorPanel();
			TopBarPanelRegistry.Register(topBar, calcPanel);

			// Expand RightAlignedStuff's left offset by 80px so all right-aligned items (Timer, SaveIndicator, Calculator, Map, Deck, Pause) fit cleanly without overlapping
			rightAlignedStuff.OffsetLeft -= 80f;

			int mapIndex = topBar.Map != null ? topBar.Map.GetIndex() : rightAlignedStuff.GetChildCount();
			rightAlignedStuff.AddChildSafely(calcButton.Root);
			rightAlignedStuff.MoveChild(calcButton.Root, mapIndex);

			topBar.AddChildSafely(calcPanel.Root);

			calcButton.Pressed += () => calcPanel.Toggle(calcButton.Root);
			calcPanel.VisibilityChanged += calcButton.SetOpenState;
		}
		catch (Exception ex)
		{
			Log.Error($"[{Sts2CalculatorEntry.ModId}] Failed to inject Calculator button into NTopBar: {ex}");
		}
	}

	internal static void DetachAll()
	{
		foreach (NTopBar topBar in TopBarPanelRegistry.TakeAllLive())
		{
			var rightAlignedStuff = topBar.Map?.GetParent() as HBoxContainer
				?? topBar.GetNodeOrNull<HBoxContainer>("RightAlignedStuff");

			Control? btn = rightAlignedStuff?.GetNodeOrNull<Control>(CalculatorTopBarButton.NodeName);
			if (btn != null && GodotObject.IsInstanceValid(btn))
			{
				rightAlignedStuff!.RemoveChild(btn);
				btn.QueueFree();
				rightAlignedStuff.OffsetLeft += 80f;
			}

			Control? panel = topBar.GetNodeOrNull<Control>(CalculatorPanel.NodeName);
			if (panel != null && GodotObject.IsInstanceValid(panel))
			{
				topBar.RemoveChild(panel);
				panel.QueueFree();
			}
		}
	}
}

/// <summary>
/// Closes the calculator panel when the TopBar animates off-screen (e.g. pause menu or inspecting a teammate in multiplayer).
/// </summary>
[HarmonyPatch(typeof(NTopBar), nameof(NTopBar.AnimHide))]
public static class TopBarAnimHidePatch
{
	public static void Postfix(NTopBar __instance)
	{
		if (TopBarPanelRegistry.TryGet(__instance, out CalculatorPanel? panel) && panel != null && panel.IsOpen)
		{
			panel.SetOpen(false);
		}
	}
}

/// <summary>
/// Releases focus from the calculator input box when the user clicks outside the calculator panel,
/// so hotkeys, hand card shortcuts, and the multiplayer reaction wheel resume working immediately.
/// </summary>
[HarmonyPatch(typeof(NTopBar), nameof(NTopBar._Input))]
public static class TopBarInputPatch
{
	public static void Postfix(NTopBar __instance, InputEvent inputEvent)
	{
		if (inputEvent is InputEventMouseButton { Pressed: true }
			&& TopBarPanelRegistry.TryGet(__instance, out CalculatorPanel? panel)
			&& panel != null
			&& panel.IsOpen
			&& panel.IsInputEditing()
			&& !panel.ContainsGlobalMousePosition())
		{
			panel.ReleaseInputFocus();
		}
	}
}

/// <summary>
/// Prevents number keys (1-0) typed into the calculator input box from triggering hand card play shortcuts during combat.
/// </summary>
[HarmonyPatch(typeof(NPlayerHand), nameof(NPlayerHand._UnhandledInput))]
public static class PlayerHandInputGuardPatch
{
	public static bool Prefix(NPlayerHand __instance)
	{
		if (CalculatorPanel.IsCalculatorInputEditing(__instance.GetViewport()))
		{
			return false;
		}
		return true;
	}
}
