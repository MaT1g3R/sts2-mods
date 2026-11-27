using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Saves;

namespace Sts2HotReloader;

/// <summary>
/// Manages dev mods under <c>dev-mods/&lt;ModId&gt;/</c> inside collectible AssemblyLoadContexts,
/// registers them in STS2's Mod Settings UI, and watches for rebuilt DLLs/stamps to perform
/// clean Shutdown -> Unload -> Reload cycles on Godot's main thread.
/// </summary>
internal static class HotReloadHost
{
    private const string DevModsFolderName = "dev-mods";
    private const string ConfigFileName = "hotreload.cfg";
    private const string LegacyConfigFileName = "hotreload.json";
    private const string ManifestFileName = "manifest.cfg";
    private const string StampFileName = "reload.stamp";
    private const long DebounceMs = 300;

    private static readonly AccessTools.FieldRef<List<Mod>> ModManagerModsRef =
        AccessTools.StaticFieldRefAccess<List<Mod>>(AccessTools.Field(typeof(ModManager), "_mods"));

    private static readonly AccessTools.FieldRef<ModSettings?> ModManagerSettingsRef =
        AccessTools.StaticFieldRefAccess<ModSettings?>(AccessTools.Field(typeof(ModManager), "_settings"));

    private static readonly AccessTools.FieldRef<NModdingScreen, Control> ModRowContainerRef =
        AccessTools.FieldRefAccess<NModdingScreen, Control>("_modRowContainer");

    private static readonly AccessTools.FieldRef<NModdingScreen, Control> PendingChangesWarningRef =
        AccessTools.FieldRefAccess<NModdingScreen, Control>("_pendingChangesWarning");

    private sealed class HotReloadConfig
    {
        [JsonPropertyName("entry_assembly")]
        public string? EntryAssembly { get; set; }

        [JsonPropertyName("entry_type")]
        public string? EntryType { get; set; }

        [JsonPropertyName("init_method")]
        public string? InitMethod { get; set; }

        [JsonPropertyName("shutdown_method")]
        public string? ShutdownMethod { get; set; }
    }

    private sealed class LoadedDevMod
    {
        public required string Id;
        public required string ModDir;
        public CollectibleModLoadContext? LoadContext;
        public MethodInfo? ShutdownMethod;
        public int Generation;
        public string LastSignature = string.Empty;
    }

    private static readonly Dictionary<string, LoadedDevMod> LoadedMods = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Mod> RegisteredUiMods = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> KnownDevModDirs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, long> PendingReloadDueTicks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<(string ModId, int Generation, WeakReference AlcRef, long CheckAfterTicks)> PendingGcChecks = new();

    private static string? _devModsRoot;
    private static FileSystemWatcher? _watcher;
    private static bool _gameReady;
    private static bool _handlingToggle;

    public static void Initialize()
    {
        string? hostDir = Path.GetDirectoryName(typeof(HotReloadHost).Assembly.Location);
        if (string.IsNullOrEmpty(hostDir))
        {
            Log.Error("[HotReload] Could not determine Sts2HotReloader directory.");
            return;
        }

        _devModsRoot = Path.Combine(hostDir, DevModsFolderName);
        Directory.CreateDirectory(_devModsRoot);

        LoadInitialMods();
        StartWatcher(_devModsRoot);
    }

