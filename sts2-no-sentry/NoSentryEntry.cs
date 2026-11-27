using HarmonyLib;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace NoSentry;

/// <summary>
/// Mod entry point that patches out Sentry initialization/reporting and shuts down any
/// Sentry SDK instance started during early boot.
/// </summary>
[ModInitializer("Initialize")]
public static class NoSentryEntry
{
    private const string HarmonyId = "com.vmservice.nosentry";
    private static Harmony? _harmony;

    public static void Initialize()
    {
        _harmony?.UnpatchAll(HarmonyId);
        _harmony = new Harmony(HarmonyId);
        _harmony.PatchAll(typeof(NoSentryEntry).Assembly);

        // SentryBootstrap._EnterTree runs before ModManager.Initialize; close both layers now.
        SentryService.Shutdown();
        if (Sentry.Godot.SentrySdk.IsEnabled)
        {
            Sentry.Godot.SentrySdk.Close();
        }

        Log.Info("[NoSentry] Sentry reporting disabled");
    }

    public static void Shutdown()
    {
        _harmony?.UnpatchAll(HarmonyId);
        _harmony = null;
        Log.Info("[NoSentry] Shut down cleanly");
    }
}
