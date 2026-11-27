using System.Reflection;
using System.Runtime.Loader;
using MegaCrit.Sts2.Core.Modding;

namespace Sts2HotReloader;

/// <summary>
/// Collectible AssemblyLoadContext that snapshots a dev-mod directory's DLLs and PDBs into memory
/// so disk files are never locked and rebuilt assemblies can be cleanly reloaded and garbage collected.
/// Delegates game/engine assemblies to Godot's host PluginLoadContext so sts2, GodotSharp, and
/// 0Harmony keep a single identity and initialized native interop pointers.
/// </summary>
internal sealed class CollectibleModLoadContext : AssemblyLoadContext
{
    private readonly AssemblyLoadContext? _hostContext;
    private readonly Dictionary<string, (byte[] DllBytes, byte[]? PdbBytes)> _assemblies;

    public CollectibleModLoadContext(string name, string modDirectory)
        : base(name, isCollectible: true)
    {
        _hostContext = GetLoadContext(typeof(ModManager).Assembly);
        _assemblies = new Dictionary<string, (byte[], byte[]?)>(StringComparer.OrdinalIgnoreCase);

        foreach (string dllPath in Directory.EnumerateFiles(modDirectory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            string simpleName = Path.GetFileNameWithoutExtension(dllPath);
            byte[] dllBytes = File.ReadAllBytes(dllPath);

            string pdbPath = Path.ChangeExtension(dllPath, ".pdb");
            byte[]? pdbBytes = File.Exists(pdbPath) ? File.ReadAllBytes(pdbPath) : null;

            _assemblies[simpleName] = (dllBytes, pdbBytes);
        }
    }

    public Assembly LoadEntryAssembly(string entryFileName)
    {
        string simpleName = Path.GetFileNameWithoutExtension(entryFileName);
        if (!_assemblies.TryGetValue(simpleName, out var payload))
        {
            throw new FileNotFoundException($"Entry assembly '{entryFileName}' not found in dev-mod snapshot.");
        }

        return LoadFromBytes(payload.DllBytes, payload.PdbBytes);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.IsNullOrEmpty(assemblyName.Name))
        {
            return null;
        }

        if (_assemblies.TryGetValue(assemblyName.Name, out var payload))
        {
            return LoadFromBytes(payload.DllBytes, payload.PdbBytes);
        }

        if (_hostContext != null && _hostContext != Default)
        {
            foreach (Assembly loaded in _hostContext.Assemblies)
            {
                if (string.Equals(loaded.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return loaded;
                }
            }

            try
            {
                return _hostContext.LoadFromAssemblyName(assemblyName);
            }
            catch
            {
                // Fall through to AssemblyLoadContext.Default for BCL assemblies
            }
        }

        return null;
    }

    private Assembly LoadFromBytes(byte[] dllBytes, byte[]? pdbBytes)
    {
        using var dllStream = new MemoryStream(dllBytes, writable: false);
        if (pdbBytes != null)
        {
            using var pdbStream = new MemoryStream(pdbBytes, writable: false);
            return LoadFromStream(dllStream, pdbStream);
        }

        return LoadFromStream(dllStream);
    }
}