    /// <summary>
    /// Runs once NGame._Ready fires (after ModManager.Initialize has finished iterating _mods)
    /// so dev mods can be safely added to ModManager._mods and ModSettings for the Installed Mods UI.
    /// </summary>
    public static void OnGameReady()
    {
        _gameReady = true;
        if (string.IsNullOrEmpty(_devModsRoot) || !Directory.Exists(_devModsRoot))
        {
            return;
        }

        foreach (string dir in Directory.GetDirectories(_devModsRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            string modId = Path.GetFileName(dir);
            if (IsLoadedByVanillaModManager(modId))
            {
                continue;
            }

            SyncModManagerEntry(modId, dir);
        }
    }

    public static void PollAndDrainOnMainThread()
    {
        if (string.IsNullOrEmpty(_devModsRoot) || !Directory.Exists(_devModsRoot))
        {
            return;
        }

        ScanForAddedRemovedOrChangedMods();
        DrainPendingReloads();
        RunPendingGcChecks();
    }

    public static void HandleModSettingsToggled(NModdingScreen screen)
    {
        if (_handlingToggle || string.IsNullOrEmpty(_devModsRoot))
        {
            return;
        }

        ModSettings? settings = SaveManager.Instance?.SettingsSave?.ModSettings;
        if (settings == null)
        {
            return;
        }

        _handlingToggle = true;
        try
        {
            bool changedAny = false;
            foreach (var (modId, uiMod) in RegisteredUiMods.ToArray())
            {
                bool shouldBeDisabled = settings.IsModDisabled(uiMod);
                bool isCurrentlyLoaded = LoadedMods.ContainsKey(modId);

                if (shouldBeDisabled && isCurrentlyLoaded)
                {
                    UnloadModInternal(modId, removeFromModManager: false, showToast: true, reason: "disabled in Mod Settings");
                    uiMod.state = ModLoadState.Disabled;
                    changedAny = true;
                }
                else if (!shouldBeDisabled && !isCurrentlyLoaded)
                {
                    string modDir = Path.Combine(_devModsRoot, modId);
                    if (Directory.Exists(modDir))
                    {
                        bool ok = ReloadModInternal(modId, modDir, showToast: true);
                        uiMod.state = ok ? ModLoadState.Loaded : ModLoadState.Failed;
                        changedAny = true;
                    }
                }
            }

            if (changedAny)
            {
                RefreshModdingScreenRows(screen);
                RecomputePendingChangesWarning(screen);
            }
        }
        finally
        {
            _handlingToggle = false;
        }
    }

    public static void ReloadAllModsNow()
    {
        if (string.IsNullOrEmpty(_devModsRoot) || !Directory.Exists(_devModsRoot))
        {
            HotReloadToast.Show("[HotReload] dev-mods directory not found", isError: true);
            return;
        }

        string[] dirs = Directory.GetDirectories(_devModsRoot);
        if (dirs.Length == 0)
        {
            HotReloadToast.Show("[HotReload] No mods in dev-mods/ — run `make hot-deploy-<mod>`", isInfo: true);
            return;
        }

        int reloaded = 0;
        int failed = 0;
        Stopwatch totalSw = Stopwatch.StartNew();

        foreach (string dir in dirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            string modId = Path.GetFileName(dir);
            if (IsDisabledInModSettings(modId))
            {
                continue;
            }

            if (ReloadModInternal(modId, dir, showToast: false))
            {
                reloaded++;
            }
            else
            {
                failed++;
            }
        }

        totalSw.Stop();
        if (failed == 0)
        {
            HotReloadToast.Show($"[HotReload] Reloaded {reloaded} mod(s) in {totalSw.ElapsedMilliseconds}ms");
        }
        else
        {
            HotReloadToast.Show($"[HotReload] Reloaded {reloaded}, failed {failed} — check godot.log", isError: true);
        }
    }

    private static void LoadInitialMods()
    {
        if (string.IsNullOrEmpty(_devModsRoot) || !Directory.Exists(_devModsRoot))
        {
            return;
        }

        string[] dirs = Directory.GetDirectories(_devModsRoot);
        Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);

        foreach (string dir in dirs)
        {
            string modId = Path.GetFileName(dir);
            KnownDevModDirs.Add(modId);

            if (IsLoadedByVanillaModManager(modId))
            {
                Log.Warn($"[HotReload] Skipping dev-mod '{modId}' because it is already loaded in <game>/mods/{modId}/. Run `make hot-deploy-...` to switch it to hot reload.");
                continue;
            }

            if (IsDisabledInModSettings(modId))
            {
                Log.Info($"[HotReload] Dev-mod '{modId}' is disabled in Mod Settings; skipping initial load.");
                continue;
            }

            ReloadModInternal(modId, dir, showToast: false);
        }

        Log.Info($"[HotReload] Host ready. Watching '{_devModsRoot}' ({LoadedMods.Count} dev mod(s) loaded). Press F9 or Ctrl+Shift+R to force reload.");
    }

