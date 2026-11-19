using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using RunResumer.Storage;

namespace RunResumer.Patches;

internal static class RunHistoryShareUiHelper
{
    private const string ShareButtonScenePath = "res://scenes/ui/share_button.tscn";
    private const string HoverTipTable = "static_hover_tips";
    private const string ExportTipTitleKey = "RUN_RESUMER_EXPORT.title";
    private const string ImportTipTitleKey = "RUN_RESUMER_IMPORT.title";

    private static readonly Color DisabledLabelModulate = new(1f, 1f, 1f, 0.45f);
    private static readonly Color RestButtonPillColor = new(0f, 0f, 0f, 0.753f);
    private static readonly Color HoverButtonPillColor = new(0.18f, 0.32f, 0.38f, 0.94f);
    private static readonly Color PressButtonPillColor = new(0.10f, 0.18f, 0.22f, 0.90f);
    private static readonly Color ButtonShadowColor = new(0f, 0f, 0f, 0.38f);
    private static readonly Vector2 HoverScale = new(1.06f, 1.06f);
    private static readonly Vector2 PressScale = new(0.96f, 0.96f);

    private sealed class ShareUiState
    {
        public required NButton ExportButton;
        public required Control ExportLabelContainer;
        public required NButton ImportButton;
        public string ExportTipDescription = string.Empty;
    }

    private static readonly ConditionalWeakTable<NRunHistory, ShareUiState> UiStates = new();

    private static readonly AccessTools.FieldRef<NRunHistory, RunHistory> HistoryRef =
        AccessTools.FieldRefAccess<NRunHistory, RunHistory>("_history");

    private static readonly AccessTools.FieldRef<NRunHistory, List<string>> RunNamesRef =
        AccessTools.FieldRefAccess<NRunHistory, List<string>>("_runNames");

    private static readonly Func<NRunHistory, int, System.Threading.Tasks.Task> RefreshAndSelectRunDelegate =
        AccessTools.MethodDelegate<Func<NRunHistory, int, System.Threading.Tasks.Task>>(
            AccessTools.Method(typeof(NRunHistory), "RefreshAndSelectRun", new[] { typeof(int) }));

    private static string? _lastDirectory;

