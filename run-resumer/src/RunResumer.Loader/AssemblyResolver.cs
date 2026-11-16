using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace RunResumer;

internal static class AssemblyResolver
{
    private static int _initialized;

#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Initialize()
#pragma warning restore CA2255
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
        {
            return;
        }

        string? modDir = Path.GetDirectoryName(typeof(AssemblyResolver).Assembly.Location);
        if (string.IsNullOrEmpty(modDir))
        {
            return;
        }

        AssemblyLoadContext? loadContext = AssemblyLoadContext.GetLoadContext(typeof(AssemblyResolver).Assembly);
        if (loadContext != null)
        {
            loadContext.Resolving += (_, name) => ResolveFromModDir(modDir, name);
        }

        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            if (string.IsNullOrEmpty(args.Name))
            {
                return null;
            }
            return ResolveFromModDir(modDir, new AssemblyName(args.Name));
        };
    }

    private static Assembly? ResolveFromModDir(string modDir, AssemblyName assemblyName)
    {
        if (string.IsNullOrEmpty(assemblyName.Name))
        {
            return null;
        }

        string candidatePath = Path.Combine(modDir, assemblyName.Name + ".dll");
        if (!File.Exists(candidatePath))
        {
            return null;
        }

        AssemblyLoadContext? loadContext = AssemblyLoadContext.GetLoadContext(typeof(AssemblyResolver).Assembly);
        return loadContext != null
            ? loadContext.LoadFromAssemblyPath(candidatePath)
            : Assembly.LoadFrom(candidatePath);
    }
}
