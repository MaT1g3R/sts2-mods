using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

[assembly: InternalsVisibleTo("RunResumer.Tests")]

namespace RunResumer;

[ModInitializer("Initialize")]
public static class RunResumerEntry
{
    public const string HarmonyId = "com.vmservice.runresumer";

    public static void Initialize()
    {
        AssemblyResolver.Initialize();
        new Harmony(HarmonyId).PatchAll(typeof(RunResumerEntry).Assembly);
        Log.Info("[RunResumer] Initialized");
    }
}