    private static void StartWatcher(string root)
    {
        try
        {
            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            void OnFsEvent(object _, FileSystemEventArgs e)
            {
                string? modId = ExtractModIdFromPath(root, e.FullPath);
                if (string.IsNullOrEmpty(modId))
                {
                    return;
                }

                string fileName = Path.GetFileName(e.FullPath);
                if (fileName.Equals(StampFileName, StringComparison.OrdinalIgnoreCase) ||
                    fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals(ConfigFileName, StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
                {
                    PendingReloadDueTicks[modId] = System.Environment.TickCount64 + DebounceMs;
                }
            }

            _watcher.Changed += OnFsEvent;
            _watcher.Created += OnFsEvent;
            _watcher.Renamed += (s, e) => OnFsEvent(s, e);
            _watcher.Deleted += OnFsEvent;
        }
        catch (Exception ex)
        {
            Log.Warn($"[HotReload] FileSystemWatcher failed (falling back to polling): {ex.Message}");
        }
    }

    private static void ScanForAddedRemovedOrChangedMods()
    {
        if (string.IsNullOrEmpty(_devModsRoot))
        {
            return;
        }

        var currentDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string dir in Directory.GetDirectories(_devModsRoot))
        {
            string modId = Path.GetFileName(dir);
            currentDirs.Add(modId);

            if (IsLoadedByVanillaModManager(modId))
            {
                continue;
            }

            bool isNewDir = KnownDevModDirs.Add(modId);
            if (isNewDir && _gameReady)
            {
                SyncModManagerEntry(modId, dir);
            }

            if (IsDisabledInModSettings(modId))
            {
                continue;
            }

            string signature = ComputeModSignature(dir);
            if (string.IsNullOrEmpty(signature))
            {
                continue;
            }

            if (!LoadedMods.TryGetValue(modId, out LoadedDevMod? loaded) ||
                !string.Equals(loaded.LastSignature, signature, StringComparison.Ordinal))
            {
                PendingReloadDueTicks.TryAdd(modId, System.Environment.TickCount64 + DebounceMs);
            }
        }

        foreach (string knownId in KnownDevModDirs.ToList())
        {
            if (!currentDirs.Contains(knownId))
            {
                KnownDevModDirs.Remove(knownId);
                UnloadModInternal(knownId, removeFromModManager: true, showToast: true, reason: "removed from dev-mods/");
            }
        }
    }

    private static void DrainPendingReloads()
    {
        if (PendingReloadDueTicks.IsEmpty || string.IsNullOrEmpty(_devModsRoot))
        {
            return;
        }

        long now = System.Environment.TickCount64;
        foreach (var kvp in PendingReloadDueTicks.ToArray())
        {
            if (now < kvp.Value)
            {
                continue;
            }

            if (!PendingReloadDueTicks.TryRemove(kvp.Key, out _))
            {
                continue;
            }

            string modId = kvp.Key;
            string modDir = Path.Combine(_devModsRoot, modId);
            if (!Directory.Exists(modDir))
            {
                KnownDevModDirs.Remove(modId);
                UnloadModInternal(modId, removeFromModManager: true, showToast: true, reason: "removed from dev-mods/");
                continue;
            }

            if (IsLoadedByVanillaModManager(modId))
            {
                Log.Warn($"[HotReload] Cannot hot-reload '{modId}' while it is also installed in <game>/mods/{modId}/.");
                HotReloadToast.Show($"[HotReload] {modId} is loaded by vanilla ModManager — restart once after `make hot-deploy`", isError: true);
                continue;
            }

            if (IsDisabledInModSettings(modId))
            {
                if (_gameReady)
                {
                    SyncModManagerEntry(modId, modDir);
                }
                continue;
            }

            ReloadModInternal(modId, modDir, showToast: true);
        }
    }

    private static bool ReloadModInternal(string modId, string modDir, bool showToast)
    {
        Stopwatch sw = Stopwatch.StartNew();
        int nextGen = LoadedMods.TryGetValue(modId, out LoadedDevMod? prev) ? prev.Generation + 1 : 1;

        try
        {
            if (prev != null)
            {
                WeakReference? oldAlcRef = ShutdownAndUnload(prev);
                LoadedMods.Remove(modId);
                if (oldAlcRef != null)
                {
                    PendingGcChecks.Add((modId, prev.Generation, oldAlcRef, System.Environment.TickCount64 + 2500));
                }
            }

            string signature = ComputeModSignature(modDir);
            LoadedDevMod loaded = LoadAndInitialize(modId, modDir, nextGen, signature);
            LoadedMods[modId] = loaded;

            if (_gameReady)
            {
                SyncModManagerEntry(modId, modDir, failed: false);
            }

            sw.Stop();
            Log.Info($"[HotReload] Loaded {modId} (#{loaded.Generation}) in {sw.ElapsedMilliseconds}ms");
            if (showToast)
            {
                HotReloadToast.Show($"[HotReload] Reloaded {modId} (#{loaded.Generation}) in {sw.ElapsedMilliseconds}ms");
            }

            return true;
        }
        catch (Exception ex)
        {
            sw.Stop();
            if (_gameReady)
            {
                SyncModManagerEntry(modId, modDir, failed: true);
            }

            Log.Error($"[HotReload] Failed to reload {modId}: {ex}");
            if (showToast)
            {
                HotReloadToast.Show($"[HotReload] Failed to reload {modId}: {ex.Message}", isError: true);
            }

            return false;
        }
    }

    private static void UnloadModInternal(string modId, bool removeFromModManager, bool showToast, string reason)
    {
        if (LoadedMods.TryGetValue(modId, out LoadedDevMod? prev))
        {
            WeakReference? oldAlcRef = ShutdownAndUnload(prev);
            LoadedMods.Remove(modId);
            if (oldAlcRef != null)
            {
                PendingGcChecks.Add((modId, prev.Generation, oldAlcRef, System.Environment.TickCount64 + 2500));
            }
        }

        if (removeFromModManager && RegisteredUiMods.TryGetValue(modId, out Mod? uiMod))
        {
            RegisteredUiMods.Remove(modId);
            List<Mod>? mods = ModManagerModsRef();
            mods?.Remove(uiMod);
        }

        Log.Info($"[HotReload] Unloaded {modId} ({reason})");
        if (showToast)
        {
            HotReloadToast.Show($"[HotReload] Unloaded {modId} ({reason})", isInfo: true);
        }
    }

    private static void SyncModManagerEntry(string modId, string modDir, bool failed = false)
    {
        ModManifest manifest = ReadModManifest(modId, modDir);
        SemanticVersion? parsedVersion = null;
        if (!string.IsNullOrWhiteSpace(manifest.version))
        {
            SemanticVersion.TryFromString(manifest.version, out parsedVersion);
        }

        ModLoadState state = IsDisabledInModSettings(modId)
            ? ModLoadState.Disabled
            : failed
                ? ModLoadState.Failed
                : LoadedMods.ContainsKey(modId)
                    ? ModLoadState.Loaded
                    : ModLoadState.Disabled;

        if (!RegisteredUiMods.TryGetValue(modId, out Mod? mod))
        {
            mod = new Mod
            {
                modSource = ModSource.ModsDirectory,
                path = modDir,
                manifest = manifest,
                version = parsedVersion,
                state = state
                // Keep assemblies empty so ModManager._mods never pins the collectible ALC.
            };
            RegisteredUiMods[modId] = mod;

            List<Mod>? mods = ModManagerModsRef();
            if (mods != null && !mods.Contains(mod))
            {
                mods.Add(mod);
            }

            EnsureSettingsSaveModEntry(mod);
            NotifyModDetected(mod);
        }
        else
        {
            mod.path = modDir;
            mod.manifest = manifest;
            mod.version = parsedVersion;
            mod.state = state;
            EnsureSettingsSaveModEntry(mod);
        }
    }

    private static void EnsureSettingsSaveModEntry(Mod mod)
    {
        ModSettings? settings = SaveManager.Instance?.SettingsSave?.ModSettings ?? ModManagerSettingsRef();
        if (settings == null || mod.manifest == null)
        {
            return;
        }

        bool exists = settings.ModList.Any(m =>
            string.Equals(m.Id, mod.manifest.id, StringComparison.OrdinalIgnoreCase) &&
            m.Source == mod.modSource);

        if (!exists)
        {
            var entry = new SettingsSaveMod(mod)
            {
                IsEnabled = mod.state != ModLoadState.Disabled
            };
            settings.ModList.Add(entry);
        }
    }

    private static void NotifyModDetected(Mod mod)
    {
        try
        {
            MethodInfo? invokeMethod = AccessTools.Method(typeof(ModManager), "InvokeOnModDetected", new[] { typeof(Mod) });
            invokeMethod?.Invoke(null, new object[] { mod });
        }
        catch (Exception ex)
        {
            Log.Warn($"[HotReload] Could not notify ModManager.OnModDetected for '{mod.manifest?.id}': {ex.Message}");
        }
    }

    private static void RefreshModdingScreenRows(NModdingScreen screen)
    {
        Control? container = ModRowContainerRef(screen);
        if (container == null || !GodotObject.IsInstanceValid(container))
        {
            return;
        }

        foreach (NModMenuRow row in container.GetChildren().OfType<NModMenuRow>())
        {
            if (row.Mod == null || !RegisteredUiMods.ContainsValue(row.Mod))
            {
                continue;
            }

            Color color = row.Mod.state switch
            {
                ModLoadState.Loaded => Colors.White,
                ModLoadState.Failed => StsColors.red,
                _ => StsColors.gray
            };

            MegaRichTextLabel? title = row.GetNodeOrNull<MegaRichTextLabel>("Title");
            if (title != null) title.Modulate = color;

            TextureRect? icon = row.GetNodeOrNull<TextureRect>("PlatformIcon");
            if (icon != null) icon.Modulate = color;
        }
    }

    private static void RecomputePendingChangesWarning(NModdingScreen screen)
    {
        Control? warning = PendingChangesWarningRef(screen);
        if (warning == null || !GodotObject.IsInstanceValid(warning))
        {
            return;
        }

        ModSettings? settings = SaveManager.Instance?.SettingsSave?.ModSettings;
        foreach (Mod mod in ModManager.Mods)
        {
            bool disabled = settings?.IsModDisabled(mod) ?? false;
            if ((mod.state == ModLoadState.Disabled && !disabled) ||
                (mod.state == ModLoadState.Loaded && disabled))
            {
                warning.Visible = true;
                return;
            }
        }

        warning.Visible = false;
    }

    private static ModManifest ReadModManifest(string modId, string modDir)
    {
        string manifestPath = Path.Combine(modDir, ManifestFileName);
        if (File.Exists(manifestPath))
        {
            try
            {
                using FileStream stream = File.OpenRead(manifestPath);
                ModManifest? parsed = ModManifest.ReadFromStream(stream, out _);
                if (parsed != null)
                {
                    parsed.id ??= modId;
                    parsed.name ??= modId;
                    string baseDesc = string.IsNullOrWhiteSpace(parsed.description)
                        ? string.Empty
                        : parsed.description.TrimEnd() + "\n\n";
                    parsed.description = $"{baseDesc}[Hot Reload enabled — managed by Sts2HotReloader]";
                    return parsed;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[HotReload] Failed to read {manifestPath}: {ex.Message}");
            }
        }

        return new ModManifest
        {
            id = modId,
            name = modId,
            author = "vmService",
            version = "dev",
            hasDll = true,
            affectsGameplay = false,
            description = "[Hot Reload enabled — managed by Sts2HotReloader]"
        };
    }

    private static bool IsDisabledInModSettings(string modId)
    {
        ModSettings? settings = SaveManager.Instance?.SettingsSave?.ModSettings ?? ModManagerSettingsRef();
        if (settings == null)
        {
            return false;
        }

        SettingsSaveMod? entry = settings.ModList.FirstOrDefault(m =>
            string.Equals(m.Id, modId, StringComparison.OrdinalIgnoreCase) &&
            m.Source == ModSource.ModsDirectory);

        return entry != null && !entry.IsEnabled;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference? ShutdownAndUnload(LoadedDevMod mod)
    {
        if (mod.ShutdownMethod != null)
        {
            try
            {
                mod.ShutdownMethod.Invoke(null, null);
            }
            catch (Exception ex)
            {
                Log.Error($"[HotReload] Error during {mod.Id}.Shutdown(): {ex}");
            }
        }
        else
        {
            Log.Warn($"[HotReload] {mod.Id} has no static Shutdown() method; Harmony patches and UI nodes may persist.");
        }

        CollectibleModLoadContext? alc = mod.LoadContext;
        mod.ShutdownMethod = null;
        mod.LoadContext = null;

        if (alc == null)
        {
            return null;
        }

        WeakReference alcRef = new WeakReference(alc, trackResurrection: true);
        alc.Unload();
        return alcRef;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static LoadedDevMod LoadAndInitialize(string modId, string modDir, int generation, string signature)
    {
        HotReloadConfig? config = ReadConfig(modDir);
        string entryFileName = ResolveEntryDllName(modId, modDir, config);

        var alc = new CollectibleModLoadContext($"HotReload:{modId}#{generation}", modDir);
        try
        {
            Assembly entryAssembly = alc.LoadEntryAssembly(entryFileName);
            (Type entryType, MethodInfo initMethod, MethodInfo? shutdownMethod) = ResolveEntryMethods(entryAssembly, config);

            initMethod.Invoke(null, null);

            return new LoadedDevMod
            {
                Id = modId,
                ModDir = modDir,
                LoadContext = alc,
                ShutdownMethod = shutdownMethod,
                Generation = generation,
                LastSignature = signature
            };
        }
        catch
        {
            alc.Unload();
            throw;
        }
    }

    private static HotReloadConfig? ReadConfig(string modDir)
    {
        string configPath = Path.Combine(modDir, ConfigFileName);
        if (!File.Exists(configPath))
        {
            configPath = Path.Combine(modDir, LegacyConfigFileName);
            if (!File.Exists(configPath))
            {
                return null;
            }
        }

        string json = File.ReadAllText(configPath);
        return JsonSerializer.Deserialize<HotReloadConfig>(json);
    }

    private static string ResolveEntryDllName(string modId, string modDir, HotReloadConfig? config)
    {
        if (!string.IsNullOrWhiteSpace(config?.EntryAssembly))
        {
            return config.EntryAssembly!;
        }

        string direct = modId + ".dll";
        if (File.Exists(Path.Combine(modDir, direct)))
        {
            return direct;
        }

        string? firstDll = Directory.EnumerateFiles(modDir, "*.dll", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return firstDll ?? throw new FileNotFoundException($"No .dll found in '{modDir}'.");
    }

    private static (Type EntryType, MethodInfo InitMethod, MethodInfo? ShutdownMethod) ResolveEntryMethods(
        Assembly assembly,
        HotReloadConfig? config)
    {
        const BindingFlags staticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        if (!string.IsNullOrWhiteSpace(config?.EntryType))
        {
            Type entryType = assembly.GetType(config.EntryType!, throwOnError: true)!;
            string initName = string.IsNullOrWhiteSpace(config.InitMethod) ? "Initialize" : config.InitMethod!;
            string shutdownName = string.IsNullOrWhiteSpace(config.ShutdownMethod) ? "Shutdown" : config.ShutdownMethod!;

            MethodInfo initMethod = entryType.GetMethod(initName, staticFlags, Type.EmptyTypes)
                ?? throw new MissingMethodException(entryType.FullName, initName);
            MethodInfo? shutdownMethod = entryType.GetMethod(shutdownName, staticFlags, Type.EmptyTypes);
            return (entryType, initMethod, shutdownMethod);
        }

        foreach (Type type in assembly.GetTypes())
        {
            ModInitializerAttribute? attr = type.GetCustomAttribute<ModInitializerAttribute>();
            if (attr == null)
            {
                continue;
            }

            string initName = string.IsNullOrWhiteSpace(attr.initializerMethod) ? "Initialize" : attr.initializerMethod;
            MethodInfo initMethod = type.GetMethod(initName, staticFlags, Type.EmptyTypes)
                ?? throw new MissingMethodException(type.FullName, initName);
            MethodInfo? shutdownMethod = type.GetMethod("Shutdown", staticFlags, Type.EmptyTypes);
            return (type, initMethod, shutdownMethod);
        }

        throw new InvalidOperationException($"No [ModInitializer] type or hotreload.cfg entry_type found in '{assembly.GetName().Name}'.");
    }

    private static void RunPendingGcChecks()
    {
        if (PendingGcChecks.Count == 0)
        {
            return;
        }

        long now = System.Environment.TickCount64;
        bool collectedAny = false;

        for (int i = PendingGcChecks.Count - 1; i >= 0; i--)
        {
            var check = PendingGcChecks[i];
            if (now < check.CheckAfterTicks)
            {
                continue;
            }

            if (!collectedAny)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                collectedAny = true;
            }

            if (check.AlcRef.IsAlive)
            {
                Log.Warn($"[HotReload] Old AssemblyLoadContext for {check.ModId} (#{check.Generation}) is still alive after GC (check for retained static events/delegates).");
            }
            else
            {
                Log.Info($"[HotReload] Old AssemblyLoadContext for {check.ModId} (#{check.Generation}) cleanly unloaded.");
            }

            PendingGcChecks.RemoveAt(i);
        }
    }

    private static bool IsLoadedByVanillaModManager(string modId)
    {
        foreach (Mod mod in ModManager.Mods)
        {
            if (RegisteredUiMods.ContainsValue(mod))
            {
                continue;
            }

            if (mod.state == ModLoadState.Loaded &&
                mod.manifest != null &&
                string.Equals(mod.manifest.id, modId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string ComputeModSignature(string modDir)
    {
        string stampPath = Path.Combine(modDir, StampFileName);
        if (File.Exists(stampPath))
        {
            try
            {
                return "stamp:" + File.ReadAllText(stampPath).Trim();
            }
            catch
            {
                // Fall through to DLL timestamps
            }
        }

        var parts = new List<string>();
        foreach (string dll in Directory.EnumerateFiles(modDir, "*.dll", SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            FileInfo fi = new FileInfo(dll);
            parts.Add($"{fi.Name}:{fi.Length}:{fi.LastWriteTimeUtc.Ticks}");
        }

        return string.Join("|", parts);
    }

    private static string? ExtractModIdFromPath(string root, string fullPath)
    {
        string relative = Path.GetRelativePath(root, fullPath);
        if (string.IsNullOrEmpty(relative) || relative.StartsWith("..", StringComparison.Ordinal))
        {
            return null;
        }

        int sep = relative.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        return sep > 0 ? relative[..sep] : null;
    }
}