    public static void AttachButtons(NRunHistory historyScreen)
    {
        if (UiStates.TryGetValue(historyScreen, out _))
        {
            return;
        }

        try
        {
            Control? existingShareBtn = historyScreen.GetNodeOrNull<Control>("%ShareButton");
            if (existingShareBtn != null)
            {
                existingShareBtn.Visible = false;
            }

            PackedScene? buttonScene = PreloadManager.Cache.GetScene(ShareButtonScenePath);
            if (buttonScene == null)
            {
                Log.Warn("[RunResumer] Could not load share_button.tscn for Export/Import buttons.");
                return;
            }

            VBoxContainer container = new VBoxContainer
            {
                Name = "RunResumerShareControls",
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            container.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
            container.GrowHorizontal = Control.GrowDirection.Begin;
            container.GrowVertical = Control.GrowDirection.Begin;
            container.OffsetLeft = -220f;
            container.OffsetTop = -186f;
            container.OffsetRight = -48f;
            container.OffsetBottom = -48f;
            container.AddThemeConstantOverride("separation", 10);
            historyScreen.AddChild(container);

            NButton exportBtn = InstantiateShareButton(buttonScene, "ExportButton", "Export", out Control exportLabelContainer);
            NButton importBtn = InstantiateShareButton(buttonScene, "ImportButton", "Import", out Control importLabelContainer);

            container.AddChild(exportBtn);
            container.AddChild(importBtn);

            importBtn.Enable();
            importLabelContainer.Modulate = Colors.White;

            ShareUiState state = new ShareUiState
            {
                ExportButton = exportBtn,
                ExportLabelContainer = exportLabelContainer,
                ImportButton = importBtn,
                ExportTipDescription = "No Run Resumer snapshots saved for this run."
            };
            UiStates.Add(historyScreen, state);

            SetButtonEnabled(exportBtn, exportLabelContainer, enabled: false);

            exportBtn.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ =>
            {
                OnExportPressed(historyScreen);
            }));
            importBtn.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ =>
            {
                OnImportPressed(historyScreen);
            }));

            WireHoverTip(exportBtn, ExportTipTitleKey, "Export", () => state.ExportTipDescription);
            WireHoverTip(importBtn, ImportTipTitleKey, "Import", () => "Import a shared [gold].zip[/gold] run archive (or drag and drop onto the window).");
        }
        catch (Exception ex)
        {
            Log.Error($"[RunResumer] Failed to attach Export/Import buttons to Run History: {ex}");
        }
    }

    public static void UpdateExportState(NRunHistory historyScreen, RunHistory? history)
    {
        if (!UiStates.TryGetValue(historyScreen, out ShareUiState? state))
        {
            return;
        }

        if (history == null)
        {
            SetButtonEnabled(state.ExportButton, state.ExportLabelContainer, enabled: false);
            state.ExportTipDescription = "No run loaded.";
            return;
        }

        if (history.Players.Count != 1)
        {
            SetButtonEnabled(state.ExportButton, state.ExportLabelContainer, enabled: false);
            state.ExportTipDescription = "Export unavailable for multiplayer runs.";
            return;
        }

        if (!RunArchiveStorage.HasAnyFloor(history))
        {
            SetButtonEnabled(state.ExportButton, state.ExportLabelContainer, enabled: false);
            state.ExportTipDescription = "Export unavailable: no Run Resumer snapshots saved for this run.";
            return;
        }

        SetButtonEnabled(state.ExportButton, state.ExportLabelContainer, enabled: true);
        state.ExportTipDescription = "Export this run's history and Run Resumer snapshots as a [gold].zip[/gold] archive.";
    }

    public static void ImportDroppedFile(string filePath)
    {
        ImportFileAndNotify(filePath, activeHistoryScreen: null);
    }

    public static void SelectRunInHistory(NRunHistory historyScreen, long startTime)
    {
        List<string> runNames = RunNamesRef(historyScreen);
        runNames.Clear();
        runNames.AddRange(SaveManager.Instance.GetAllRunHistoryNames());
        runNames.Reverse();

        int index = runNames.IndexOf($"{startTime}.run");
        if (index >= 0)
        {
            TaskHelper.RunSafely(RefreshAndSelectRunDelegate(historyScreen, index));
        }
    }

    private static NButton InstantiateShareButton(
        PackedScene buttonScene,
        string name,
        string labelText,
        out Control labelContainer)
    {
        NButton button = buttonScene.Instantiate<NButton>(PackedScene.GenEditState.Disabled);
        button.Name = name;
        button.CustomMinimumSize = new Vector2(172f, 64f);

        // NShareButton._Ready() runs on tree entry and overwrites %Label and disables the button.
        button.Connect(Node.SignalName.Ready, Callable.From(() =>
        {
            Control? icon = button.GetNodeOrNull<Control>("LabelContainer/HBoxContainer/Icon");
            if (icon != null)
            {
                icon.Visible = false;
            }

            MegaLabel? label = button.GetNodeOrNull<MegaLabel>("%Label");
            label?.SetTextAutoSize(labelText);
        }));

        labelContainer = button.GetNode<Control>("LabelContainer");
        WireButtonHighlight(button);
        return button;
    }

    private static void WireButtonHighlight(NButton button)
    {
        TextureRect? buttonImage = button.GetNodeOrNull<TextureRect>("%ButtonImage");
        TextureRect? shadow = buttonImage?.GetNodeOrNull<TextureRect>("Shadow");
        MegaLabel? label = button.GetNodeOrNull<MegaLabel>("%Label");

        if (buttonImage != null)
        {
            // Move the scene's dark tint from Modulate to SelfModulate so Shadow stays dark black.
            buttonImage.Modulate = Colors.White;
            buttonImage.SelfModulate = RestButtonPillColor;
        }

        if (shadow != null)
        {
            shadow.Modulate = ButtonShadowColor;
        }

        button.PivotOffset = new Vector2(86f, 32f);
        Tween? hoverTween = null;
        bool isFocused = false;

        void UpdatePivot()
        {
            button.PivotOffset = button.Size * 0.5f;
        }

        void AnimateTo(Vector2 targetScale, Color pillColor, Color fontColor, double duration, bool fast)
        {
            if (!GodotObject.IsInstanceValid(button) || !button.IsInsideTree())
            {
                return;
            }

            UpdatePivot();
            hoverTween?.Kill();
            hoverTween = button.CreateTween().SetParallel();

            PropertyTweener scaleTweener = hoverTween.TweenProperty(button, "scale", targetScale, duration);
            if (!fast)
            {
                scaleTweener.SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
            }

            if (buttonImage != null && GodotObject.IsInstanceValid(buttonImage))
            {
                PropertyTweener pillTweener = hoverTween.TweenProperty(buttonImage, "self_modulate", pillColor, duration);
                if (!fast)
                {
                    pillTweener.SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
                }
            }

            if (label != null && GodotObject.IsInstanceValid(label))
            {
                PropertyTweener labelTweener = hoverTween.TweenProperty(label, "theme_override_colors/font_color", fontColor, fast ? duration : 0.12);
                if (!fast)
                {
                    labelTweener.SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
                }
            }
        }

        void SetHovered() => AnimateTo(HoverScale, HoverButtonPillColor, StsColors.gold, 0.05, fast: true);
        void SetRest() => AnimateTo(Vector2.One, RestButtonPillColor, StsColors.cream, 0.25, fast: false);
        void SetPressed() => AnimateTo(PressScale, PressButtonPillColor, StsColors.lightGray, 0.12, fast: false);

        button.Connect(NClickableControl.SignalName.Focused, Callable.From<NClickableControl>(_ =>
        {
            isFocused = true;
            SetHovered();
        }));
        button.Connect(NClickableControl.SignalName.Unfocused, Callable.From<NClickableControl>(_ =>
        {
            isFocused = false;
            SetRest();
        }));
        button.Connect(NClickableControl.SignalName.MousePressed, Callable.From<InputEvent>(inputEvent =>
        {
            if (inputEvent is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                SetPressed();
            }
        }));
        button.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ =>
        {
            if (isFocused)
            {
                SetHovered();
            }
            else
            {
                SetRest();
            }
        }));
        button.Connect(Control.SignalName.Resized, Callable.From(UpdatePivot));
        button.Connect(Node.SignalName.TreeExiting, Callable.From(() => hoverTween?.Kill()));
    }

    private static void SetButtonEnabled(NButton button, Control labelContainer, bool enabled)
    {
        if (enabled)
        {
            button.Enable();
            labelContainer.Modulate = Colors.White;
        }
        else
        {
            button.Disable();
            labelContainer.Modulate = DisabledLabelModulate;
        }
    }

    private static void WireHoverTip(Control button, string titleKey, string titleText, Func<string> getDescription)
    {
        bool isHovered = false;
        bool isFocused = false;
        bool tipShown = false;

        void RefreshTip()
        {
            bool shouldShow = button.IsVisibleInTree() && (isHovered || isFocused);
            if (shouldShow == tipShown)
            {
                return;
            }

            tipShown = shouldShow;
            if (shouldShow)
            {
                EnsureTipTitleRegistered(titleKey, titleText);
                HoverTip tip = new HoverTip(new LocString(HoverTipTable, titleKey), getDescription());
                NHoverTipSet? tipSet = NHoverTipSet.CreateAndShow(button, tip);
                if (tipSet != null)
                {
                    tipSet.SetGlobalPosition(button.GlobalPosition + new Vector2(button.Size.X - tipSet.Size.X, -tipSet.Size.Y - 12f));
                }
            }
            else
            {
                NHoverTipSet.Remove(button);
            }
        }

        button.Connect(Control.SignalName.MouseEntered, Callable.From(() =>
        {
            isHovered = true;
            RefreshTip();
        }));
        button.Connect(Control.SignalName.MouseExited, Callable.From(() =>
        {
            isHovered = false;
            RefreshTip();
        }));
        button.Connect(Control.SignalName.FocusEntered, Callable.From(() =>
        {
            isFocused = true;
            RefreshTip();
        }));
        button.Connect(Control.SignalName.FocusExited, Callable.From(() =>
        {
            isFocused = false;
            RefreshTip();
        }));
        button.Connect(Node.SignalName.TreeExiting, Callable.From(() =>
        {
            if (tipShown)
            {
                tipShown = false;
                NHoverTipSet.Remove(button);
            }
        }));
    }

    private static void EnsureTipTitleRegistered(string titleKey, string titleText)
    {
        if (LocManager.Instance == null || LocString.Exists(HoverTipTable, titleKey))
        {
            return;
        }

        LocManager.Instance.GetTable(HoverTipTable).MergeWith(new Dictionary<string, string>
        {
            [titleKey] = titleText
        });
    }

    private static void OnExportPressed(NRunHistory historyScreen)
    {
        RunHistory? history = HistoryRef(historyScreen);
        if (history == null || !RunArchiveStorage.HasAnyFloor(history))
        {
            ShowErrorPopup("Export Unavailable", "This run has no Run Resumer snapshots to export.");
            return;
        }

        ShowFileDialog(
            historyScreen,
            FileDialog.FileModeEnum.SaveFile,
            "Export Shared Run",
            RunShareService.BuildDefaultFileName(history),
            selectedPath =>
            {
                try
                {
                    string writtenPath = RunShareService.ExportToFile(history, selectedPath);
                    Log.Info($"[RunResumer] Exported shared run {history.StartTime} to '{writtenPath}'");
                    ShowToast($"Exported {Path.GetFileName(writtenPath)}");
                }
                catch (Exception ex)
                {
                    Log.Error($"[RunResumer] Failed to export run {history.StartTime}: {ex}");
                    ShowErrorPopup("Export Failed", ex.Message);
                }
            });
    }

    private static void OnImportPressed(NRunHistory historyScreen)
    {
        ShowFileDialog(
            historyScreen,
            FileDialog.FileModeEnum.OpenFile,
            "Import Shared Run",
            defaultFileName: null,
            selectedPath => ImportFileAndNotify(selectedPath, historyScreen));
    }

    private static void ImportFileAndNotify(string filePath, NRunHistory? activeHistoryScreen)
    {
        try
        {
            ShareImportResult result = RunShareService.ImportFromFile(filePath);
            Log.Info($"[RunResumer] Imported shared run '{filePath}' as StartTime={result.StartTime} ({result.FloorCount} floors)");

            if (activeHistoryScreen != null && GodotObject.IsInstanceValid(activeHistoryScreen))
            {
                SelectRunInHistory(activeHistoryScreen, result.StartTime);
                string renameNote = result.WasRenamedOnCollision ? " (copied with new timestamp)" : string.Empty;
                ShowToast($"Imported {result.FloorCount}-floor run{renameNote}");
                return;
            }

            ShowImportedInUi(result);
        }
        catch (RunShareValidationException ex)
        {
            Log.Warn($"[RunResumer] Rejected imported zip '{filePath}': {ex.Message}");
            ShowErrorPopup("Import Failed", ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error($"[RunResumer] Failed to import run '{filePath}': {ex}");
            ShowErrorPopup("Import Failed", $"Could not import shared run: {ex.Message}");
        }
    }

    private static void ShowFileDialog(
        Node parent,
        FileDialog.FileModeEnum fileMode,
        string title,
        string? defaultFileName,
        Action<string> onSelected)
    {
        FileDialog dialog = new FileDialog
        {
            Title = title,
            FileMode = fileMode,
            Access = FileDialog.AccessEnum.Filesystem,
            UseNativeDialog = true,
            Filters = new[] { "*.zip ; Run Resumer Archive" },
            MinSize = new Vector2I(820, 520),
            CurrentDir = GetInitialDirectory()
        };

        if (!string.IsNullOrEmpty(defaultFileName))
        {
            dialog.CurrentFile = defaultFileName;
        }

        dialog.Connect(FileDialog.SignalName.FileSelected, Callable.From<string>(path =>
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                _lastDirectory = dir;
            }
            dialog.QueueFreeSafely();
            onSelected(path);
        }));

        dialog.Connect(FileDialog.SignalName.Canceled, Callable.From(() =>
        {
            dialog.QueueFreeSafely();
        }));

        parent.AddChildSafely(dialog);
        dialog.PopupCentered();
    }

    private static string GetInitialDirectory()
    {
        if (!string.IsNullOrEmpty(_lastDirectory) && Directory.Exists(_lastDirectory))
        {
            return _lastDirectory;
        }

        string downloads = OS.GetSystemDir(OS.SystemDir.Downloads);
        if (!string.IsNullOrEmpty(downloads) && Directory.Exists(downloads))
        {
            return downloads;
        }

        string desktop = OS.GetSystemDir(OS.SystemDir.Desktop);
        if (!string.IsNullOrEmpty(desktop) && Directory.Exists(desktop))
        {
            return desktop;
        }

        return ProjectSettings.GlobalizePath("user://");
    }

    private static void ShowImportedInUi(ShareImportResult result)
    {
        string renameNote = result.WasRenamedOnCollision ? " (copied with new timestamp)" : string.Empty;
        string toast = $"Imported {result.FloorCount}-floor run{renameNote}";

        NMainMenu? mainMenu = NGame.Instance?.MainMenu;
        if (mainMenu != null && GodotObject.IsInstanceValid(mainMenu) && !RunManager.Instance.IsInProgress)
        {
            NSubmenu? top = mainMenu.SubmenuStack.Peek();
            if (top is NRunHistory openHistory)
            {
                SelectRunInHistory(openHistory, result.StartTime);
                ShowToast(toast);
                return;
            }

            if (top is NCompendiumSubmenu)
            {
                NRunHistory historyScreen = mainMenu.SubmenuStack.PushSubmenuType<NRunHistory>();
                Callable.From(() => SelectRunInHistory(historyScreen, result.StartTime)).CallDeferred();
                ShowToast(toast);
                return;
            }

            if (!mainMenu.SubmenuStack.SubmenusOpen)
            {
                mainMenu.OpenCompendiumSubmenu();
                NRunHistory historyScreen = mainMenu.SubmenuStack.PushSubmenuType<NRunHistory>();
                Callable.From(() => SelectRunInHistory(historyScreen, result.StartTime)).CallDeferred();
                ShowToast(toast);
                return;
            }
        }

        ShowToast($"{toast} — view in Run History");
    }

    private static void ShowToast(string message)
    {
        NFullscreenTextVfx? vfx = NFullscreenTextVfx.Create(message);
        if (vfx != null)
        {
            NGame.Instance?.AddChildSafely(vfx);
        }
    }

    private static void ShowErrorPopup(string title, string message)
    {
        NErrorPopup? popup = NErrorPopup.Create(title, message, showReportBugButton: false);
        if (popup != null && NModalContainer.Instance != null)
        {
            NModalContainer.Instance.Add(popup);
            return;
        }

        ShowToast($"{title}: {message}");
    }
}

[HarmonyPatch(typeof(NRunHistory), nameof(NRunHistory._Ready))]
internal static class RunHistoryShareReadyPatch
{
    public static void Postfix(NRunHistory __instance)
    {
        RunHistoryShareUiHelper.AttachButtons(__instance);
    }
}

[HarmonyPatch(typeof(NRunHistory), "DisplayRun")]
internal static class RunHistoryShareDisplayPatch
{
    public static void Postfix(NRunHistory __instance, RunHistory history)
    {
        RunHistoryShareUiHelper.UpdateExportState(__instance, history);
    }
}

[HarmonyPatch(typeof(FileDropHandler), nameof(FileDropHandler.OnFilesDropped))]
internal static class FileDropShareImportPatch
{
    public static bool Prefix(string[] files)
    {
        if (files == null || files.Length != 1)
        {
            return true;
        }

        string droppedPath = files[0];
        if (!droppedPath.EndsWith(RunShareService.ShareExtension, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        RunHistoryShareUiHelper.ImportDroppedFile(droppedPath);
        return false;
    }
}
