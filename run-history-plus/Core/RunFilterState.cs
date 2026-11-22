namespace RunHistoryPlus.Core;

public enum RunGameType
{
    Unknown,
    Standard,
    Daily,
    Blitz,
    Dread,
    Custom
}

public enum OutcomeFilter
{
    All,
    Wins,
    Losses
}

public enum GameTypeFilter
{
    Any,
    Standard,
    Daily,
    Blitz,
    Dread,
    Custom
}

public enum MultiMatchMode
{
    All,
    Any
}

public enum SortField
{
    Date,
    BadgeCount,
    RunTime,
    RelicCount,
    CardCount
}

public enum SortDirection
{
    Descending,
    Ascending
}

public sealed class RunFilterState
{
    public OutcomeFilter Outcome { get; set; } = OutcomeFilter.All;
    public string? CharacterId { get; set; }
    public GameTypeFilter GameType { get; set; } = GameTypeFilter.Any;
    public HashSet<string> RequiredRelicIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public MultiMatchMode RelicMatchMode { get; set; } = MultiMatchMode.All;
    public SortField SortBy { get; set; } = SortField.Date;
    public SortDirection SortDirection { get; set; } = SortDirection.Descending;

    public bool HasMoreFilters => RequiredRelicIds.Count > 0;

    public int ActiveMoreFilterCount => RequiredRelicIds.Count;

    public bool HasActiveFilters =>
        Outcome != OutcomeFilter.All ||
        !string.IsNullOrEmpty(CharacterId) ||
        GameType != GameTypeFilter.Any ||
        HasMoreFilters;

    public bool IsDefault =>
        !HasActiveFilters &&
        SortBy == SortField.Date &&
        SortDirection == SortDirection.Descending;

    public void ClearMoreFilters()
    {
        RequiredRelicIds.Clear();
        RelicMatchMode = MultiMatchMode.All;
    }

    public void ResetFilters()
    {
        Outcome = OutcomeFilter.All;
        CharacterId = null;
        GameType = GameTypeFilter.Any;
        ClearMoreFilters();
    }

    public void ResetAll()
    {
        ResetFilters();
        SortBy = SortField.Date;
        SortDirection = SortDirection.Descending;
    }
}

public sealed record RunSummary(
    string FileName,
    long StartTime,
    float RunTime,
    int Ascension,
    int FloorCount,
    int BadgeCount,
    int RelicCount,
    int CardCount,
    bool Win,
    bool WasAbandoned,
    RunGameType GameType,
    string PrimaryCharacterId,
    string PrimaryCharacterName,
    IReadOnlyList<string> PlayerCharacterIds,
    IReadOnlyCollection<string> PlayerRelicIds,
    int PlayerCount,
    string Seed,
    bool LoadSucceeded);
