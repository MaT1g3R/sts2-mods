using System.Globalization;
using RunHistoryPlus.Core;
using Xunit;

namespace RunHistoryPlus.Tests;

public class RunFilterEngineTests
{
    private static RunSummary MakeRun(
        string fileName,
        long startTime = 1700000000,
        float runTime = 1200f,
        int ascension = 0,
        int floorCount = 30,
        int badgeCount = 2,
        int relicCount = 8,
        int cardCount = 20,
        bool win = false,
        bool wasAbandoned = false,
        RunGameType gameType = RunGameType.Standard,
        string charId = "IRONCLAD",
        string charName = "The Ironclad",
        IReadOnlyList<string>? playerChars = null,
        IReadOnlyCollection<string>? playerRelics = null,
        int playerCount = 1,
        bool loadSucceeded = true)
    {
        return new RunSummary(
            fileName,
            startTime,
            runTime,
            ascension,
            floorCount,
            badgeCount,
            relicCount,
            cardCount,
            win,
            wasAbandoned,
            gameType,
            charId,
            charName,
            playerChars ?? new[] { charId },
            playerRelics ?? Array.Empty<string>(),
            playerCount,
            "SEED123",
            loadSucceeded);
    }

    [Fact]
    public void Filters_ByOutcome_AllWinsAndLossesIncludingAbandoned()
    {
        var runs = new[]
        {
            MakeRun("win.run", win: true),
            MakeRun("loss.run", win: false, wasAbandoned: false),
            MakeRun("abandoned.run", win: false, wasAbandoned: true)
        };

        var state = new RunFilterState { Outcome = OutcomeFilter.All };
        Assert.Equal(3, RunFilterEngine.Apply(runs, state).Count);

        state.Outcome = OutcomeFilter.Wins;
        Assert.Equal(new[] { "win.run" }, RunFilterEngine.Apply(runs, state).Select(r => r.FileName));

        state.Outcome = OutcomeFilter.Losses;
        var losses = RunFilterEngine.Apply(runs, state).Select(r => r.FileName).ToHashSet();
        Assert.Equal(2, losses.Count);
        Assert.Contains("loss.run", losses);
        Assert.Contains("abandoned.run", losses);
    }

    [Theory]
    [InlineData(GameTypeFilter.Standard, "standard.run")]
    [InlineData(GameTypeFilter.Daily, "daily.run")]
    [InlineData(GameTypeFilter.Blitz, "blitz.run")]
    [InlineData(GameTypeFilter.Dread, "dread.run")]
    [InlineData(GameTypeFilter.Custom, "custom.run")]
    public void Filters_ByAllFiveGameTypes(GameTypeFilter filter, string expectedFile)
    {
        var runs = new[]
        {
            MakeRun("standard.run", gameType: RunGameType.Standard),
            MakeRun("daily.run", gameType: RunGameType.Daily),
            MakeRun("blitz.run", gameType: RunGameType.Blitz),
            MakeRun("dread.run", gameType: RunGameType.Dread),
            MakeRun("custom.run", gameType: RunGameType.Custom)
        };

        var state = new RunFilterState { GameType = filter };
        var result = RunFilterEngine.Apply(runs, state);

        Assert.Single(result);
        Assert.Equal(expectedFile, result[0].FileName);
    }

    [Fact]
    public void Filters_ByCharacter_MatchesOnlyLocalPlayerInMultiplayer()
    {
        var runs = new[]
        {
            MakeRun("solo_ironclad.run", charId: "IRONCLAD", charName: "The Ironclad"),
            MakeRun("solo_defect.run", charId: "DEFECT", charName: "The Defect"),
            MakeRun("coop_local_silent.run", charId: "SILENT", charName: "The Silent", playerChars: new[] { "SILENT", "DEFECT" }, playerCount: 2)
        };

        var state = new RunFilterState { CharacterId = "defect" };
        var result = RunFilterEngine.Apply(runs, state).Select(r => r.FileName).ToList();

        Assert.Equal(new[] { "solo_defect.run" }, result);
    }

