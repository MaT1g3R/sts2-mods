using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Debug;
using Sentry;
using Sentry.Godot;

namespace NoSentry;

/// <summary>
/// Harmony patches that block all Sentry SDK initialization, lifecycle, and event capture.
/// </summary>
[HarmonyPatch]
internal static class SentryPatches
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(SentryService), nameof(SentryService.Initialize))]
    private static bool SkipInitialize() => false;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SentryService), nameof(SentryService.InitializeForTesting))]
    private static bool SkipInitializeForTesting() => false;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SentryService), nameof(SentryService.AfterGameInit))]
    private static bool SkipAfterGameInit() => false;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SentryService), "ShouldStayAliveAfterInit")]
    private static bool ForceStayAliveFalse(ref bool __result)
    {
        __result = false;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SentryService), nameof(SentryService.CaptureException), new[] { typeof(Exception) })]
    private static bool SkipCaptureException() => false;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SentryService), nameof(SentryService.CaptureException), new[] { typeof(Exception), typeof(Action<Scope>) })]
    private static bool SkipCaptureExceptionWithScope() => false;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SentryService), nameof(SentryService.CaptureMessage))]
    private static bool SkipCaptureMessage() => false;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SentryService), nameof(SentryService.AddBreadcrumb))]
    private static bool SkipAddBreadcrumb() => false;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SentryService), nameof(SentryService.SetTag))]
    private static bool SkipSetTag() => false;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SentryService), nameof(SentryService.SetExtra))]
    private static bool SkipSetExtra() => false;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Sentry.Godot.SentrySdk), nameof(Sentry.Godot.SentrySdk.Init), new[] { typeof(Action<SentryGodotOptions>) })]
    private static bool SkipGodotSdkInit() => false;
}
