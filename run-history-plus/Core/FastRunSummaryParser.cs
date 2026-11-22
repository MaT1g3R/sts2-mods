using System.Text.Json;

namespace RunHistoryPlus.Core;

/// <summary>
/// Lightweight, thread-safe parser that extracts RunSummary metadata directly from .run JSON
/// without full RunHistory object-graph deserialization, Steam Cloud IPC, or Godot dependencies.
/// </summary>
public static class FastRunSummaryParser
{
    public static RunSummary Parse(
        string fileName,
        ReadOnlyMemory<byte> utf8Json,
        ulong localPlayerId,
        IReadOnlyDictionary<string, string> characterDisplayNames)
    {
        using JsonDocument doc = JsonDocument.Parse(utf8Json);
        return ParseRoot(fileName, doc.RootElement, localPlayerId, characterDisplayNames);
    }

    public static RunSummary Parse(
        string fileName,
        string json,
        ulong localPlayerId,
        IReadOnlyDictionary<string, string> characterDisplayNames)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return ParseRoot(fileName, doc.RootElement, localPlayerId, characterDisplayNames);
    }

    private static RunSummary ParseRoot(
        string fileName,
        JsonElement root,
        ulong localPlayerId,
        IReadOnlyDictionary<string, string> characterDisplayNames)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Unreadable(fileName);
        }

        long startTime = GetInt64(root, "start_time");
        float runTime = GetSingle(root, "run_time");
        int ascension = GetInt32(root, "ascension");
        bool win = GetBool(root, "win");
        bool wasAbandoned = GetBool(root, "was_abandoned");
        bool isCustomGame = GetBool(root, "is_custom_game");
        string seed = GetString(root, "seed");
        RunGameType gameType = ParseGameType(root, isCustomGame);

        // Players: find primary (local) player (matching localPlayerId, else first).
        // All filters (character, relics, counts) apply only to the local player's character.
        int playerCount = 0;
        string primaryCharId = string.Empty;
        int badgeCount = 0;
        int relicCount = 0;
        int cardCount = 0;
        bool matchedLocalPlayer = false;
        var primaryRelicIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (root.TryGetProperty("players", out JsonElement playersEl) && playersEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement playerEl in playersEl.EnumerateArray())
            {
                playerCount++;
                ulong playerId = GetUInt64(playerEl, "id");
                bool isLocal = localPlayerId != 0 && playerId == localPlayerId;

                if (isLocal || (!matchedLocalPlayer && playerCount == 1))
                {
                    if (isLocal)
                    {
                        matchedLocalPlayer = true;
                    }

                    primaryCharId = NormalizeModelEntry(GetString(playerEl, "character"));
                    badgeCount = GetArrayLength(playerEl, "badges");
                    cardCount = GetArrayLength(playerEl, "deck");

                    primaryRelicIds.Clear();
                    int thisPlayerRelicCount = 0;
                    if (playerEl.TryGetProperty("relics", out JsonElement relicsEl) && relicsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement relicEl in relicsEl.EnumerateArray())
                        {
                            thisPlayerRelicCount++;
                            string relicId = NormalizeModelEntry(GetString(relicEl, "id"));
                            if (!string.IsNullOrEmpty(relicId))
                            {
                                primaryRelicIds.Add(relicId);
                            }
                        }
                    }
                    relicCount = thisPlayerRelicCount;
                }
            }
        }

        IReadOnlyList<string> playerCharIds = string.IsNullOrEmpty(primaryCharId)
            ? Array.Empty<string>()
            : new[] { primaryCharId };
        IReadOnlyCollection<string> playerRelicIds = primaryRelicIds;

        string primaryCharName = ResolveCharacterDisplayName(primaryCharId, characterDisplayNames);

        int floorCount = 0;
        if (root.TryGetProperty("map_point_history", out JsonElement actsEl) && actsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement actEl in actsEl.EnumerateArray())
            {
                if (actEl.ValueKind == JsonValueKind.Array)
                {
                    floorCount += actEl.GetArrayLength();
                }
            }
        }

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
            primaryCharId,
            primaryCharName,
            playerCharIds,
            playerRelicIds,
            playerCount,
            seed,
            true);
    }

    public static RunSummary Unreadable(string fileName) => new(
        fileName, 0, 0f, 0, 0, 0, 0, 0, false, false, RunGameType.Unknown,
        string.Empty, "Unknown", Array.Empty<string>(), Array.Empty<string>(), 0, string.Empty, false);

    private static RunGameType ParseGameType(JsonElement root, bool isCustomGame)
    {
        if (isCustomGame)
        {
            return RunGameType.Custom;
        }

        if (!root.TryGetProperty("game_mode", out JsonElement modeEl))
        {
            return RunGameType.Standard;
        }

        if (modeEl.ValueKind == JsonValueKind.Number && modeEl.TryGetInt32(out int modeNum))
        {
            return modeNum switch
            {
                1 => RunGameType.Standard,
                2 => RunGameType.Daily,
                3 => RunGameType.Dread,
                4 => RunGameType.Blitz,
                _ => RunGameType.Unknown
            };
        }

        if (modeEl.ValueKind == JsonValueKind.String)
        {
            string? s = modeEl.GetString();
            if (string.Equals(s, "standard", StringComparison.OrdinalIgnoreCase)) return RunGameType.Standard;
            if (string.Equals(s, "daily", StringComparison.OrdinalIgnoreCase)) return RunGameType.Daily;
            if (string.Equals(s, "dread", StringComparison.OrdinalIgnoreCase)) return RunGameType.Dread;
            if (string.Equals(s, "blitz", StringComparison.OrdinalIgnoreCase)) return RunGameType.Blitz;
        }

        return RunGameType.Unknown;
    }

    private static string NormalizeModelEntry(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        int dot = raw.LastIndexOf('.');
        return dot >= 0 && dot < raw.Length - 1 ? raw[(dot + 1)..] : raw;
    }

    private static string ResolveCharacterDisplayName(string entry, IReadOnlyDictionary<string, string> displayNames)
    {
        if (string.IsNullOrEmpty(entry))
        {
            return "Unknown";
        }

        if (displayNames.TryGetValue(entry, out string? name) && !string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        return string.Join(" ", entry
            .Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
    }

    private static int GetArrayLength(JsonElement obj, string prop)
    {
        return obj.TryGetProperty(prop, out JsonElement el) && el.ValueKind == JsonValueKind.Array
            ? el.GetArrayLength()
            : 0;
    }

    private static int GetInt32(JsonElement obj, string prop)
    {
        return obj.TryGetProperty(prop, out JsonElement el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int val)
            ? val
            : 0;
    }

    private static long GetInt64(JsonElement obj, string prop)
    {
        return obj.TryGetProperty(prop, out JsonElement el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out long val)
            ? val
            : 0L;
    }

    private static ulong GetUInt64(JsonElement obj, string prop)
    {
        return obj.TryGetProperty(prop, out JsonElement el) && el.ValueKind == JsonValueKind.Number && el.TryGetUInt64(out ulong val)
            ? val
            : 0UL;
    }

    private static float GetSingle(JsonElement obj, string prop)
    {
        return obj.TryGetProperty(prop, out JsonElement el) && el.ValueKind == JsonValueKind.Number && el.TryGetSingle(out float val)
            ? val
            : 0f;
    }

    private static bool GetBool(JsonElement obj, string prop)
    {
        return obj.TryGetProperty(prop, out JsonElement el) && el.ValueKind == JsonValueKind.True;
    }

    private static string GetString(JsonElement obj, string prop)
    {
        return obj.TryGetProperty(prop, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? string.Empty
            : string.Empty;
    }
}
