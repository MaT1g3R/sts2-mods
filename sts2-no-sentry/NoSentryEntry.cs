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
    public static void Initialize()
    {
        new Harmony("com.vmservice.nosentry").PatchAll();

        // SentryBootstrap._EnterTree runs before ModManager.Initialize; close both layers now.
        SentryService.Shutdown();
        if (Sentry.Godot.SentrySdk.IsEnabled)
        {
            Sentry.Godot.SentrySdk.Close();
        }

        Log.Info("[NoSentry] Sentry reporting disabled");
    }
}
