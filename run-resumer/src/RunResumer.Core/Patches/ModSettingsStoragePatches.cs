using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using RunResumer.Storage;

namespace RunResumer.Patches;

internal static class ModSettingsStorageHelper
{
    private const string RunResumerModId = "RunResumer";
    private const string PanelNodeName = "RunResumerStoragePanel";
    private const string KreonBoldPath = "res://themes/kreon_bold_glyph_space_one.tres";
    private const string KreonRegularPath = "res://themes/kreon_regular_glyph_space_one.tres";
    private const string MainMenuUiTable = "main_menu_ui";
    private const string PrunePopupHeaderKey = "RUN_RESUMER_PRUNE.header";
    private const string PrunePopupBodyKey = "RUN_RESUMER_PRUNE.body";

    private static readonly Color PanelBgColor = new(0.06f, 0.11f, 0.14f, 0.92f);
    private static readonly Color PanelBorderColor = new("3C5B6B");
    private static readonly Color RowBgColor = new("14232B");
    private static readonly Color RowBorderColor = new("2C434F");
    private static readonly Color ActiveRowBorderColor = new("B5943B");
    private static readonly Color GoldColor = new("EFC851");
    private static readonly Color CreamColor = new("FFF6E2");
    private static readonly Color MutedColor = new(0.74f, 0.80f, 0.78f, 0.85f);
    private static readonly Color BtnRestColor = new("243945");
    private static readonly Color BtnHoverColor = new("355464");
    private static readonly Color BtnPressColor = new("1A2A33");
    private static readonly Color DeleteRestColor = new(0.34f, 0.13f, 0.13f, 0.95f);
    private static readonly Color DeleteHoverColor = new(0.52f, 0.17f, 0.17f, 0.98f);
    private static readonly Color DeletePressColor = new(0.25f, 0.09f, 0.09f, 0.95f);

    private sealed class ProfileRowWidgets
    {
        public required PanelContainer Card;
        public required Label TitleLabel;
        public required Label UsageLabel;
        public required Button MinusButton;
        public required LineEdit CountInput;
        public required Button PlusButton;
        public required Button DeleteButton;
        public int MaxFiles;
    }

    private sealed class StoragePanelState
    {
        public required PanelContainer Root;
        public required Label TotalLabel;
        public required Dictionary<int, ProfileRowWidgets> Rows;
    }

    private static readonly ConditionalWeakTable<NModInfoContainer, StoragePanelState> Panels = new();
    private static readonly List<WeakReference<NModInfoContainer>> TrackedContainers = new();

    private static readonly AccessTools.FieldRef<NModdingScreen, NModInfoContainer> ModInfoContainerRef =
        AccessTools.FieldRefAccess<NModdingScreen, NModInfoContainer>("_modInfoContainer");

    private static readonly AccessTools.FieldRef<NModdingScreen, Control> ModRowContainerRef =
        AccessTools.FieldRefAccess<NModdingScreen, Control>("_modRowContainer");

    private static readonly AccessTools.FieldRef<NModMenuRow, bool> RowIsSelectedRef =
        AccessTools.FieldRefAccess<NModMenuRow, bool>("_isSelected");

    public static void OnModInfoFilled(NModInfoContainer container, Mod? mod)
    {
        bool isRunResumer = string.Equals(mod?.manifest?.id, RunResumerModId, StringComparison.OrdinalIgnoreCase);
        if (!isRunResumer)
        {
            HidePanel(container);
            return;
        }

        StoragePanelState state = GetOrCreatePanel(container);
        state.Root.Visible = true;
        RefreshStats(state);
    }

    public static void OnModInfoCleared(NModInfoContainer container)
    {
        HidePanel(container);
    }

    public static void ReattachToLiveModdingScreen(NModdingScreen screen)
    {
        if (!GodotObject.IsInstanceValid(screen))
        {
            return;
        }

        NModInfoContainer? info = ModInfoContainerRef(screen);
        Control? rowContainer = ModRowContainerRef(screen);
        if (info == null || rowContainer == null || !GodotObject.IsInstanceValid(info) || !GodotObject.IsInstanceValid(rowContainer))
        {
            return;
        }

        foreach (NModMenuRow row in rowContainer.GetChildren().OfType<NModMenuRow>())
        {
            if (RowIsSelectedRef(row) &&
                string.Equals(row.Mod?.manifest?.id, RunResumerModId, StringComparison.OrdinalIgnoreCase))
            {
                OnModInfoFilled(info, row.Mod);
                return;
            }
        }
    }

