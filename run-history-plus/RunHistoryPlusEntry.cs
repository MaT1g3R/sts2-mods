using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace RunHistoryPlus;

[ModInitializer("Initialize")]
public static class RunHistoryPlusEntry
{
    public const string ModId = "RunHistoryPlus";

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
