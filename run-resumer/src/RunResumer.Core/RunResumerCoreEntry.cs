using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using RunResumer.Patches;

[assembly: InternalsVisibleTo("RunResumer.Tests")]

namespace RunResumer;

public static class RunResumerCoreEntry
{
    public const string HarmonyId = "com.vmservice.runresumer";
    private static int _initialized;
    private static Harmony? _harmony;

    public static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
        {
            return;
        }

        _harmony = new Harmony(HarmonyId);
        _harmony.PatchAll(typeof(RunResumerCoreEntry).Assembly);
        ReattachToLiveNodes();
        Log.Info("[RunResumer] Initialized");
    }

    public static void Shutdown()
    {
        if (Interlocked.Exchange(ref _initialized, 0) == 0)
        {
            return;
        }

        try
        {
            RunHistoryShareUiHelper.DetachAll();
            MapPointHistoryEntryReadyPatch.DetachAll();
            ModSettingsStorageHelper.DetachAll();
            DetachTopBarIndicators();
        }
        catch (Exception ex)
        {
            Log.Error($"[RunResumer] Error detaching UI during shutdown: {ex}");
        }

        _harmony?.UnpatchAll(HarmonyId);
        _harmony = null;
        Log.Info("[RunResumer] Shut down cleanly");
    }

    private static void ReattachToLiveNodes()
    {
        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null)
        {
            return;
        }

        foreach (Node node in WalkTree(tree.Root))
        {
            switch (node)
            {
                case NRunHistory historyScreen:
                    RunHistoryShareUiHelper.ReattachToScreen(historyScreen);
                    break;
                case NMapPointHistoryEntry entry:
                    MapPointHistoryEntryReadyPatch.AttachEntry(entry);
                    break;
                case NTopBar topBar:
                    TopBarReplayIndicatorHelper.SyncIndicator(topBar);
                    break;
                case NModdingScreen moddingScreen:
                    ModSettingsStorageHelper.ReattachToLiveModdingScreen(moddingScreen);
                    break;
            }
        }
    }

    private static void DetachTopBarIndicators()
    {
        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null)
        {
            return;
        }

        foreach (Node node in WalkTree(tree.Root))
        {
            if (node is NTopBar topBar)
            {
                TopBarReplayIndicatorHelper.DetachFrom(topBar);
            }
        }
    }

    private static IEnumerable<Node> WalkTree(Node root)
    {
        yield return root;

        int childCount = root.GetChildCount();
        for (int i = 0; i < childCount; i++)
        {
            foreach (Node child in WalkTree(root.GetChild(i)))
            {
                yield return child;
            }
        }
    }
}
