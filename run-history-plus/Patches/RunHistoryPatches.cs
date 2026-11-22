using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using RunHistoryPlus.UI;

namespace RunHistoryPlus.Patches;

internal static class RunHistoryFilterBarRegistry
{
    private static readonly ConditionalWeakTable<NRunHistory, RunHistoryFilterBar> Bars = new();

    public static RunHistoryFilterBar GetOrCreate(NRunHistory screen)
    {
        if (!Bars.TryGetValue(screen, out RunHistoryFilterBar? bar))
        {
            bar = new RunHistoryFilterBar(screen);
            Bars.Add(screen, bar);
        }

        return bar;
    }

    public static bool TryGet(NRunHistory screen, out RunHistoryFilterBar? bar)
    {
        return Bars.TryGetValue(screen, out bar);
    }
}

[HarmonyPatch(typeof(NRunHistory), nameof(NRunHistory._Ready))]
internal static class RunHistoryReadyPatch
{
    public static void Postfix(NRunHistory __instance)
    {
        try
        {
            RunHistoryFilterBarRegistry.GetOrCreate(__instance);
        }
        catch (Exception ex)
        {
            Log.Error($"[RunHistoryPlus] Error attaching filter bar in NRunHistory._Ready: {ex}");
        }
    }
}

[HarmonyPatch(typeof(NRunHistory), nameof(NRunHistory.OnSubmenuOpened))]
internal static class RunHistoryOpenedPatch
{
    public static void Postfix(NRunHistory __instance)
    {
        try
        {
            RunHistoryFilterBar bar = RunHistoryFilterBarRegistry.GetOrCreate(__instance);
            bar.OnSubmenuOpened();
        }
        catch (Exception ex)
        {
            Log.Error($"[RunHistoryPlus] Error refreshing filters in NRunHistory.OnSubmenuOpened: {ex}");
        }
    }
}

[HarmonyPatch(typeof(NRunHistory), "RefreshAndSelectRun", new[] { typeof(int) })]
internal static class RunHistorySelectRunPatch
{
    public static bool Prefix(NRunHistory __instance, List<string> ____runNames, ref Task __result)
    {
        if (____runNames.Count == 0 && RunHistoryFilterBarRegistry.TryGet(__instance, out RunHistoryFilterBar? bar) && bar != null)
        {
            bar.ShowEmptyState();
            __result = Task.CompletedTask;
            return false;
        }

        return true;
    }

    public static void Postfix(NRunHistory __instance, int index)
    {
        try
        {
            if (RunHistoryFilterBarRegistry.TryGet(__instance, out RunHistoryFilterBar? bar) && bar != null)
            {
                bar.SyncSelectedRunLabel(index);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[RunHistoryPlus] Error syncing run selector label: {ex}");
        }
    }
}

[HarmonyPatch(typeof(NRunHistory), "OnSubmenuHidden")]
internal static class RunHistoryHiddenPatch
{
    public static void Postfix(NRunHistory __instance)
    {
        try
        {
            if (RunHistoryFilterBarRegistry.TryGet(__instance, out RunHistoryFilterBar? bar) && bar != null)
            {
                bar.OnSubmenuHidden();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[RunHistoryPlus] Error hiding filter popups: {ex}");
        }
    }
}

[HarmonyPatch(typeof(NRunHistory), nameof(NRunHistory._Input))]
internal static class RunHistoryInputPatch
{
    public static bool Prefix(NRunHistory __instance, InputEvent inputEvent)
    {
        try
        {
            if (RunHistoryFilterBarRegistry.TryGet(__instance, out RunHistoryFilterBar? bar) && bar != null)
            {
                if (bar.HandleKeyInput(inputEvent))
                {
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[RunHistoryPlus] Error handling keyboard input in Run History: {ex}");
        }

        return true;
    }
}
