using System.Globalization;

namespace RunHistoryPlus.Core;

public static class RunFilterEngine
{
    public static bool Matches(RunSummary run, RunFilterState state)
    {
        if (!run.LoadSucceeded)
        {
            return !state.HasActiveFilters;
        }

        if (!MatchesOutcome(run, state.Outcome))
        {
            return false;
        }

        if (!MatchesCharacter(run, state.CharacterId))
        {
            return false;
        }

        if (!MatchesGameType(run, state.GameType))
        {
            return false;
        }

        return true;
    }

    public static bool MatchesOutcome(RunSummary run, OutcomeFilter outcome) => outcome switch
    {
        OutcomeFilter.All => true,
        OutcomeFilter.Wins => run.Win,
        OutcomeFilter.Losses => !run.Win,
        _ => true
    };

    public static bool MatchesCharacter(RunSummary run, string? characterId)
    {
        if (string.IsNullOrEmpty(characterId))
        {
            return true;
        }

        for (int i = 0; i < run.PlayerCharacterIds.Count; i++)
        {
            if (string.Equals(run.PlayerCharacterIds[i], characterId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool MatchesGameType(RunSummary run, GameTypeFilter gameType) => gameType switch
    {
        GameTypeFilter.Any => true,
        GameTypeFilter.Standard => run.GameType == RunGameType.Standard,
        GameTypeFilter.Daily => run.GameType == RunGameType.Daily,
        GameTypeFilter.Blitz => run.GameType == RunGameType.Blitz,
        GameTypeFilter.Dread => run.GameType == RunGameType.Dread,
        GameTypeFilter.Custom => run.GameType == RunGameType.Custom,
        _ => true
    };

    public static List<RunSummary> Apply(IReadOnlyList<RunSummary> runs, RunFilterState state)
    {
        var filtered = new List<RunSummary>(runs.Count);
        for (int i = 0; i < runs.Count; i++)
        {
            if (Matches(runs[i], state))
            {
                filtered.Add(runs[i]);
            }
        }

        filtered.Sort((a, b) => CompareRuns(a, b, state.SortBy, state.SortDirection));
        return filtered;
    }

    public static int CompareRuns(RunSummary a, RunSummary b, SortField sortBy, SortDirection direction)
    {
        int cmp = sortBy switch
        {
            SortField.Date => a.StartTime.CompareTo(b.StartTime),
            SortField.BadgeCount => a.BadgeCount.CompareTo(b.BadgeCount),
            SortField.RunTime => a.RunTime.CompareTo(b.RunTime),
            SortField.RelicCount => a.RelicCount.CompareTo(b.RelicCount),
            SortField.CardCount => a.CardCount.CompareTo(b.CardCount),
            _ => a.StartTime.CompareTo(b.StartTime)
        };

        if (cmp == 0 && sortBy != SortField.Date)
        {
            cmp = a.StartTime.CompareTo(b.StartTime);
        }

        if (cmp == 0)
        {
            cmp = string.Compare(a.FileName, b.FileName, StringComparison.Ordinal);
        }

        return direction == SortDirection.Descending ? -cmp : cmp;
    }

    public static string FormatRunLabel(RunSummary run, CultureInfo? culture = null)
    {
        return FormatRunLabelInternal(run, colorizeOutcome: false);
    }

    public static string FormatRunLabelBbcode(RunSummary run)
    {
        return FormatRunLabelInternal(run, colorizeOutcome: true);
    }

    private static string FormatRunLabelInternal(RunSummary run, bool colorizeOutcome)
    {
        if (!run.LoadSucceeded)
        {
            return $"{run.FileName} (Unreadable)";
        }

        DateTime localTime = DateTimeOffset.FromUnixTimeSeconds(run.StartTime).ToLocalTime().DateTime;
        string dateText = localTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        string charText = string.IsNullOrEmpty(run.PrimaryCharacterName) ? "Unknown" : run.PrimaryCharacterName;
        string multiText = run.PlayerCount > 1 ? $" ({run.PlayerCount}P)" : "";

        string modeText = run.GameType switch
        {
            RunGameType.Standard => $"A{run.Ascension}",
            RunGameType.Daily => "Daily",
            RunGameType.Blitz => "Blitz",
            RunGameType.Dread => "Dread",
            RunGameType.Custom => "Custom",
            _ => $"A{run.Ascension}"
        };

        string outcomeText = colorizeOutcome
            ? (run.Win ? "[color=#7AE87A]Win[/color]" : "[color=#FF6464]Loss[/color]")
            : (run.Win ? "Win" : "Loss");

        return $"{dateText} · {charText}{multiText} · {modeText} · {outcomeText}";
    }

    public static string GetOutcomeLabel(OutcomeFilter outcome) => outcome switch
    {
        OutcomeFilter.All => "Wins and Losses",
        OutcomeFilter.Wins => "Wins",
        OutcomeFilter.Losses => "Losses",
        _ => "Wins and Losses"
    };

    public static string GetGameTypeLabel(GameTypeFilter gameType) => gameType switch
    {
        GameTypeFilter.Any => "Any Game Type",
        GameTypeFilter.Standard => "Standard",
        GameTypeFilter.Daily => "Daily",
        GameTypeFilter.Blitz => "Blitz",
        GameTypeFilter.Dread => "Dread",
        GameTypeFilter.Custom => "Custom",
        _ => "Any Game Type"
    };

    public static string GetSortFieldLabel(SortField sortBy) => sortBy switch
    {
        SortField.Date => "Date",
        SortField.BadgeCount => "Badge Count",
        SortField.RunTime => "Run Time",
        SortField.RelicCount => "Relic Count",
        SortField.CardCount => "Card Count",
        _ => "Date"
    };

    public static string GetSortDirectionLabel(SortDirection direction) => direction switch
    {
        SortDirection.Descending => "Descending",
        SortDirection.Ascending => "Ascending",
        _ => "Descending"
    };
}
