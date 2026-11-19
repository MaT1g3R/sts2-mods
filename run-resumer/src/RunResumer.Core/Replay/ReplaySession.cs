using System;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Audio.Debug;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using RunResumer.Storage;

namespace RunResumer.Replay;

internal static class ReplaySession
{
    private static bool _isLaunching;

    public static bool IsActive { get; private set; }

    public static long? ReplayedRunStartTime { get; private set; }

    public static int? ReplayedFromFloor { get; private set; }

    public static long? PendingReturnStartTime { get; set; }

    public static void TryLaunchFromHistory(RunHistory history, int floorNum)
    {
        if (_isLaunching)
        {
            return;
        }

        if (history.Players.Count != 1)
        {
            NGame.Instance?.AddChildSafely(NFullscreenTextVfx.Create("Run Resumer only supports singleplayer runs."));
            return;
        }

        SerializableRun? serializableRun;
        try
        {
            serializableRun = RunArchiveStorage.LoadFloor(history, floorNum);
        }
        catch (Exception ex)
        {
            Log.Error($"[RunResumer] Failed to load floor {floorNum} for run {history.StartTime}: {ex}");
            NGame.Instance?.AddChildSafely(NFullscreenTextVfx.Create($"Failed to load replay for Floor {floorNum}."));
            return;
        }

        if (serializableRun == null)
        {
            NGame.Instance?.AddChildSafely(NFullscreenTextVfx.Create($"No replay snapshot saved for Floor {floorNum}."));
            return;
        }

        TaskHelper.RunSafely(LaunchReplayAsync(serializableRun, history.StartTime, floorNum));
    }

    public static void MarkReturningToMenu()
    {
        if (IsActive)
        {
            PendingReturnStartTime = ReplayedRunStartTime;
            IsActive = false;
            ReplayedRunStartTime = null;
            ReplayedFromFloor = null;
        }
    }

    public static void ClearAll()
    {
        IsActive = false;
        ReplayedRunStartTime = null;
        ReplayedFromFloor = null;
        PendingReturnStartTime = null;
        _isLaunching = false;
    }

    private static async Task LaunchReplayAsync(SerializableRun serializableRun, long runStartTime, int floorNum)
    {
        _isLaunching = true;
        bool wasInRun = RunManager.Instance.IsInProgress;
        NGame? game = NGame.Instance;
        if (game == null)
        {
            _isLaunching = false;
            return;
        }

        try
        {
            Log.Info($"[RunResumer] Launching practice replay for run {runStartTime} at Floor {floorNum}");
            NHotkeyManager.Instance?.ClearHotkeys();
            NAudioManager.Instance?.StopMusic();
            NRunMusicController.Instance?.StopMusic();

            RunState runState = RunState.FromSerializable(serializableRun);
            string transitionPath = runState.Players[0].Character.CharacterSelectTransitionPath;
            string transitionSfx = runState.Players[0].Character.CharacterTransitionSfx;

            SfxCmd.Play(transitionSfx);
            await game.Transition.FadeOut(0.8f, transitionPath);

            if (wasInRun)
            {
                RunManager.Instance.CleanUp(graceful: true);
            }

            IsActive = true;
            ReplayedRunStartTime = runStartTime;
            ReplayedFromFloor = floorNum;
            PendingReturnStartTime = null;

            await RunManager.Instance.SetUpSavedSingleplayer(runState, serializableRun);

            game.ReactionContainer.InitializeNetworking(new NetSingleplayerGameService());
            await game.LoadRun(runState, serializableRun.PreFinishedRoom);
            await game.Transition.FadeIn();
        }
        catch (Exception ex)
        {
            Log.Error($"[RunResumer] Exception while launching replay for run {runStartTime} Floor {floorNum}: {ex}");
            IsActive = false;
            ReplayedRunStartTime = null;
            ReplayedFromFloor = null;
            RunManager.Instance.CleanUp();
            await game.ReturnToMainMenuWithInternalError(ex);
        }
        finally
        {
            _isLaunching = false;
        }
    }
}