    [Fact]
    public void Sorts_ByDateBadgeCountRunTimeRelicCountAndCardCount_BothDirections()
    {
        var runs = new[]
        {
            MakeRun("a.run", startTime: 100, badgeCount: 1, runTime: 500f, relicCount: 12, cardCount: 15),
            MakeRun("b.run", startTime: 300, badgeCount: 5, runTime: 200f, relicCount: 5, cardCount: 35),
            MakeRun("c.run", startTime: 200, badgeCount: 3, runTime: 900f, relicCount: 8, cardCount: 24)
        };

        // Date Descending (default)
        var state = new RunFilterState { SortBy = SortField.Date, SortDirection = SortDirection.Descending };
        Assert.Equal(new[] { "b.run", "c.run", "a.run" }, RunFilterEngine.Apply(runs, state).Select(r => r.FileName));

        // Date Ascending
        state.SortDirection = SortDirection.Ascending;
        Assert.Equal(new[] { "a.run", "c.run", "b.run" }, RunFilterEngine.Apply(runs, state).Select(r => r.FileName));

        // BadgeCount Descending
        state.SortBy = SortField.BadgeCount;
        state.SortDirection = SortDirection.Descending;
        Assert.Equal(new[] { "b.run", "c.run", "a.run" }, RunFilterEngine.Apply(runs, state).Select(r => r.FileName));

        // RunTime Descending
        state.SortBy = SortField.RunTime;
        state.SortDirection = SortDirection.Descending;
        Assert.Equal(new[] { "c.run", "a.run", "b.run" }, RunFilterEngine.Apply(runs, state).Select(r => r.FileName));

        // RelicCount Descending
        state.SortBy = SortField.RelicCount;
        state.SortDirection = SortDirection.Descending;
        Assert.Equal(new[] { "a.run", "c.run", "b.run" }, RunFilterEngine.Apply(runs, state).Select(r => r.FileName));

        // CardCount Ascending
        state.SortBy = SortField.CardCount;
        state.SortDirection = SortDirection.Ascending;
        Assert.Equal(new[] { "a.run", "c.run", "b.run" }, RunFilterEngine.Apply(runs, state).Select(r => r.FileName));
    }

    [Fact]
    public void UnreadableRuns_ShownWhenNoFiltersActive_HiddenWhenFiltered()
    {
        var runs = new[]
        {
            MakeRun("good.run", win: true),
            MakeRun("corrupt.run", loadSucceeded: false)
        };

        var state = new RunFilterState();
        Assert.Equal(2, RunFilterEngine.Apply(runs, state).Count);

        state.Outcome = OutcomeFilter.Wins;
        Assert.Equal(new[] { "good.run" }, RunFilterEngine.Apply(runs, state).Select(r => r.FileName));
    }

    [Fact]
    public void FormatRunLabel_UsesIso24HourDateCharacterModeOrAscensionAndWinLoss()
    {
        var standardRun = MakeRun(
            "standard.run",
            startTime: 1700000000,
            ascension: 10,
            win: true,
            gameType: RunGameType.Standard,
            charName: "The Defect");

        string standardLabel = RunFilterEngine.FormatRunLabel(standardRun);
        string expectedDate = DateTimeOffset.FromUnixTimeSeconds(1700000000).ToLocalTime().DateTime
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        Assert.Equal($"{expectedDate} · The Defect · A10 · Win", standardLabel);
        Assert.Equal($"{expectedDate} · The Defect · A10 · [color=#7AE87A]Win[/color]", RunFilterEngine.FormatRunLabelBbcode(standardRun));

        var dreadAbandonedRun = MakeRun(
            "dread.run",
            startTime: 1700000000,
            ascension: 0,
            win: false,
            wasAbandoned: true,
            gameType: RunGameType.Dread,
            charName: "The Ironclad");

        string dreadLabel = RunFilterEngine.FormatRunLabel(dreadAbandonedRun);
        Assert.Equal($"{expectedDate} · The Ironclad · Dread · Loss", dreadLabel);
        Assert.Equal($"{expectedDate} · The Ironclad · Dread · [color=#FF6464]Loss[/color]", RunFilterEngine.FormatRunLabelBbcode(dreadAbandonedRun));
    }

