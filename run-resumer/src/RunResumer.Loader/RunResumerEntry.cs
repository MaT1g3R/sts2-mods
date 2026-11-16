using System.Reflection;
using System.Runtime.Loader;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace RunResumer;

[ModInitializer("Initialize")]
public static class RunResumerEntry
{
    public static void Initialize()
    {
        AssemblyResolver.Initialize();

        string? modDir = Path.GetDirectoryName(typeof(RunResumerEntry).Assembly.Location);
        if (string.IsNullOrEmpty(modDir))
        {
            throw new InvalidOperationException("RunResumer assembly directory could not be determined.");
        }

        string corePath = Path.Combine(modDir, "RunResumer.Core.dll");
        if (!File.Exists(corePath))
        {
            throw new FileNotFoundException("RunResumer.Core.dll not found next to RunResumer.dll.", corePath);
        }

        AssemblyLoadContext? loadContext = AssemblyLoadContext.GetLoadContext(typeof(RunResumerEntry).Assembly);
        Assembly coreAssembly = loadContext != null
            ? loadContext.LoadFromAssemblyPath(corePath)
            : Assembly.LoadFrom(corePath);

        Type? entryType = coreAssembly.GetType("RunResumer.RunResumerCoreEntry", throwOnError: true);
        MethodInfo? initMethod = entryType?.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static);
        if (initMethod == null)
        {
            throw new MissingMethodException("RunResumer.RunResumerCoreEntry", "Initialize");
        }

        initMethod.Invoke(null, null);
        Log.Info("[RunResumer] Loader finished core initialization");
    }
}
