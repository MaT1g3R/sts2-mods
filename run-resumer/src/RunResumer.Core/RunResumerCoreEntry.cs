using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;

[assembly: InternalsVisibleTo("RunResumer.Tests")]

namespace RunResumer;

public static class RunResumerCoreEntry
{
    public const string HarmonyId = "com.vmservice.runresumer";
    private static int _initialized;

    public static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
        {
            return;
        }

        new Harmony(HarmonyId).PatchAll(typeof(RunResumerCoreEntry).Assembly);
        Log.Info("[RunResumer] Initialized");
    }
}
