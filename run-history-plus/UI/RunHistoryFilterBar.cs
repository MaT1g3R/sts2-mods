using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Debug;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using RunHistoryPlus.Core;

namespace RunHistoryPlus.UI;

/// <summary>
/// Attaches native STS2 filter dropdowns, run selector dropdown, sort controls,
/// "More Filters" button, and empty-filter state to NRunHistory.
///
/// Performance design:
/// - Opening Run History applies the default options (All / All / Any / Date Desc), which
///   already matches vanilla ordering, so ZERO extra .run files are parsed on initial open.
/// - Indexing all .run files only happens on demand (when a filter/sort is changed or the
///   Run Selector dropdown is opened), runs in parallel on background threads directly from
///   the profile's disk history directory, and caches results for the entire session.
/// </summary>
internal sealed class RunHistoryFilterBar
{
    private const string KreonBoldPath = "res://themes/kreon_bold_glyph_space_one.tres";
    private const string KreonRegularPath = "res://themes/kreon_regular_glyph_space_one.tres";

    private static readonly Color CreamColor = new("FFF6E2");
    private static readonly Color GoldColor = new("EFC851");
    private static readonly Color MutedColor = new(0.82f, 0.87f, 0.85f, 0.85f);

    // Persists across Run History opens for the entire game session so re-indexing is instant.
    private static readonly ConcurrentDictionary<string, RunSummary> GlobalSummaryCache = new(StringComparer.Ordinal);

    private static readonly AccessTools.FieldRef<NRunHistory, List<string>> RunNamesRef =
        AccessTools.FieldRefAccess<NRunHistory, List<string>>("_runNames");

    private static readonly AccessTools.FieldRef<NRunHistory, int> IndexRef =
        AccessTools.FieldRefAccess<NRunHistory, int>("_index");

    private static readonly AccessTools.FieldRef<NRunHistory, RunHistory> HistoryRef =
        AccessTools.FieldRefAccess<NRunHistory, RunHistory>("_history");

    private static readonly AccessTools.FieldRef<NRunHistory, NScrollableContainer> ScreenContentsRef =
        AccessTools.FieldRefAccess<NRunHistory, NScrollableContainer>("_screenContents");

    private static readonly AccessTools.FieldRef<NRunHistory, NRunHistoryArrowButton> PrevButtonRef =
        AccessTools.FieldRefAccess<NRunHistory, NRunHistoryArrowButton>("_prevButton");

    private static readonly AccessTools.FieldRef<NRunHistory, NRunHistoryArrowButton> NextButtonRef =
        AccessTools.FieldRefAccess<NRunHistory, NRunHistoryArrowButton>("_nextButton");

    private static readonly Func<NRunHistory, int, Task> RefreshAndSelectRunDelegate =
        AccessTools.MethodDelegate<Func<NRunHistory, int, Task>>(
            AccessTools.Method(typeof(NRunHistory), "RefreshAndSelectRun", new[] { typeof(int) }));

    private static readonly Action<NRunHistory, NButton?> LeftArrowReleasedDelegate =
        AccessTools.MethodDelegate<Action<NRunHistory, NButton?>>(
            AccessTools.Method(typeof(NRunHistory), "OnLeftButtonButtonReleased", new[] { typeof(NButton) }));

    private static readonly Action<NRunHistory, NButton?> RightArrowReleasedDelegate =
        AccessTools.MethodDelegate<Action<NRunHistory, NButton?>>(
            AccessTools.Method(typeof(NRunHistory), "OnRightButtonButtonReleased", new[] { typeof(NButton) }));

    private readonly NRunHistory _screen;
    private readonly RunFilterState _state = new();
    private readonly List<RunSummary> _allRuns = new();
    private readonly List<RunSummary> _filteredRuns = new();
    private readonly List<NativeDropdown> _dropdowns = new();

    private NativeDropdown? _outcomeDropdown;
    private NativeDropdown? _characterDropdown;
    private NativeDropdown? _gameTypeDropdown;
    private Control? _runSelectorHolder;
    private NativeDropdown? _runSelectorDropdown;
    private NativeDropdown? _sortFieldDropdown;
    private NativeDropdown? _sortDirectionDropdown;
    private Font? _boldFont;

    private Label? _matchCountLabel;
    private Control? _emptyStateOverlay;
    private MoreFiltersPanel? _moreFiltersPanel;

    private int _loadGeneration;
    private bool _indexReady;
    private bool _isIndexing;
    private bool _pendingApplyAfterIndex;
    private bool _pendingRefreshRunSelectorAfterIndex;

    public RunHistoryFilterBar(NRunHistory screen)
    {
        _screen = screen;
        BuildUi();
    }