    [Fact]
    public void FastParser_ExtractsBadgeRelicCardAndFloorCounts()
    {
        const string json = """
        {
          "start_time": 1700000000,
          "run_time": 1450.5,
          "ascension": 10,
          "win": true,
          "was_abandoned": false,
          "game_mode": "standard",
          "is_custom_game": false,
          "seed": "ABC123",
          "players": [
            {
              "id": 42,
              "character": "CHARACTER.DEFECT",
              "badges": [{}, {}, {}, {}],
              "relics": [{ "id": "RELIC.CRACKED_CORE" }, { "id": "RELIC.ANCHOR" }, { "id": "BAG_OF_PREP" }],
              "deck": [{}, {}, {}, {}, {}]
            }
          ],
          "map_point_history": [
            [{}, {}, {}],
            [{}, {}]
          ]
        }
        """;

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DEFECT"] = "The Defect"
        };

        RunSummary summary = FastRunSummaryParser.Parse("1700000000.run", json, 42, names);

        Assert.True(summary.LoadSucceeded);
        Assert.Equal("The Defect", summary.PrimaryCharacterName);
        Assert.Equal(RunGameType.Standard, summary.GameType);
        Assert.Equal(5, summary.FloorCount);
        Assert.Equal(4, summary.BadgeCount);
        Assert.Equal(3, summary.RelicCount);
        Assert.Equal(5, summary.CardCount);
        Assert.Contains("CRACKED_CORE", summary.PlayerRelicIds);
        Assert.Contains("ANCHOR", summary.PlayerRelicIds);
        Assert.Contains("BAG_OF_PREP", summary.PlayerRelicIds);
    }

    [Fact]
    public void FastParser_Multiplayer_UsesOnlyLocalPlayersCharacterAndRelics()
    {
        const string json = """
        {
          "start_time": 1700000002,
          "run_time": 1200,
          "ascension": 5,
          "win": true,
          "game_mode": "standard",
          "players": [
            {
              "id": 99,
              "character": "CHARACTER.IRONCLAD",
              "badges": [{}],
              "relics": [{ "id": "RELIC.BURNING_BLOOD" }, { "id": "RELIC.LANTERN" }],
              "deck": [{}, {}]
            },
            {
              "id": 42,
              "character": "CHARACTER.DEFECT",
              "badges": [{}, {}],
              "relics": [{ "id": "RELIC.CRACKED_CORE" }, { "id": "RELIC.ANCHOR" }],
              "deck": [{}, {}, {}]
            }
          ],
          "map_point_history": [[{}]]
        }
        """;

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["IRONCLAD"] = "The Ironclad",
            ["DEFECT"] = "The Defect"
        };

        RunSummary summary = FastRunSummaryParser.Parse("coop.run", json, 42, names);

        Assert.Equal("DEFECT", summary.PrimaryCharacterId);
        Assert.Equal("The Defect", summary.PrimaryCharacterName);
        Assert.Equal(2, summary.PlayerCount);
        Assert.Equal(2, summary.BadgeCount);
        Assert.Equal(2, summary.RelicCount);
        Assert.Equal(3, summary.CardCount);
        Assert.Contains("CRACKED_CORE", summary.PlayerRelicIds);
        Assert.Contains("ANCHOR", summary.PlayerRelicIds);
        Assert.DoesNotContain("BURNING_BLOOD", summary.PlayerRelicIds);
        Assert.DoesNotContain("LANTERN", summary.PlayerRelicIds);
    }

    [Fact]
    public void Filters_ByRelics_AllAndAnyModes()
    {
        var runs = new[]
        {
            MakeRun("both.run", playerRelics: new[] { "BURNING_BLOOD", "ANCHOR", "LANTERN" }),
            MakeRun("only_anchor.run", playerRelics: new[] { "ANCHOR" }),
            MakeRun("only_lantern.run", playerRelics: new[] { "LANTERN" }),
            MakeRun("none.run", playerRelics: new[] { "BURNING_BLOOD" })
        };

        var state = new RunFilterState { RelicMatchMode = MultiMatchMode.All };
        state.RequiredRelicIds.Add("anchor");
        state.RequiredRelicIds.Add("lantern");

        Assert.Equal(new[] { "both.run" }, RunFilterEngine.Apply(runs, state).Select(r => r.FileName));

        state.RelicMatchMode = MultiMatchMode.Any;
        var anyResult = RunFilterEngine.Apply(runs, state).Select(r => r.FileName).ToHashSet();
        Assert.Equal(3, anyResult.Count);
        Assert.Contains("both.run", anyResult);
        Assert.Contains("only_anchor.run", anyResult);
        Assert.Contains("only_lantern.run", anyResult);
    }

    [Fact]
    public void ClearMoreFilters_PreservesMainScreenFilters()
    {
        var state = new RunFilterState
        {
            Outcome = OutcomeFilter.Wins,
            CharacterId = "DEFECT",
            GameType = GameTypeFilter.Standard,
            RelicMatchMode = MultiMatchMode.Any,
            MinPlayerCount = 2,
            MaxPlayerCount = 3
        };
        state.RequiredRelicIds.Add("ANCHOR");

        state.ClearMoreFilters();

        Assert.False(state.HasMoreFilters);
        Assert.True(state.HasPlayerCountFilter);
        Assert.Equal(2, state.MinPlayerCount);
        Assert.Equal(3, state.MaxPlayerCount);
        Assert.Equal(MultiMatchMode.All, state.RelicMatchMode);
        Assert.Equal(OutcomeFilter.Wins, state.Outcome);
        Assert.Equal("DEFECT", state.CharacterId);
        Assert.Equal(GameTypeFilter.Standard, state.GameType);

        state.ResetFilters();
        Assert.False(state.HasPlayerCountFilter);
        Assert.Equal(RunFilterState.MinPlayerCountBound, state.MinPlayerCount);
        Assert.Equal(RunFilterState.MaxPlayerCountCap, state.MaxPlayerCount);
    }

    [Fact]
    public void Filters_ByPlayerCount_SupportsExactRangesAndFourPlusCap()
    {
        var runs = new[]
        {
            MakeRun("1p.run", playerCount: 1),
            MakeRun("2p.run", playerCount: 2),
            MakeRun("3p.run", playerCount: 3),
            MakeRun("4p.run", playerCount: 4),
            MakeRun("6p.run", playerCount: 6)
        };

        // Default 1-4+ includes all player counts including > 4
        var state = new RunFilterState();
        Assert.False(state.HasPlayerCountFilter);
        Assert.False(state.HasActiveFilters);
        Assert.Equal(5, RunFilterEngine.Apply(runs, state).Count);

        // Solo (1-1)
        state.MinPlayerCount = 1;
        state.MaxPlayerCount = 1;
        Assert.True(state.HasPlayerCountFilter);
        Assert.True(state.HasActiveFilters);
        Assert.Equal(0, state.ActiveMoreFilterCount);
        Assert.Equal(new[] { "1p.run" }, RunFilterEngine.Apply(runs, state).Select(r => r.FileName));

        // 2-3 players
        state.MinPlayerCount = 2;
        state.MaxPlayerCount = 3;
        Assert.Equal(new[] { "3p.run", "2p.run" }, RunFilterEngine.Apply(runs, state).Select(r => r.FileName));

        // Multiplayer (2-4+) includes 2, 3, 4, and modded 6p
        state.MinPlayerCount = 2;
        state.MaxPlayerCount = 4;
        Assert.Equal(new[] { "6p.run", "4p.run", "3p.run", "2p.run" }, RunFilterEngine.Apply(runs, state).Select(r => r.FileName));

        // 4+ (4-4) includes 4p and modded 6p
        state.MinPlayerCount = 4;
        state.MaxPlayerCount = 4;
        Assert.Equal(new[] { "6p.run", "4p.run" }, RunFilterEngine.Apply(runs, state).Select(r => r.FileName));
    }

    [Theory]
    [InlineData(1, 4, "1–4+ players")]
    [InlineData(1, 1, "1 player")]
    [InlineData(2, 2, "2 players")]
    [InlineData(2, 3, "2–3 players")]
    [InlineData(2, 4, "2–4+ players")]
    [InlineData(4, 4, "4+ players")]
    public void FormatPlayerCountRange_FormatsExpectedLabels(int min, int max, string expected)
    {
        Assert.Equal(expected, RunFilterEngine.FormatPlayerCountRange(min, max));
    }
}
