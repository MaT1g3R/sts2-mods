using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using RunHistoryPlus.Patches;
using RunHistoryPlus.UI;

namespace RunHistoryPlus;

[ModInitializer("Initialize")]
public static class RunHistoryPlusEntry
{
    public const string ModId = "RunHistoryPlus";
    private const string HarmonyId = "com.vmservice.runhistoryplus";

    private static Harmony? _harmony;

    public static void Initialize()
    {
        try
        {
            _harmony?.UnpatchAll(HarmonyId);
            _harmony = new Harmony(HarmonyId);
            _harmony.PatchAll(typeof(RunHistoryPlusEntry).Assembly);
            ReattachToLiveScreens();
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
            RunHistoryFilterBarRegistry.DisposeAll();
            _harmony?.UnpatchAll(HarmonyId);
            _harmony = null;
            Log.Info($"[{ModId}] Shut down cleanly.");
        }
        catch (Exception ex)
        {
            Log.Error($"[{ModId}] Error during shutdown: {ex}");
        }
    }

    private static void ReattachToLiveScreens()
    {
        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null)
        {
            return;
        }

        foreach (NRunHistory screen in FindNodes<NRunHistory>(tree.Root))
        {
            RunHistoryFilterBar bar = RunHistoryFilterBarRegistry.GetOrCreate(screen);
            bar.ReattachToLiveScreen();
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
            Node child = root.GetChild(i);
            foreach (T found in FindNodes<T>(child))
            {
                yield return found;
            }
        }
    }
}
