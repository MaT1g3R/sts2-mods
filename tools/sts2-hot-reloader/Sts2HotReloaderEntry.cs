using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Debug;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace Sts2HotReloader;

[ModInitializer("Initialize")]
public static class Sts2HotReloaderEntry
{
    public const string ModId = "Sts2HotReloader";
    private const string HarmonyId = "com.vmservice.sts2hotreloader";

    public static void Initialize()
    {
        try
        {
            new Harmony(HarmonyId).PatchAll(typeof(Sts2HotReloaderEntry).Assembly);
            HotReloadHost.Initialize();
            Log.Info($"[{ModId}] Initialized successfully.");
        }
        catch (Exception ex)
        {
            Log.Error($"[{ModId}] Failed to initialize: {ex}");
        }
    }
}

[HarmonyPatch(typeof(NGame), nameof(NGame._Ready))]
internal static class HotReloadGameReadyPatch
{
    private const string TimerNodeName = "Sts2HotReloaderPollTimer";

    public static void Postfix(NGame __instance)
    {
        try
        {
            HotReloadHost.OnGameReady();

            if (__instance.GetNodeOrNull<Godot.Timer>(TimerNodeName) != null)
            {
                return;
            }

            Godot.Timer pollTimer = new Godot.Timer
            {
                Name = TimerNodeName,
                WaitTime = 0.25,
                OneShot = false,
                Autostart = true
            };
            pollTimer.Timeout += HotReloadHost.PollAndDrainOnMainThread;
            __instance.AddChild(pollTimer);
        }
        catch (Exception ex)
        {
            Log.Error($"[HotReload] Failed to attach main-thread poll timer: {ex}");
        }
    }
}

[HarmonyPatch(typeof(NModdingScreen), nameof(NModdingScreen.OnModEnabledOrDisabled))]
internal static class HotReloadModdingScreenTogglePatch
{
    public static void Postfix(NModdingScreen __instance)
    {
        try
        {
            HotReloadHost.HandleModSettingsToggled(__instance);
        }
        catch (Exception ex)
        {
            Log.Error($"[HotReload] Failed to handle mod toggle in ModdingScreen: {ex}");
        }
    }
}

[HarmonyPatch(typeof(NGame), nameof(NGame._Input))]
internal static class HotReloadInputPatch
{
    public static void Postfix(NGame __instance, InputEvent inputEvent)
    {
        if (inputEvent is not InputEventKey { Pressed: true, Echo: false } key)
        {
            return;
        }

        if (NDevConsole.IsConsoleVisible)
        {
            return;
        }

        bool isF9 = key.Keycode == Key.F9 && !key.CtrlPressed && !key.AltPressed && !key.ShiftPressed && !key.MetaPressed;
        bool isCtrlShiftR = key.Keycode == Key.R && key.CtrlPressed && key.ShiftPressed && !key.AltPressed && !key.MetaPressed;
        if (!isF9 && !isCtrlShiftR)
        {
            return;
        }

        if (isCtrlShiftR)
        {
            Control? focus = __instance.GetViewport()?.GuiGetFocusOwner();
            if (focus is LineEdit or TextEdit)
            {
                return;
            }
        }

        __instance.GetViewport()?.SetInputAsHandled();
        HotReloadHost.ReloadAllModsNow();
    }
}
