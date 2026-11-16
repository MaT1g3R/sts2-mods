using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using RunResumer.Replay;
using RunResumer.Storage;

namespace RunResumer.Patches;

[HarmonyPatch(typeof(RunSaveManager), nameof(RunSaveManager.SaveRun), typeof(SerializableRun), typeof(bool))]
internal static class SaveRunCapturePatch
{
    public static void Prefix(SerializableRun save, bool isMultiplayer)
    {
        try
        {
            if (isMultiplayer || save.Players.Count != 1 || save.PreFinishedRoom != null)
            {
                return;
            }

            if (ReplaySession.IsActive || !RunManager.Instance.IsInProgress || !RunManager.Instance.ShouldSave)
            {
                return;
            }

            RunArchiveStorage.CaptureFloor(save);
        }
        catch (Exception ex)
        {
            Log.Error($"[RunResumer] Error in SaveRunCapturePatch: {ex}");
        }
    }
}
