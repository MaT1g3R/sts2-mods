using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace Sts2Calculator;

[ModInitializer("Initialize")]
public static class Sts2CalculatorEntry
{
	public const string ModId = "Sts2Calculator";

	public static void Initialize()
	{
		try
		{
			var harmony = new Harmony($"com.vmservice.{ModId.ToLowerInvariant()}");
			harmony.PatchAll();
			Log.Info($"[{ModId}] Initialized successfully.");
		}
		catch (Exception ex)
		{
			Log.Error($"[{ModId}] Failed to initialize: {ex}");
		}
	}
}
