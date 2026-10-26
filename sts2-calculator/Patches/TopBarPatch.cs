using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using Sts2Calculator.UI;

namespace Sts2Calculator.Patches;

/// <summary>
/// Injects the Calculator TopBar button to the left of the Map and Deck buttons
/// and attaches the floating Calculator panel.
/// </summary>
[HarmonyPatch(typeof(NTopBar), nameof(NTopBar._Ready))]
public static class TopBarReadyPatch
{
	public static void Postfix(NTopBar __instance)
	{
		try
		{
			var rightAlignedStuff = __instance.Map?.GetParent() as HBoxContainer
				?? __instance.GetNodeOrNull<HBoxContainer>("RightAlignedStuff");

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

			// Expand RightAlignedStuff's left offset by 80px so all right-aligned items (Timer, SaveIndicator, Calculator, Map, Deck, Pause) fit cleanly without overlapping
			rightAlignedStuff.OffsetLeft -= 80f;

			int mapIndex = __instance.Map != null ? __instance.Map.GetIndex() : rightAlignedStuff.GetChildCount();
			rightAlignedStuff.AddChildSafely(calcButton.Root);
			rightAlignedStuff.MoveChild(calcButton.Root, mapIndex);

			__instance.AddChildSafely(calcPanel.Root);

			calcButton.Pressed += () => calcPanel.Toggle(calcButton.Root);
			calcPanel.VisibilityChanged += calcButton.SetOpenState;
		}
		catch (Exception ex)
		{
			Log.Error($"[{Sts2CalculatorEntry.ModId}] Failed to inject Calculator button into NTopBar: {ex}");
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
