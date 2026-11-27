using System;
using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using Sts2Calculator.Patches;

namespace Sts2Calculator;

[ModInitializer("Initialize")]
public static class Sts2CalculatorEntry
{
	public const string ModId = "Sts2Calculator";
	private const string HarmonyId = "com.vmservice.sts2calculator";

	private static Harmony? _harmony;

	public static void Initialize()
	{
		try
		{
			_harmony?.UnpatchAll(HarmonyId);
			_harmony = new Harmony(HarmonyId);
			_harmony.PatchAll(typeof(Sts2CalculatorEntry).Assembly);
			ReattachToLiveTopBars();
			Log.Info($"[{ModId}] Initialized successfully.");
		}
		catch (Exception ex)
		{
			Log.Error($"[{ModId}] Failed to initialize: {ex}");
		}
	}

	public static void Shutdown()
	{
		try
		{
			TopBarReadyPatch.DetachAll();
			_harmony?.UnpatchAll(HarmonyId);
			_harmony = null;
			Log.Info($"[{ModId}] Shut down cleanly.");
		}
		catch (Exception ex)
		{
			Log.Error($"[{ModId}] Error during shutdown: {ex}");
		}
	}

	private static void ReattachToLiveTopBars()
	{
		if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null)
		{
			return;
		}

		foreach (NTopBar topBar in FindNodes<NTopBar>(tree.Root))
		{
			TopBarReadyPatch.AttachToTopBar(topBar);
		}
	}

	private static IEnumerable<T> FindNodes<T>(Node root) where T : Node
	{
		if (root is T match)
		{
			yield return match;
		}

		int childCount = root.GetChildCount();
		for (int i = 0; i < childCount; i++)
		{
			foreach (T found in FindNodes<T>(root.GetChild(i)))
			{
				yield return found;
			}
		}
	}
}
