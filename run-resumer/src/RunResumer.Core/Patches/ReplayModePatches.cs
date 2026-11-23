using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Daily;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens;
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
    private const int MaxPreloadWaitFrames = 600;

    public static void Postfix(NMainMenu __instance)
    {
        long? pendingStartTime = ReplaySession.PendingReturnStartTime;
        if (!pendingStartTime.HasValue)
        {
            return;
        }

        ReplaySession.PendingReturnStartTime = null;
        long targetStartTime = pendingStartTime.Value;

        TaskHelper.RunSafely(ReopenRunHistoryWhenPreloadedAsync(__instance, targetStartTime));
    }

    private static async Task ReopenRunHistoryWhenPreloadedAsync(NMainMenu mainMenu, long targetStartTime)
    {
        try
        {
            if (!await WaitOneProcessFrameAsync(mainMenu))
            {
                return;
            }

            // Wait for background Common/MainMenu preloading to cache the compendium and
            // run history scenes so NRunHistory.Create() does not record a cache miss in
            // AssetCache._missedCacheAssets (which LoadRoomAssets would later dispose after
            // NRunSubmenuStack binds _runHistoryScreenScene).
            if (PreloadManager.Enabled)
            {
                for (int i = 0; i < MaxPreloadWaitFrames && !AreHistoryPreloadAssetsReady(); i++)
                {
                    if (!GodotObject.IsInstanceValid(mainMenu)
                        || !mainMenu.IsInsideTree()
                        || ReplaySession.IsInActiveRun()
                        || mainMenu.SubmenuStack.SubmenusOpen)
                    {
                        return;
                    }

                    if (!await WaitOneProcessFrameAsync(mainMenu))
                    {
                        return;
                    }
                }
            }

            if (!GodotObject.IsInstanceValid(mainMenu)
                || !mainMenu.IsInsideTree()
                || ReplaySession.IsInActiveRun()
                || mainMenu.SubmenuStack.SubmenusOpen
                || !NRunHistory.CanBeShown())
            {
                return;
            }

            mainMenu.OpenCompendiumSubmenu();
            NRunHistory historyScreen = mainMenu.SubmenuStack.PushSubmenuType<NRunHistory>();
            RunHistoryShareUiHelper.SelectRunInHistory(historyScreen, targetStartTime);
        }
        catch (Exception ex)
        {
            Log.Error($"[RunResumer] Failed to reopen Run History after replay: {ex}");
        }
    }

    private static async Task<bool> WaitOneProcessFrameAsync(NMainMenu mainMenu)
    {
        if (!GodotObject.IsInstanceValid(mainMenu) || !mainMenu.IsInsideTree())
        {
            return false;
        }

        SceneTree? tree = mainMenu.GetTree();
        if (tree == null)
        {
            return false;
        }

        await mainMenu.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        return GodotObject.IsInstanceValid(mainMenu) && mainMenu.IsInsideTree();
    }

    private static bool AreHistoryPreloadAssetsReady()
    {
        foreach (string path in NCompendiumSubmenu.AssetPaths)
        {
            if (!PreloadManager.Cache.ContainsKey(path))
            {
                return false;
            }
        }

        foreach (string path in NRunHistory.AssetPaths)
        {
            if (!PreloadManager.Cache.ContainsKey(path))
            {
                return false;
            }
        }

        return true;
    }
}

[HarmonyPatch(typeof(NRunSubmenuStack), nameof(NRunSubmenuStack.GetSubmenuType), typeof(Type))]
internal static class RunSubmenuStackGetSubmenuTypePatch
{
    private const string RunHistoryScenePath = "res://scenes/screens/run_history_screen/run_history.tscn";

    private static readonly AccessTools.FieldRef<NRunSubmenuStack, PackedScene> RunHistoryScreenSceneRef =
        AccessTools.FieldRefAccess<NRunSubmenuStack, PackedScene>("_runHistoryScreenScene");

    private static readonly AccessTools.FieldRef<NRunSubmenuStack, NRunHistory?> RunHistoryScreenRef =
        AccessTools.FieldRefAccess<NRunSubmenuStack, NRunHistory?>("_runHistoryScreen");

    public static void Prefix(NRunSubmenuStack __instance, Type type)
    {
        if (type != typeof(NRunHistory) || RunHistoryScreenRef(__instance) != null)
        {
            return;
        }

        PackedScene? scene = RunHistoryScreenSceneRef(__instance);
        if (scene != null && GodotObject.IsInstanceValid(scene))
        {
            return;
        }

        PackedScene? reloaded = ResourceLoader.Load<PackedScene>(RunHistoryScenePath, null, ResourceLoader.CacheMode.Reuse);
        if (reloaded != null && GodotObject.IsInstanceValid(reloaded))
        {
            RunHistoryScreenSceneRef(__instance) = reloaded;
            Log.Info("[RunResumer] Reloaded disposed NRunSubmenuStack._runHistoryScreenScene");
        }
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
