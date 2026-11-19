using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Daily;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using MegaCrit.Sts2.Core.Saves.Runs;
using RunResumer.Replay;
using RunResumer.Storage;

namespace RunResumer.Patches;

[HarmonyPatch(typeof(RunManager), "InitializeShared")]
internal static class RunManagerInitializeSharedPatch
{
    public static void Prefix(ref bool shouldSave)
    {
        if (ReplaySession.IsActive)
        {
            shouldSave = false;
        }
    }
}

[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.IncrementNumReloads))]
internal static class SaveManagerIncrementNumReloadsPatch
{
    public static bool Prefix(ref Task __result)
    {
        if (!ReplaySession.IsActive)
        {
            return true;
        }

        __result = Task.CompletedTask;
        return false;
    }
}

[HarmonyPatch(typeof(DailyRunUtility), nameof(DailyRunUtility.UploadScore), typeof(DateTimeOffset), typeof(int), typeof(List<SerializablePlayer>))]
internal static class DailyRunUploadScorePatch
{
    public static bool Prefix(ref Task __result)
    {
        if (!ReplaySession.IsActive)
        {
            return true;
        }

        __result = Task.CompletedTask;
        return false;
    }
}

[HarmonyPatch(typeof(NRun), nameof(NRun.ShowGameOverScreen))]
internal static class ShowGameOverScreenPatch
{
    public static bool Prefix()
    {
        if (!ReplaySession.IsActive)
        {
            return true;
        }

        NCapstoneContainer.Instance?.Close();
        NMapScreen.Instance?.Close();
        ReplaySession.ReturnToHistoryAfterRunEnd();
        return false;
    }
}

[HarmonyPatch(typeof(NGame), nameof(NGame.GoToTimelineAfterRun))]
internal static class GoToTimelineAfterRunPatch
{
    public static bool Prefix(NGame __instance, ref Task __result)
    {
        if (!ReplaySession.IsActive)
        {
            return true;
        }

        __result = __instance.ReturnToMainMenuAfterRun();
        return false;
    }
}

[HarmonyPatch(typeof(NGame), "LoadMainMenu")]
internal static class LoadMainMenuPatch
{
    public static void Prefix()
    {
        ReplaySession.MarkReturningToMenu();
    }
}

[HarmonyPatch(typeof(NMainMenu), nameof(NMainMenu._Ready))]
internal static class MainMenuReadyReturnPatch
{
    private static readonly AccessTools.FieldRef<NRunHistory, List<string>> RunNamesRef =
        AccessTools.FieldRefAccess<NRunHistory, List<string>>("_runNames");

    private static readonly Func<NRunHistory, int, Task> RefreshAndSelectRunDelegate =
        AccessTools.MethodDelegate<Func<NRunHistory, int, Task>>(
            AccessTools.Method(typeof(NRunHistory), "RefreshAndSelectRun", new[] { typeof(int) }));

    public static void Postfix(NMainMenu __instance)
    {
        long? pendingStartTime = ReplaySession.PendingReturnStartTime;
        if (!pendingStartTime.HasValue)
        {
            return;
        }

        ReplaySession.PendingReturnStartTime = null;
        long targetStartTime = pendingStartTime.Value;

        Callable.From(() =>
        {
            try
            {
                if (!GodotObject.IsInstanceValid(__instance) || !NRunHistory.CanBeShown())
                {
                    return;
                }

                __instance.OpenCompendiumSubmenu();
                NRunHistory historyScreen = __instance.SubmenuStack.PushSubmenuType<NRunHistory>();
                List<string> runNames = RunNamesRef(historyScreen);
                int index = runNames.IndexOf($"{targetStartTime}.run");
                if (index > 0)
                {
                    TaskHelper.RunSafely(RefreshAndSelectRunDelegate(historyScreen, index));
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[RunResumer] Failed to reopen Run History after replay: {ex}");
            }
        }).CallDeferred();
    }
}

[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.InitProfileId))]
internal static class SaveManagerInitProfilePatch
{
    public static void Postfix()
    {
        RunArchiveStorage.InvalidateCache();
        RunArchiveStorage.CleanupOrphans();
    }
}

[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SwitchProfileId))]
internal static class SaveManagerSwitchProfilePatch
{
    public static void Postfix()
    {
        RunArchiveStorage.InvalidateCache();
        RunArchiveStorage.CleanupOrphans();
    }
}

[HarmonyPatch(typeof(RunHistorySaveManager), nameof(RunHistorySaveManager.SaveHistory))]
internal static class RunHistorySavedPatch
{
    public static void Postfix()
    {
        RunArchiveStorage.InvalidateCache();
    }
}