    private void BuildUi()
    {
        Font? boldFont = GD.Load<Font>(KreonBoldPath);
        Font? regularFont = GD.Load<Font>(KreonRegularPath);
        _boldFont = boldFont;

        // Nudge main content slightly down so the centered Run Selector dropdown has clean headroom
        MarginContainer? contentMargin = _screen.GetNodeOrNull<MarginContainer>("ScreenContents/Content");
        if (contentMargin != null)
        {
            contentMargin.AddThemeConstantOverride("margin_top", 78);
        }

        // --- Top-left: Outcome, Character, Game Type dropdowns + match count ---
        // Aligned to the same starting Y (88px) as the right-side Sort group.
        // Uses a plain Control with explicit positions (not VBoxContainer) so MoveChild on
        // dropdown open brings the open menu above sibling dropdowns for mouse input without
        // reordering them vertically.
        Control leftFilters = new Control
        {
            Name = "RunHistoryPlusLeftFilters",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        leftFilters.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        leftFilters.OffsetLeft = 20f;
        leftFilters.OffsetTop = 88f;
        leftFilters.OffsetRight = 240f;
        leftFilters.OffsetBottom = 310f;
        _screen.AddChild(leftFilters);

        Label filterLabel = CreateSectionLabel("Filter", boldFont);
        PlaceChild(filterLabel, 0f, 0f, 220f, 24f);
        leftFilters.AddChild(filterLabel);

        _outcomeDropdown = CreateDropdown("OutcomeDropdown", 220f, 40f, 36f, 240f, 19, 17);
        _characterDropdown = CreateDropdown("CharacterDropdown", 220f, 40f, 36f, 360f, 19, 17);
        _gameTypeDropdown = CreateDropdown("GameTypeDropdown", 220f, 40f, 36f, 280f, 19, 17);

        if (_outcomeDropdown != null)
        {
            PlaceChild(_outcomeDropdown.Root, 0f, 28f, 220f, 40f);
            leftFilters.AddChild(_outcomeDropdown.Root);
        }
        if (_characterDropdown != null)
        {
            PlaceChild(_characterDropdown.Root, 0f, 76f, 220f, 40f);
            leftFilters.AddChild(_characterDropdown.Root);
        }
        if (_gameTypeDropdown != null)
        {
            PlaceChild(_gameTypeDropdown.Root, 0f, 124f, 220f, 40f);
            leftFilters.AddChild(_gameTypeDropdown.Root);
        }

        _matchCountLabel = new Label
        {
            Name = "MatchCountLabel",
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        if (regularFont != null) _matchCountLabel.AddThemeFontOverride("font", regularFont);
        _matchCountLabel.AddThemeFontSizeOverride("font_size", 17);
        _matchCountLabel.AddThemeColorOverride("font_color", MutedColor);
        _matchCountLabel.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.5f));
        _matchCountLabel.AddThemeConstantOverride("shadow_offset_x", 2);
        _matchCountLabel.AddThemeConstantOverride("shadow_offset_y", 1);
        PlaceChild(_matchCountLabel, 0f, 172f, 220f, 28f);
        leftFilters.AddChild(_matchCountLabel);

        // --- Top-center: Run Selector dropdown ---
        _runSelectorHolder = new Control
        {
            Name = "RunHistoryPlusRunSelectorHolder",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _runSelectorHolder.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        _runSelectorHolder.OffsetLeft = -220f;
        _runSelectorHolder.OffsetRight = 220f;
        _runSelectorHolder.OffsetTop = 18f;
        _runSelectorHolder.OffsetBottom = 62f;
        _screen.AddChild(_runSelectorHolder);

        _runSelectorDropdown = CreateDropdown("RunSelectorDropdown", 440f, 42f, 38f, 480f, 20, 18, useRichText: true);
        if (_runSelectorDropdown != null)
        {
            PlaceChild(_runSelectorDropdown.Root, 0f, 0f, 440f, 42f);
            _runSelectorHolder.AddChild(_runSelectorDropdown.Root);
            ConfigureLazyRunSelectorDropdown();
        }

        // --- Top-right: Sort & Sort Direction dropdowns (offset below top-right game version string) ---
        Control rightSort = new Control
        {
            Name = "RunHistoryPlusRightSort",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        rightSort.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        rightSort.GrowHorizontal = Control.GrowDirection.Begin;
        rightSort.OffsetLeft = -236f;
        rightSort.OffsetTop = 88f;
        rightSort.OffsetRight = -16f;
        rightSort.OffsetBottom = 260f;
        _screen.AddChild(rightSort);

        Label sortLabel = CreateSectionLabel("Sort", boldFont);
        PlaceChild(sortLabel, 0f, 0f, 220f, 24f);
        rightSort.AddChild(sortLabel);

        _sortFieldDropdown = CreateDropdown("SortFieldDropdown", 220f, 40f, 36f, 260f, 19, 17);
        if (_sortFieldDropdown != null)
        {
            PlaceChild(_sortFieldDropdown.Root, 0f, 28f, 220f, 40f);
            rightSort.AddChild(_sortFieldDropdown.Root);
        }

        Label sortDirLabel = CreateSectionLabel("Sort Direction", boldFont);
        PlaceChild(sortDirLabel, 0f, 78f, 220f, 24f);
        rightSort.AddChild(sortDirLabel);

        _sortDirectionDropdown = CreateDropdown("SortDirectionDropdown", 220f, 40f, 36f, 160f, 19, 17);
        if (_sortDirectionDropdown != null)
        {
            PlaceChild(_sortDirectionDropdown.Root, 0f, 106f, 220f, 40f);
            rightSort.AddChild(_sortDirectionDropdown.Root);
        }

        // --- Left-side "More Filters" button (placed above BackButton) ---
        _moreFiltersPanel = new MoreFiltersPanel(ResetFiltersAndRefresh);

        Button moreFiltersBtn = MoreFiltersPanel.CreateStyledButton(
            "More Filters",
            boldFont,
            () =>
            {
                CloseAllDropdowns();
                _moreFiltersPanel.Toggle();
            },
            new Vector2(200f, 52f));
        moreFiltersBtn.Name = "MoreFiltersButton";
        moreFiltersBtn.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        moreFiltersBtn.GrowVertical = Control.GrowDirection.Begin;
        moreFiltersBtn.OffsetLeft = 20f;
        moreFiltersBtn.OffsetRight = 220f;
        moreFiltersBtn.OffsetTop = -436f;
        moreFiltersBtn.OffsetBottom = -384f;
        _screen.AddChild(moreFiltersBtn);

        // --- Empty-state overlay when 0 runs match current filters ---
        _emptyStateOverlay = BuildEmptyStateOverlay(boldFont, regularFont);
        _screen.AddChild(_emptyStateOverlay);

        // Add modal last so it sits above everything else
        _screen.AddChild(_moreFiltersPanel.Root);

        PopulateStaticDropdowns();
    }

    private NativeDropdown? CreateDropdown(
        string name,
        float width,
        float height,
        float itemHeight,
        float maxListHeight,
        int fontSize,
        int itemFontSize,
        bool useRichText = false)
    {
        NativeDropdown? dropdown = NativeDropdown.Create(name, width, height, itemHeight, maxListHeight, fontSize, itemFontSize, useRichText);
        if (dropdown != null)
        {
            dropdown.Opened += OnDropdownOpened;
            _dropdowns.Add(dropdown);
        }
        return dropdown;
    }

    private void OnDropdownOpened(NativeDropdown opened)
    {
        foreach (NativeDropdown dd in _dropdowns)
        {
            if (dd != opened && dd.IsOpen)
            {
                dd.Close();
            }
        }
    }

    public void CloseAllDropdowns()
    {
        foreach (NativeDropdown dd in _dropdowns)
        {
            if (dd.IsOpen)
            {
                dd.Close();
            }
        }
    }

    public bool HasOpenDropdown()
    {
        foreach (NativeDropdown dd in _dropdowns)
        {
            if (dd.IsOpen)
            {
                return true;
            }
        }
        return false;
    }

    public bool HandleKeyInput(InputEvent inputEvent)
    {
        if (inputEvent is not InputEventKey { Pressed: true } keyEvent)
        {
            return false;
        }

        if (!_screen.IsVisibleInTree() || NDevConsole.IsConsoleVisible || !ActiveScreenContext.Instance.IsCurrent(_screen))
        {
            return false;
        }

        Control? focusOwner = _screen.GetViewport()?.GuiGetFocusOwner();
        if (focusOwner is TextEdit or LineEdit)
        {
            return false;
        }

        if (keyEvent.Keycode == Key.Escape)
        {
            if (_moreFiltersPanel is { Visible: true })
            {
                _moreFiltersPanel.Hide();
                _screen.GetViewport()?.SetInputAsHandled();
                return true;
            }

            if (HasOpenDropdown())
            {
                CloseAllDropdowns();
                _screen.GetViewport()?.SetInputAsHandled();
                return true;
            }

            return false;
        }

        if (keyEvent.Echo || keyEvent.CtrlPressed || keyEvent.AltPressed || keyEvent.MetaPressed)
        {
            return false;
        }

        if (_moreFiltersPanel is { Visible: true } || HasOpenDropdown())
        {
            return false;
        }

        if (NControllerManager.Instance?.IsUsingDirectionalNavigation == true)
        {
            return false;
        }

        if (keyEvent.Keycode == Key.Left)
        {
            NRunHistoryArrowButton? prev = PrevButtonRef(_screen);
            if (prev != null && prev.IsEnabled && IndexRef(_screen) < RunNamesRef(_screen).Count - 1)
            {
                LeftArrowReleasedDelegate(_screen, prev);
                _screen.GetViewport()?.SetInputAsHandled();
                return true;
            }
        }
        else if (keyEvent.Keycode == Key.Right)
        {
            NRunHistoryArrowButton? next = NextButtonRef(_screen);
            if (next != null && next.IsEnabled && IndexRef(_screen) > 0)
            {
                RightArrowReleasedDelegate(_screen, next);
                _screen.GetViewport()?.SetInputAsHandled();
                return true;
            }
        }

        return false;
    }

    public void OnSubmenuHidden()
    {
        _loadGeneration++;
        _isIndexing = false;
        _pendingApplyAfterIndex = false;
        _pendingRefreshRunSelectorAfterIndex = false;
        CloseAllDropdowns();
        _moreFiltersPanel?.Hide();
    }

    private void PopulateStaticDropdowns()
    {
        var outcomes = new List<(string, OutcomeFilter)>
        {
            (RunFilterEngine.GetOutcomeLabel(OutcomeFilter.All), OutcomeFilter.All),
            (RunFilterEngine.GetOutcomeLabel(OutcomeFilter.Wins), OutcomeFilter.Wins),
            (RunFilterEngine.GetOutcomeLabel(OutcomeFilter.Losses), OutcomeFilter.Losses)
        };
        _outcomeDropdown?.SetItems(outcomes, value =>
        {
            _state.Outcome = value;
            _outcomeDropdown.SetSelectedText(RunFilterEngine.GetOutcomeLabel(value));
            RequestFilterOrSortUpdate();
        });
        _outcomeDropdown?.SetSelectedText(RunFilterEngine.GetOutcomeLabel(_state.Outcome));

        var gameTypes = new List<(string, GameTypeFilter)>
        {
            (RunFilterEngine.GetGameTypeLabel(GameTypeFilter.Any), GameTypeFilter.Any),
            (RunFilterEngine.GetGameTypeLabel(GameTypeFilter.Standard), GameTypeFilter.Standard),
            (RunFilterEngine.GetGameTypeLabel(GameTypeFilter.Daily), GameTypeFilter.Daily),
            (RunFilterEngine.GetGameTypeLabel(GameTypeFilter.Blitz), GameTypeFilter.Blitz),
            (RunFilterEngine.GetGameTypeLabel(GameTypeFilter.Dread), GameTypeFilter.Dread),
            (RunFilterEngine.GetGameTypeLabel(GameTypeFilter.Custom), GameTypeFilter.Custom)
        };
        _gameTypeDropdown?.SetItems(gameTypes, value =>
        {
            _state.GameType = value;
            _gameTypeDropdown.SetSelectedText(RunFilterEngine.GetGameTypeLabel(value));
            RequestFilterOrSortUpdate();
        });
        _gameTypeDropdown?.SetSelectedText(RunFilterEngine.GetGameTypeLabel(_state.GameType));

        var sortFields = new List<(string, SortField)>
        {
            (RunFilterEngine.GetSortFieldLabel(SortField.Date), SortField.Date),
            (RunFilterEngine.GetSortFieldLabel(SortField.BadgeCount), SortField.BadgeCount),
            (RunFilterEngine.GetSortFieldLabel(SortField.RunTime), SortField.RunTime),
            (RunFilterEngine.GetSortFieldLabel(SortField.RelicCount), SortField.RelicCount),
            (RunFilterEngine.GetSortFieldLabel(SortField.CardCount), SortField.CardCount)
        };
        _sortFieldDropdown?.SetItems(sortFields, value =>
        {
            _state.SortBy = value;
            _sortFieldDropdown.SetSelectedText(RunFilterEngine.GetSortFieldLabel(value));
            RequestFilterOrSortUpdate();
        });
        _sortFieldDropdown?.SetSelectedText(RunFilterEngine.GetSortFieldLabel(_state.SortBy));

        var sortDirections = new List<(string, SortDirection)>
        {
            (RunFilterEngine.GetSortDirectionLabel(SortDirection.Descending), SortDirection.Descending),
            (RunFilterEngine.GetSortDirectionLabel(SortDirection.Ascending), SortDirection.Ascending)
        };
        _sortDirectionDropdown?.SetItems(sortDirections, value =>
        {
            _state.SortDirection = value;
            _sortDirectionDropdown.SetSelectedText(RunFilterEngine.GetSortDirectionLabel(value));
            RequestFilterOrSortUpdate();
        });
        _sortDirectionDropdown?.SetSelectedText(RunFilterEngine.GetSortDirectionLabel(_state.SortDirection));
    }

    /// <summary>
    /// Called after vanilla NRunHistory.OnSubmenuOpened(). Resets to default options so no
    /// filtering/sorting or extra disk I/O runs on initial screen open.
    /// </summary>
    public void OnSubmenuOpened()
    {
        _loadGeneration++;
        _indexReady = false;
        _isIndexing = false;
        _pendingApplyAfterIndex = false;
        _pendingRefreshRunSelectorAfterIndex = false;
        _allRuns.Clear();
        _filteredRuns.Clear();

        _state.ResetAll();
        _outcomeDropdown?.SetSelectedText(RunFilterEngine.GetOutcomeLabel(_state.Outcome));
        _gameTypeDropdown?.SetSelectedText(RunFilterEngine.GetGameTypeLabel(_state.GameType));
        _sortFieldDropdown?.SetSelectedText(RunFilterEngine.GetSortFieldLabel(_state.SortBy));
        _sortDirectionDropdown?.SetSelectedText(RunFilterEngine.GetSortDirectionLabel(_state.SortDirection));

        HideEmptyState();
        PopulateCharacterDropdown();
        _runSelectorDropdown?.InvalidateLazyItems();

        int totalRuns = RunNamesRef(_screen).Count;
        if (_matchCountLabel != null)
        {
            _matchCountLabel.Text = $"{totalRuns} runs";
        }

        // Vanilla just loaded index 0 into _history; update the Run Selector label with zero extra I/O
        SyncSelectedRunLabel(IndexRef(_screen));
    }

    private void RequestFilterOrSortUpdate()
    {
        if (_indexReady)
        {
            ApplyFiltersAndRefresh();
            return;
        }

        _pendingApplyAfterIndex = true;
        StartBackgroundIndexing();
    }

    private void StartBackgroundIndexing()
    {
        if (_isIndexing || _indexReady)
        {
            return;
        }

        int generation = ++_loadGeneration;
        int profileId = SaveManager.Instance.CurrentProfileId;
        string cachePrefix = $"{profileId}:";

        List<string> allNames = SaveManager.Instance.GetAllRunHistoryNames();
        var missingNames = new List<string>();
        for (int i = 0; i < allNames.Count; i++)
        {
            if (!GlobalSummaryCache.ContainsKey(cachePrefix + allNames[i]))
            {
                missingNames.Add(allNames[i]);
            }
        }

        // Everything already cached from earlier in this session -> instant (< 1 ms)
        if (missingNames.Count == 0)
        {
            CompleteIndexing(allNames, cachePrefix);
            return;
        }

        _isIndexing = true;
        if (_matchCountLabel != null)
        {
            _matchCountLabel.Text = $"Loading {allNames.Count} runs...";
        }

        Dictionary<string, string> charNames = SnapshotCharacterDisplayNames();
        ulong localPlayerId = TryGetLocalPlayerId();
        string? historyDir = TryResolveGlobalHistoryDir();

        Task.Run(() =>
        {
            var fallbackNames = new ConcurrentBag<string>();

            Parallel.ForEach(missingNames, fileName =>
            {
                string cacheKey = cachePrefix + fileName;
                if (GlobalSummaryCache.ContainsKey(cacheKey))
                {
                    return;
                }

                if (!string.IsNullOrEmpty(historyDir))
                {
                    string fullPath = Path.Combine(historyDir, fileName);
                    if (File.Exists(fullPath))
                    {
                        try
                        {
                            byte[] bytes = File.ReadAllBytes(fullPath);
                            RunSummary summary = FastRunSummaryParser.Parse(fileName, bytes, localPlayerId, charNames);
                            GlobalSummaryCache[cacheKey] = summary;
                            return;
                        }
                        catch
                        {
                            // Fall through to SaveManager fallback
                        }
                    }
                }

                fallbackNames.Add(fileName);
            });

            Callable.From(() =>
            {
                if (generation != _loadGeneration || !GodotObject.IsInstanceValid(_screen))
                {
                    return;
                }

                foreach (string fileName in fallbackNames)
                {
                    string cacheKey = cachePrefix + fileName;
                    if (!GlobalSummaryCache.ContainsKey(cacheKey))
                    {
                        GlobalSummaryCache[cacheKey] = LoadRunSummaryViaSaveManager(fileName);
                    }
                }

                _isIndexing = false;
                CompleteIndexing(allNames, cachePrefix);
            }).CallDeferred();
        });
    }

    private void CompleteIndexing(IReadOnlyList<string> allNames, string cachePrefix)
    {
        _allRuns.Clear();
        for (int i = 0; i < allNames.Count; i++)
        {
            if (GlobalSummaryCache.TryGetValue(cachePrefix + allNames[i], out RunSummary? summary))
            {
                _allRuns.Add(summary);
            }
        }

        _indexReady = true;
        PopulateCharacterDropdown();

        bool shouldApply = _pendingApplyAfterIndex;
        bool shouldRefreshSelector = _pendingRefreshRunSelectorAfterIndex;
        _pendingApplyAfterIndex = false;
        _pendingRefreshRunSelectorAfterIndex = false;

        if (shouldApply || !_state.IsDefault)
        {
            ApplyFiltersAndRefresh();
        }
        else
        {
            // Keep vanilla order (Date Descending) populated into _filteredRuns for the Run Selector
            _filteredRuns.Clear();
            _filteredRuns.AddRange(RunFilterEngine.Apply(_allRuns, _state));
            UpdateMatchCountLabel();
            _runSelectorDropdown?.InvalidateLazyItems();
            SyncSelectedRunLabel(IndexRef(_screen));
        }

        if (shouldRefreshSelector && _runSelectorDropdown != null && _runSelectorDropdown.IsOpen)
        {
            _runSelectorDropdown.InvalidateLazyItems();
        }
    }

    private void PopulateCharacterDropdown()
    {
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new List<(string Label, string? Value)>
        {
            ("All Characters", null)
        };

        try
        {
            foreach (CharacterModel character in ModelDb.AllCharacters)
            {
                if (!character.IsPlayable)
                {
                    continue;
                }

                string id = character.Id.Entry;
                if (seenIds.Add(id))
                {
                    options.Add((GetCharacterTitle(character), id));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[RunHistoryPlus] Error reading ModelDb.AllCharacters: {ex.Message}");
        }

        foreach (RunSummary run in _allRuns)
        {
            if (!run.LoadSucceeded)
            {
                continue;
            }

            foreach (string charId in run.PlayerCharacterIds)
            {
                if (!string.IsNullOrEmpty(charId) && seenIds.Add(charId))
                {
                    options.Add((FormatFallbackCharacterName(charId), charId));
                }
            }
        }

        if (!string.IsNullOrEmpty(_state.CharacterId) && !seenIds.Contains(_state.CharacterId))
        {
            _state.CharacterId = null;
        }

        string selectedLabel = options.FirstOrDefault(o =>
            string.Equals(o.Value, _state.CharacterId, StringComparison.OrdinalIgnoreCase)).Label ?? "All Characters";

        _characterDropdown?.SetItems(options, charId =>
        {
            _state.CharacterId = charId;
            string label = options.FirstOrDefault(o =>
                string.Equals(o.Value, charId, StringComparison.OrdinalIgnoreCase)).Label ?? "All Characters";
            _characterDropdown.SetSelectedText(label);
            RequestFilterOrSortUpdate();
        });
        _characterDropdown?.SetSelectedText(selectedLabel);
    }

    private void ResetFiltersAndRefresh()
    {
        _state.ResetFilters();
        _outcomeDropdown?.SetSelectedText(RunFilterEngine.GetOutcomeLabel(_state.Outcome));
        _characterDropdown?.SetSelectedText("All Characters");
        _gameTypeDropdown?.SetSelectedText(RunFilterEngine.GetGameTypeLabel(_state.GameType));
        RequestFilterOrSortUpdate();
    }

    public void ApplyFiltersAndRefresh()
    {
        if (!_indexReady)
        {
            return;
        }

        List<string> runNames = RunNamesRef(_screen);
        int currentIndex = IndexRef(_screen);
        string? previousFileName = (currentIndex >= 0 && currentIndex < runNames.Count)
            ? runNames[currentIndex]
            : null;

        _filteredRuns.Clear();
        _filteredRuns.AddRange(RunFilterEngine.Apply(_allRuns, _state));

        runNames.Clear();
        foreach (RunSummary run in _filteredRuns)
        {
            runNames.Add(run.FileName);
        }

        UpdateMatchCountLabel();
        _runSelectorDropdown?.InvalidateLazyItems();

        if (_filteredRuns.Count == 0)
        {
            ShowEmptyState();
            _runSelectorDropdown?.SetSelectedText("No matching runs");
            return;
        }

        HideEmptyState();

        // Always jump to index 0 (the first/most-recent run under the updated filter & sort)
        const int targetIndex = 0;
        bool sameRunDisplayed = !string.IsNullOrEmpty(previousFileName) &&
            string.Equals(_filteredRuns[targetIndex].FileName, previousFileName, StringComparison.Ordinal);

        if (sameRunDisplayed)
        {
            IndexRef(_screen) = targetIndex;
            UpdateArrowButtons(targetIndex, _filteredRuns.Count);
            SyncSelectedRunLabel(targetIndex);
        }
        else
        {
            TaskHelper.RunSafely(RefreshAndSelectRunDelegate(_screen, targetIndex));
            SyncSelectedRunLabel(targetIndex);
        }
    }

    private void UpdateArrowButtons(int index, int total)
    {
        NRunHistoryArrowButton? prev = PrevButtonRef(_screen);
        if (prev != null)
        {
            bool hasPrev = index < total - 1;
            prev.SetEnabled(hasPrev);
            prev.Visible = hasPrev;
        }

        NRunHistoryArrowButton? next = NextButtonRef(_screen);
        if (next != null)
        {
            bool hasNext = index > 0;
            next.SetEnabled(hasNext);
            next.Visible = hasNext;
        }
    }

    private void ConfigureLazyRunSelectorDropdown()
    {
        _runSelectorDropdown?.SetLazyItems(() =>
        {
            if (!_indexReady)
            {
                _pendingRefreshRunSelectorAfterIndex = true;
                StartBackgroundIndexing();

                if (!_indexReady)
                {
                    return new List<(string Label, Action OnSelected)>
                    {
                        ("Loading runs...", () => { })
                    };
                }
            }

            if (_filteredRuns.Count == 0)
            {
                return Array.Empty<(string Label, Action OnSelected)>();
            }

            ResizeRunSelectorToFit(_filteredRuns);

            var items = new List<(string Label, Action OnSelected)>(_filteredRuns.Count);
            for (int i = 0; i < _filteredRuns.Count; i++)
            {
                int capturedIndex = i;
                string label = RunFilterEngine.FormatRunLabelBbcode(_filteredRuns[i]);
                items.Add((label, () =>
                {
                    if (capturedIndex >= 0 && capturedIndex < _filteredRuns.Count)
                    {
                        TaskHelper.RunSafely(RefreshAndSelectRunDelegate(_screen, capturedIndex));
                        SyncSelectedRunLabel(capturedIndex);
                    }
                }));
            }
            return items;
        });
    }

    private void ResizeRunSelectorToFit(IReadOnlyList<RunSummary> runs)
    {
        if (_runSelectorDropdown == null || _runSelectorHolder == null || runs.Count == 0)
        {
            return;
        }

        float maxTextWidth = 0f;
        for (int i = 0; i < runs.Count; i++)
        {
            string plain = RunFilterEngine.FormatRunLabel(runs[i]);
            float w = MeasureTextWidth(plain, 20);
            if (w > maxTextWidth)
            {
                maxTextWidth = w;
            }
        }

        // Left padding (10) + right arrow/scrollbar clearance (40) + small breathing room (12)
        float targetWidth = Mathf.Clamp(Mathf.Ceil(maxTextWidth + 62f), 320f, 620f);
        ApplyRunSelectorWidth(targetWidth);
    }

    private void ResizeRunSelectorToFitText(string plainText)
    {
        if (_runSelectorDropdown == null || _runSelectorHolder == null)
        {
            return;
        }

        float textWidth = MeasureTextWidth(plainText, 20);
        float targetWidth = Mathf.Clamp(Mathf.Ceil(textWidth + 62f), 320f, 620f);
        ApplyRunSelectorWidth(targetWidth);
    }

    private void ApplyRunSelectorWidth(float width)
    {
        if (_runSelectorDropdown == null || _runSelectorHolder == null)
        {
            return;
        }

        _runSelectorHolder.OffsetLeft = -width * 0.5f;
        _runSelectorHolder.OffsetRight = width * 0.5f;
        PlaceChild(_runSelectorDropdown.Root, 0f, 0f, width, 42f);
        _runSelectorDropdown.SetWidth(width);
    }

    private float MeasureTextWidth(string text, int fontSize)
    {
        if (_boldFont != null)
        {
            return _boldFont.GetStringSize(text, HorizontalAlignment.Left, -1f, fontSize).X;
        }

        return text.Length * (fontSize * 0.56f);
    }

    public void SyncSelectedRunLabel(int index)
    {
        if (_runSelectorDropdown == null)
        {
            return;
        }

        if (_indexReady)
        {
            if (_filteredRuns.Count == 0 || index < 0 || index >= _filteredRuns.Count)
            {
                _runSelectorDropdown.SetSelectedText("No matching runs");
                return;
            }

            ResizeRunSelectorToFit(_filteredRuns);
            _runSelectorDropdown.SetSelectedText(RunFilterEngine.FormatRunLabelBbcode(_filteredRuns[index]));
            return;
        }

        // Unindexed fast path: format directly from the currently loaded _history with zero extra disk I/O
        List<string> runNames = RunNamesRef(_screen);
        RunHistory? history = HistoryRef(_screen);
        if (history != null && index >= 0 && index < runNames.Count)
        {
            string fileName = runNames[index];
            RunSummary summary = BuildSummaryFromLoadedHistory(fileName, history);
            string cacheKey = $"{SaveManager.Instance.CurrentProfileId}:{fileName}";
            GlobalSummaryCache[cacheKey] = summary;
            ResizeRunSelectorToFitText(RunFilterEngine.FormatRunLabel(summary));
            _runSelectorDropdown.SetSelectedText(RunFilterEngine.FormatRunLabelBbcode(summary));
        }
        else if (runNames.Count == 0)
        {
            _runSelectorDropdown.SetSelectedText("No matching runs");
        }
    }

    public void ShowEmptyState()
    {
        NScrollableContainer? screenContents = ScreenContentsRef(_screen);
        if (screenContents != null) screenContents.Visible = false;

        NRunHistoryArrowButton? prev = PrevButtonRef(_screen);
        if (prev != null)
        {
            prev.Disable();
            prev.Visible = false;
        }

        NRunHistoryArrowButton? next = NextButtonRef(_screen);
        if (next != null)
        {
            next.Disable();
            next.Visible = false;
        }

        if (_emptyStateOverlay != null)
        {
            _emptyStateOverlay.Visible = true;
        }
    }

    private void HideEmptyState()
    {
        NScrollableContainer? screenContents = ScreenContentsRef(_screen);
        if (screenContents != null) screenContents.Visible = true;

        if (_emptyStateOverlay != null)
        {
            _emptyStateOverlay.Visible = false;
        }
    }

    private void UpdateMatchCountLabel()
    {
        if (_matchCountLabel == null)
        {
            return;
        }

        _matchCountLabel.Text = _filteredRuns.Count == _allRuns.Count
            ? $"{_allRuns.Count} runs"
            : $"Showing {_filteredRuns.Count} / {_allRuns.Count}";
    }

    private Control BuildEmptyStateOverlay(Font? boldFont, Font? regularFont)
    {
        CenterContainer container = new CenterContainer
        {
            Name = "RunHistoryPlusEmptyState",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        container.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        container.OffsetLeft = 250f;
        container.OffsetRight = -250f;

        VBoxContainer vbox = new VBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        vbox.AddThemeConstantOverride("separation", 18);
        container.AddChild(vbox);

        Label headline = new Label
        {
            Text = "No runs match the current filters.",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        if (boldFont != null) headline.AddThemeFontOverride("font", boldFont);
        headline.AddThemeFontSizeOverride("font_size", 32);
        headline.AddThemeColorOverride("font_color", CreamColor);
        headline.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.5f));
        headline.AddThemeConstantOverride("shadow_offset_x", 3);
        headline.AddThemeConstantOverride("shadow_offset_y", 2);
        vbox.AddChild(headline);

        Label hint = new Label
        {
            Text = "Adjust your filters on the left or reset them below.",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        if (regularFont != null) hint.AddThemeFontOverride("font", regularFont);
        hint.AddThemeFontSizeOverride("font_size", 20);
        hint.AddThemeColorOverride("font_color", MutedColor);
        vbox.AddChild(hint);

        HBoxContainer btnRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        btnRow.AddChild(MoreFiltersPanel.CreateStyledButton("Reset Filters", boldFont, ResetFiltersAndRefresh));
        vbox.AddChild(btnRow);

        return container;
    }

    private static void PlaceChild(Control control, float x, float y, float width, float height)
    {
        control.AnchorLeft = 0f;
        control.AnchorTop = 0f;
        control.AnchorRight = 0f;
        control.AnchorBottom = 0f;
        control.OffsetLeft = x;
        control.OffsetTop = y;
        control.OffsetRight = x + width;
        control.OffsetBottom = y + height;
    }

    private static Label CreateSectionLabel(string text, Font? font)
    {
        Label label = new Label
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        if (font != null) label.AddThemeFontOverride("font", font);
        label.AddThemeFontSizeOverride("font_size", 18);
        label.AddThemeColorOverride("font_color", GoldColor);
        label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.5f));
        label.AddThemeConstantOverride("shadow_offset_x", 2);
        label.AddThemeConstantOverride("shadow_offset_y", 1);
        return label;
    }

    private static Dictionary<string, string> SnapshotCharacterDisplayNames()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (CharacterModel character in ModelDb.AllCharacters)
            {
                map[character.Id.Entry] = GetCharacterTitle(character);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[RunHistoryPlus] Error snapshotting character titles: {ex.Message}");
        }
        return map;
    }

    private static ulong TryGetLocalPlayerId()
    {
        try
        {
            return PlatformUtil.GetLocalPlayerId(PlatformUtil.PrimaryPlatform);
        }
        catch
        {
            return 0UL;
        }
    }

    private static string? TryResolveGlobalHistoryDir()
    {
        try
        {
            string scopedUserPath = SaveManager.Instance.GetProfileScopedPath(
                Path.Combine(UserDataPathProvider.SavesDir, "history"));
            string globalPath = ProjectSettings.GlobalizePath(scopedUserPath);
            if (Directory.Exists(globalPath))
            {
                return globalPath;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[RunHistoryPlus] Could not resolve global history dir: {ex.Message}");
        }

        return null;
    }

    private static RunSummary LoadRunSummaryViaSaveManager(string fileName)
    {
        try
        {
            ReadSaveResult<RunHistory> result = SaveManager.Instance.LoadRunHistory(fileName);
            if (!result.Success || result.SaveData == null)
            {
                return FastRunSummaryParser.Unreadable(fileName);
            }

            return BuildSummaryFromLoadedHistory(fileName, result.SaveData);
        }
        catch (Exception ex)
        {
            Log.Warn($"[RunHistoryPlus] Failed to summarize run '{fileName}': {ex.Message}");
            return FastRunSummaryParser.Unreadable(fileName);
        }
    }

    private static RunSummary BuildSummaryFromLoadedHistory(string fileName, RunHistory history)
    {
        int floorCount = history.MapPointHistory.Sum(act => act.Count);
        ulong localPlayerId = PlatformUtil.GetLocalPlayerId(history.PlatformType);

        RunHistoryPlayer? primaryPlayer = history.Players.FirstOrDefault(p => p.Id == localPlayerId)
            ?? history.Players.FirstOrDefault();

        string primaryCharId = primaryPlayer?.Character.Entry ?? string.Empty;
        string primaryCharName = ResolveCharacterName(primaryPlayer?.Character);
        int badgeCount = primaryPlayer?.Badges.Count() ?? 0;
        int relicCount = primaryPlayer?.Relics.Count() ?? 0;
        int cardCount = primaryPlayer?.Deck.Count() ?? 0;
        List<string> playerCharIds = history.Players
            .Select(p => p.Character.Entry)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new RunSummary(
            fileName,
            history.StartTime,
            history.RunTime,
            history.Ascension,
            floorCount,
            badgeCount,
            relicCount,
            cardCount,
            history.Win,
            history.WasAbandoned,
            ResolveGameType(history),
            primaryCharId,
            primaryCharName,
            playerCharIds,
            history.Players.Count,
            history.Seed,
            true);
    }

    private static RunGameType ResolveGameType(RunHistory history)
    {
        if (history.IsCustomGame)
        {
            return RunGameType.Custom;
        }

        return history.GameMode switch
        {
            GameMode.Standard => RunGameType.Standard,
            GameMode.Daily => RunGameType.Daily,
            GameMode.Blitz => RunGameType.Blitz,
            GameMode.Dread => RunGameType.Dread,
            _ => RunGameType.Unknown
        };
    }

    private static string ResolveCharacterName(ModelId? charId)
    {
        if (charId == null || charId == ModelId.none)
        {
            return "Unknown";
        }

        try
        {
            CharacterModel model = SaveUtil.CharacterOrDeprecated(charId);
            return GetCharacterTitle(model);
        }
        catch
        {
            return FormatFallbackCharacterName(charId.Entry);
        }
    }

    private static string GetCharacterTitle(CharacterModel character)
    {
        try
        {
            string title = character.Title.GetFormattedText();
            if (!string.IsNullOrWhiteSpace(title))
            {
                return title;
            }
        }
        catch
        {
            // Fall back to entry name
        }

        return FormatFallbackCharacterName(character.Id.Entry);
    }

    private static string FormatFallbackCharacterName(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
        {
            return "Unknown";
        }

        return string.Join(" ", entry
            .Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
    }
}