    public static void DetachAll()
    {
        for (int i = TrackedContainers.Count - 1; i >= 0; i--)
        {
            if (TrackedContainers[i].TryGetTarget(out NModInfoContainer? container) &&
                GodotObject.IsInstanceValid(container))
            {
                if (Panels.TryGetValue(container, out StoragePanelState? state))
                {
                    if (GodotObject.IsInstanceValid(state.Root))
                    {
                        state.Root.GetParent()?.RemoveChild(state.Root);
                        state.Root.QueueFree();
                    }
                    Panels.Remove(container);
                }
            }
        }

        TrackedContainers.Clear();
    }

    private static void HidePanel(NModInfoContainer container)
    {
        if (Panels.TryGetValue(container, out StoragePanelState? state) && GodotObject.IsInstanceValid(state.Root))
        {
            state.Root.Visible = false;
        }
    }

    private static StoragePanelState GetOrCreatePanel(NModInfoContainer container)
    {
        if (Panels.TryGetValue(container, out StoragePanelState? existing) && GodotObject.IsInstanceValid(existing.Root))
        {
            return existing;
        }

        Font? boldFont = ResourceLoader.Exists(KreonBoldPath) ? GD.Load<Font>(KreonBoldPath) : null;
        Font? regularFont = ResourceLoader.Exists(KreonRegularPath) ? GD.Load<Font>(KreonRegularPath) : null;

        Control? modImage = container.GetNodeOrNull<Control>("ModImage");
        float topOffset = modImage != null && modImage.Position.Y > 40f ? modImage.Position.Y : 108f;

        PanelContainer panel = new PanelContainer
        {
            Name = PanelNodeName,
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        panel.AnchorLeft = 0f;
        panel.AnchorRight = 1f;
        panel.AnchorTop = 0f;
        panel.AnchorBottom = 0f;
        panel.GrowHorizontal = Control.GrowDirection.Both;
        panel.GrowVertical = Control.GrowDirection.End;
        panel.OffsetLeft = 24f;
        panel.OffsetRight = -24f;
        panel.OffsetTop = topOffset;
        panel.OffsetBottom = topOffset + 250f;
        panel.AddThemeStyleboxOverride("panel", CreateBoxStyle(PanelBgColor, PanelBorderColor, 1, 6, 14, 12));
        container.AddChild(panel);

        VBoxContainer outerVbox = new VBoxContainer();
        outerVbox.AddThemeConstantOverride("separation", 10);
        panel.AddChild(outerVbox);

        HBoxContainer headerRow = new HBoxContainer();
        headerRow.AddThemeConstantOverride("separation", 8);
        outerVbox.AddChild(headerRow);

        Label titleLabel = new Label
        {
            Text = "Disk Space Usage",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (boldFont != null) titleLabel.AddThemeFontOverride("font", boldFont);
        titleLabel.AddThemeFontSizeOverride("font_size", 20);
        titleLabel.AddThemeColorOverride("font_color", GoldColor);
        headerRow.AddChild(titleLabel);

        Label totalLabel = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (regularFont != null) totalLabel.AddThemeFontOverride("font", regularFont);
        totalLabel.AddThemeFontSizeOverride("font_size", 16);
        totalLabel.AddThemeColorOverride("font_color", CreamColor);
        headerRow.AddChild(totalLabel);

        var rows = new Dictionary<int, ProfileRowWidgets>();
        StoragePanelState state = new StoragePanelState
        {
            Root = panel,
            TotalLabel = totalLabel,
            Rows = rows
        };

        for (int profileId = 1; profileId <= 3; profileId++)
        {
            ProfileRowWidgets row = BuildProfileRow(profileId, boldFont, regularFont, state);
            rows[profileId] = row;
            outerVbox.AddChild(row.Card);
        }

        Panels.AddOrUpdate(container, state);
        TrackedContainers.Add(new WeakReference<NModInfoContainer>(container));
        return state;
    }

    private static ProfileRowWidgets BuildProfileRow(
        int profileId,
        Font? boldFont,
        Font? regularFont,
        StoragePanelState state)
    {
        PanelContainer card = new PanelContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        card.AddThemeStyleboxOverride("panel", CreateBoxStyle(RowBgColor, RowBorderColor, 1, 5, 10, 7));

        HBoxContainer rowHbox = new HBoxContainer();
        rowHbox.AddThemeConstantOverride("separation", 8);
        card.AddChild(rowHbox);

        VBoxContainer infoVbox = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        infoVbox.AddThemeConstantOverride("separation", 2);
        rowHbox.AddChild(infoVbox);

        Label profileTitle = new Label
        {
            Text = $"Profile {profileId}",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        if (boldFont != null) profileTitle.AddThemeFontOverride("font", boldFont);
        profileTitle.AddThemeFontSizeOverride("font_size", 17);
        profileTitle.AddThemeColorOverride("font_color", CreamColor);
        infoVbox.AddChild(profileTitle);

        Label usageLabel = new Label
        {
            Text = "0 files · 0 B",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        if (regularFont != null) usageLabel.AddThemeFontOverride("font", regularFont);
        usageLabel.AddThemeFontSizeOverride("font_size", 15);
        usageLabel.AddThemeColorOverride("font_color", MutedColor);
        infoVbox.AddChild(usageLabel);

        HBoxContainer controls = new HBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.End,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
        };
        controls.AddThemeConstantOverride("separation", 5);
        rowHbox.AddChild(controls);

        Button minusBtn = CreateActionButton("−", boldFont, new Vector2(28f, 32f), isDelete: false);
        LineEdit countInput = new LineEdit
        {
            Text = "0",
            Alignment = HorizontalAlignment.Center,
            CustomMinimumSize = new Vector2(48f, 32f),
            MaxLength = 5,
            CaretBlink = true
        };
        if (regularFont != null) countInput.AddThemeFontOverride("font", regularFont);
        countInput.AddThemeFontSizeOverride("font_size", 16);
        countInput.AddThemeColorOverride("font_color", CreamColor);
        countInput.AddThemeStyleboxOverride("normal", CreateBoxStyle(new Color("0B141A"), RowBorderColor, 1, 4, 4, 2));
        countInput.AddThemeStyleboxOverride("focus", CreateBoxStyle(new Color("0B141A"), GoldColor, 1, 4, 4, 2));

        Button plusBtn = CreateActionButton("+", boldFont, new Vector2(28f, 32f), isDelete: false);
        Button deleteBtn = CreateActionButton("Delete Oldest", boldFont, new Vector2(124f, 32f), isDelete: true);

        controls.AddChild(minusBtn);
        controls.AddChild(countInput);
        controls.AddChild(plusBtn);
        controls.AddChild(deleteBtn);

        ProfileRowWidgets widgets = new ProfileRowWidgets
        {
            Card = card,
            TitleLabel = profileTitle,
            UsageLabel = usageLabel,
            MinusButton = minusBtn,
            CountInput = countInput,
            PlusButton = plusBtn,
            DeleteButton = deleteBtn
        };

        int capturedProfileId = profileId;
        minusBtn.Pressed += () => AdjustCount(widgets, Input.IsKeyPressed(Key.Shift) ? -10 : -1);
        plusBtn.Pressed += () => AdjustCount(widgets, Input.IsKeyPressed(Key.Shift) ? 10 : 1);
        countInput.TextChanged += text => OnCountTextChanged(widgets, text);
        countInput.FocusExited += () => NormalizeCountInput(widgets);
        deleteBtn.Pressed += () =>
        {
            int count = ParseClampedCount(widgets);
            if (count > 0)
            {
                TaskHelper.RunSafely(ConfirmAndPruneAsync(state, capturedProfileId, count));
            }
        };

        return widgets;
    }

    private static void RefreshStats(StoragePanelState state)
    {
        IReadOnlyList<ProfileStorageInfo> stats = RunArchiveStorage.GetAllProfileStorageStats();
        int totalFiles = 0;
        long totalBytes = 0L;

        foreach (ProfileStorageInfo info in stats)
        {
            totalFiles += info.FileCount;
            totalBytes += info.TotalBytes;

            if (!state.Rows.TryGetValue(info.ProfileId, out ProfileRowWidgets? row))
            {
                continue;
            }

            row.MaxFiles = info.FileCount;
            row.TitleLabel.Text = info.IsCurrentProfile ? $"Profile {info.ProfileId} (Active)" : $"Profile {info.ProfileId}";
            row.TitleLabel.AddThemeColorOverride("font_color", info.IsCurrentProfile ? GoldColor : CreamColor);
            row.Card.AddThemeStyleboxOverride(
                "panel",
                CreateBoxStyle(RowBgColor, info.IsCurrentProfile ? ActiveRowBorderColor : RowBorderColor, 1, 5, 10, 7));

            if (info.FileCount == 0)
            {
                row.UsageLabel.Text = "No snapshots · 0 B";
                row.UsageLabel.AddThemeColorOverride("font_color", MutedColor);
                row.CountInput.Text = "0";
                row.CountInput.Editable = false;
                row.MinusButton.Disabled = true;
                row.PlusButton.Disabled = true;
                row.DeleteButton.Disabled = true;
            }
            else
            {
                string fileWord = info.FileCount == 1 ? "file" : "files";
                row.UsageLabel.Text = $"{info.FileCount} {fileWord} · {RunArchiveStorage.FormatBytes(info.TotalBytes)}";
                row.UsageLabel.AddThemeColorOverride("font_color", CreamColor);

                int current = ParseRawCount(row.CountInput.Text);
                int desired = current > 0 ? Math.Min(current, info.FileCount) : Math.Min(10, info.FileCount);
                row.CountInput.Text = desired.ToString(CultureInfo.InvariantCulture);
                row.CountInput.Editable = true;
                row.MinusButton.Disabled = false;
                row.PlusButton.Disabled = false;
                row.DeleteButton.Disabled = false;
            }
        }

        string totalWord = totalFiles == 1 ? "file" : "files";
        state.TotalLabel.Text = $"Total: {RunArchiveStorage.FormatBytes(totalBytes)} ({totalFiles} {totalWord})";
    }

    private static void AdjustCount(ProfileRowWidgets row, int delta)
    {
        if (row.MaxFiles <= 0)
        {
            return;
        }

        int current = ParseRawCount(row.CountInput.Text);
        if (current <= 0)
        {
            current = 1;
        }

        int next = Math.Clamp(current + delta, 1, row.MaxFiles);
        row.CountInput.Text = next.ToString(CultureInfo.InvariantCulture);
        row.DeleteButton.Disabled = false;
    }

    private static void OnCountTextChanged(ProfileRowWidgets row, string text)
    {
        if (row.MaxFiles <= 0)
        {
            row.DeleteButton.Disabled = true;
            return;
        }

        string digitsOnly = new string(text.Where(char.IsDigit).ToArray());
        if (digitsOnly != text)
        {
            row.CountInput.Text = digitsOnly;
            row.CountInput.CaretColumn = digitsOnly.Length;
        }

        int parsed = ParseRawCount(digitsOnly);
        row.DeleteButton.Disabled = parsed <= 0;
    }

    private static void NormalizeCountInput(ProfileRowWidgets row)
    {
        if (row.MaxFiles <= 0)
        {
            row.CountInput.Text = "0";
            row.DeleteButton.Disabled = true;
            return;
        }

        int clamped = ParseClampedCount(row);
        row.CountInput.Text = clamped.ToString(CultureInfo.InvariantCulture);
        row.DeleteButton.Disabled = clamped <= 0;
    }

    private static int ParseRawCount(string? text)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int val) ? val : 0;
    }

    private static int ParseClampedCount(ProfileRowWidgets row)
    {
        if (row.MaxFiles <= 0)
        {
            return 0;
        }

        int raw = ParseRawCount(row.CountInput.Text);
        return Math.Clamp(raw <= 0 ? 1 : raw, 1, row.MaxFiles);
    }

    private static async Task ConfirmAndPruneAsync(StoragePanelState state, int profileId, int count)
    {
        try
        {
            string fileWord = count == 1 ? "file" : "files";
            EnsureLocStringRegistered(MainMenuUiTable, PrunePopupHeaderKey, "Delete Oldest Snapshots");
            EnsureLocStringRegistered(
                MainMenuUiTable,
                PrunePopupBodyKey,
                $"Delete the oldest {count} Run Resumer snapshot {fileWord} from Profile {profileId}?\n\nYour Run History entries will be kept, but those runs will no longer be replayable.");

            NGenericPopup? popup = NGenericPopup.Create();
            if (popup != null && NModalContainer.Instance != null)
            {
                NModalContainer.Instance.Add(popup);
                bool confirmed = await popup.WaitForConfirmation(
                    new LocString(MainMenuUiTable, PrunePopupBodyKey),
                    new LocString(MainMenuUiTable, PrunePopupHeaderKey),
                    new LocString(MainMenuUiTable, "GENERIC_POPUP.cancel"),
                    new LocString(MainMenuUiTable, "GENERIC_POPUP.confirm"));

                if (!confirmed)
                {
                    return;
                }
            }

            PruneResult result = RunArchiveStorage.DeleteOldestArchives(profileId, count);
            if (GodotObject.IsInstanceValid(state.Root))
            {
                RefreshStats(state);
            }

            if (result.DeletedCount > 0)
            {
                string deletedWord = result.DeletedCount == 1 ? "snapshot" : "snapshots";
                ShowToast($"Deleted {result.DeletedCount} oldest {deletedWord} from Profile {profileId} (freed {RunArchiveStorage.FormatBytes(result.FreedBytes)})");
            }
            else
            {
                ShowToast($"No snapshots could be deleted from Profile {profileId} (active run is protected)");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[RunResumer] Failed to prune archives for Profile {profileId}: {ex}");
        }
    }

    private static void EnsureLocStringRegistered(string tableName, string key, string value)
    {
        LocTable? table = LocManager.Instance?.GetTable(tableName);
        if (table == null)
        {
            return;
        }

        var translations = AccessTools.Field(typeof(LocTable), "_translations")?.GetValue(table) as Dictionary<string, string>;
        if (translations != null)
        {
            translations[key] = value;
        }
    }

    private static void ShowToast(string message)
    {
        NFullscreenTextVfx? vfx = NFullscreenTextVfx.Create(message);
        if (vfx != null)
        {
            NGame.Instance?.AddChildSafely(vfx);
        }
    }

    private static Button CreateActionButton(string text, Font? font, Vector2 minSize, bool isDelete)
    {
        Button btn = new Button
        {
            Text = text,
            CustomMinimumSize = minSize,
            FocusMode = Control.FocusModeEnum.None
        };
        if (font != null) btn.AddThemeFontOverride("font", font);
        btn.AddThemeFontSizeOverride("font_size", 15);
        btn.AddThemeColorOverride("font_color", CreamColor);
        btn.AddThemeColorOverride("font_hover_color", GoldColor);
        btn.AddThemeColorOverride("font_pressed_color", GoldColor);
        btn.AddThemeColorOverride("font_disabled_color", new Color(0.6f, 0.65f, 0.65f, 0.45f));

        Color rest = isDelete ? DeleteRestColor : BtnRestColor;
        Color hover = isDelete ? DeleteHoverColor : BtnHoverColor;
        Color press = isDelete ? DeletePressColor : BtnPressColor;
        Color border = isDelete ? new Color(0.65f, 0.28f, 0.28f, 0.9f) : new Color("4A6E80");

        btn.AddThemeStyleboxOverride("normal", CreateBoxStyle(rest, border, 1, 4, 8, 4));
        btn.AddThemeStyleboxOverride("hover", CreateBoxStyle(hover, isDelete ? new Color("FF7A7A") : GoldColor, 1, 4, 8, 4));
        btn.AddThemeStyleboxOverride("pressed", CreateBoxStyle(press, border, 1, 4, 8, 4));
        btn.AddThemeStyleboxOverride("disabled", CreateBoxStyle(new Color(0.12f, 0.16f, 0.19f, 0.6f), new Color(0.22f, 0.28f, 0.32f, 0.5f), 1, 4, 8, 4));
        return btn;
    }

    private static StyleBoxFlat CreateBoxStyle(Color bg, Color border, int borderWidth, int radius, int padX, int padY) => new()
    {
        BgColor = bg,
        BorderColor = border,
        BorderWidthLeft = borderWidth,
        BorderWidthTop = borderWidth,
        BorderWidthRight = borderWidth,
        BorderWidthBottom = borderWidth,
        CornerRadiusTopLeft = radius,
        CornerRadiusTopRight = radius,
        CornerRadiusBottomLeft = radius,
        CornerRadiusBottomRight = radius,
        ContentMarginLeft = padX,
        ContentMarginTop = padY,
        ContentMarginRight = padX,
        ContentMarginBottom = padY
    };
}

[HarmonyPatch(typeof(NModInfoContainer), nameof(NModInfoContainer.Fill))]
internal static class ModInfoContainerFillPatch
{
    public static void Postfix(NModInfoContainer __instance, Mod mod)
    {
        try
        {
            ModSettingsStorageHelper.OnModInfoFilled(__instance, mod);
        }
        catch (Exception ex)
        {
            Log.Error($"[RunResumer] Error showing storage panel in Mod Settings: {ex}");
        }
    }
}

[HarmonyPatch(typeof(NModInfoContainer), nameof(NModInfoContainer.Clear))]
internal static class ModInfoContainerClearPatch
{
    public static void Postfix(NModInfoContainer __instance)
    {
        try
        {
            ModSettingsStorageHelper.OnModInfoCleared(__instance);
        }
        catch (Exception ex)
        {
            Log.Error($"[RunResumer] Error clearing storage panel in Mod Settings: {ex}");
        }
    }
}
