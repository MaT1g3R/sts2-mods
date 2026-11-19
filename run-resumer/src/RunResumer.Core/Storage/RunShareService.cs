using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using RunResumer.Proto;

namespace RunResumer.Storage;

internal sealed class RunShareValidationException : Exception
{
    public RunShareValidationException(string message) : base(message) { }
    public RunShareValidationException(string message, Exception innerException) : base(message, innerException) { }
}

internal sealed record ShareImportPayload(
    RunHistory History,
    RunArchive Archive,
    string RawRunJson,
    int FloorCount);

internal sealed record ShareImportResult(
    long StartTime,
    long OriginalStartTime,
    bool WasRenamedOnCollision,
    int FloorCount,
    string CharacterEntry);

internal static class RunShareService
{
    public const string ShareExtension = ".zip";
    public const int CurrentShareFormatVersion = 1;

    private const string ManifestEntryName = "manifest.json";
    private const string HistoryEntryName = "history.run";
    private const string ArchiveEntryName = "archive.rrpb";
    private const long MaxEntryBytes = 32L * 1024 * 1024;

    public static string BuildDefaultFileName(RunHistory history)
    {
        string charSlug = history.Players.FirstOrDefault()?.Character.Entry.ToLowerInvariant() ?? "run";
        string outcome = history.Win ? "win" : (history.WasAbandoned ? "abandoned" : "loss");
        int floors = history.MapPointHistory.Sum(act => act.Count);
        return $"{charSlug}_f{floors}_{outcome}_{history.StartTime}{ShareExtension}";
    }

    public static string ExportToFile(RunHistory history, string destinationPath)
    {
        if (history.Players.Count != 1)
        {
            throw new RunShareValidationException("Only singleplayer runs can be exported.");
        }

        if (!RunArchiveStorage.TryReadArchive(history.StartTime, out RunArchive? archive) || archive == null || archive.Floors.Count == 0)
        {
            throw new RunShareValidationException("No Run Resumer snapshots exist for this run.");
        }

        if (!destinationPath.EndsWith(ShareExtension, StringComparison.OrdinalIgnoreCase))
        {
            destinationPath += ShareExtension;
        }

        byte[] bundleBytes = CreateShareBytes(history, archive);
        string? dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = destinationPath + ".tmp";
        File.WriteAllBytes(tempPath, bundleBytes);
        File.Move(tempPath, destinationPath, overwrite: true);
        return destinationPath;
    }

    public static byte[] CreateShareBytes(RunHistory history, RunArchive archive, string? rawRunJson = null)
    {
        if (history.Players.Count != 1)
        {
            throw new RunShareValidationException("Only singleplayer runs can be exported.");
        }
        if (archive.Floors.Count == 0)
        {
            throw new RunShareValidationException("Cannot export a run with zero snapshots.");
        }
        if (archive.StartTime != history.StartTime)
        {
            throw new RunShareValidationException("Run history and Run Resumer archive StartTime do not match.");
        }

        string historyJson = rawRunJson ?? JsonSerializationUtility.ToJson(history);
        byte[] archiveBytes = RunArchiveCodec.SerializeCompressed(archive);

        JsonObject manifest = new JsonObject
        {
            ["share_format_version"] = CurrentShareFormatVersion,
            ["start_time"] = history.StartTime,
            ["build_id"] = history.BuildId,
            ["run_schema_version"] = archive.SchemaVersion,
            ["history_schema_version"] = history.SchemaVersion,
            ["floor_count"] = archive.Floors.Count,
            ["character"] = history.Players[0].Character.ToString()
        };

        using MemoryStream ms = new MemoryStream();
        using (ZipArchive zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteZipEntry(zip, ManifestEntryName, Encoding.UTF8.GetBytes(manifest.ToJsonString()));
            WriteZipEntry(zip, HistoryEntryName, Encoding.UTF8.GetBytes(historyJson));
            WriteZipEntry(zip, ArchiveEntryName, archiveBytes);
        }

        return ms.ToArray();
    }

    public static ShareImportResult ImportFromFile(string sourcePath)
    {
        if (!File.Exists(sourcePath))
        {
            throw new RunShareValidationException("Shared run file was not found.");
        }

        byte[] bytes = File.ReadAllBytes(sourcePath);
        int expectedRunSchema = SaveManager.Instance.GetLatestSchemaVersion<SerializableRun>();
        int maxHistorySchema = SaveManager.Instance.GetLatestSchemaVersion<RunHistory>();

        ShareImportPayload payload = ValidateAndParseShare(
            bytes,
            expectedRunSchema,
            maxHistorySchema,
            id => id == ModelId.none || ModelDb.GetByIdOrNull<AbstractModel>(id) != null);

        HashSet<string> existingRunFiles = new HashSet<string>(
            SaveManager.Instance.GetAllRunHistoryNames(),
            StringComparer.OrdinalIgnoreCase);

        long originalStartTime = payload.History.StartTime;
        long targetStartTime = ResolveUniqueStartTime(
            originalStartTime,
            candidate => existingRunFiles.Contains($"{candidate}.run") || RunArchiveStorage.DoesArchiveExist(candidate));

        RunHistory historyToSave = payload.History;
        RunArchive archiveToSave = payload.Archive;
        if (targetStartTime != originalStartTime)
        {
            historyToSave = ApplyStartTime(payload.RawRunJson, archiveToSave, targetStartTime);
        }

        RunArchiveStorage.WriteImportedArchive(archiveToSave);
        try
        {
            SaveManager.Instance.SaveRunHistory(historyToSave);
            if (!SaveManager.Instance.GetAllRunHistoryNames().Contains($"{targetStartTime}.run"))
            {
                throw new RunShareValidationException("Failed to write imported run history to save storage.");
            }
        }
        catch
        {
            RunArchiveStorage.DeleteArchiveIfExists(targetStartTime);
            throw;
        }

        return new ShareImportResult(
            targetStartTime,
            originalStartTime,
            targetStartTime != originalStartTime,
            payload.FloorCount,
            historyToSave.Players[0].Character.Entry);
    }

    public static ShareImportPayload ValidateAndParseShare(
        byte[] shareBytes,
        int expectedRunSchemaVersion,
        int maxHistorySchemaVersion,
        Func<ModelId, bool> isModelKnown)
    {
        if (shareBytes == null || shareBytes.Length == 0)
        {
            throw new RunShareValidationException("Shared run file is empty.");
        }

        byte[] historyBytes;
        byte[] archiveBytes;
        try
        {
            using MemoryStream ms = new MemoryStream(shareBytes);
            using ZipArchive zip = new ZipArchive(ms, ZipArchiveMode.Read);

            ZipArchiveEntry? historyEntry = zip.GetEntry(HistoryEntryName)
                ?? zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".run", StringComparison.OrdinalIgnoreCase));
            ZipArchiveEntry? archiveEntry = zip.GetEntry(ArchiveEntryName)
                ?? zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".rrpb", StringComparison.OrdinalIgnoreCase));

            if (historyEntry == null || archiveEntry == null)
            {
                throw new RunShareValidationException("Invalid zip file: missing run history or Run Resumer snapshot data.");
            }

            historyBytes = ReadZipEntryBytes(historyEntry);
            archiveBytes = ReadZipEntryBytes(archiveEntry);
        }
        catch (InvalidDataException ex)
        {
            throw new RunShareValidationException("Invalid or corrupted zip archive.", ex);
        }

        string rawRunJson = Encoding.UTF8.GetString(historyBytes);
        RunHistory history = ParseAndValidateRunHistory(rawRunJson, maxHistorySchemaVersion);
        RunArchive archive = ParseAndValidateRunArchive(archiveBytes, history, expectedRunSchemaVersion);
        ValidateAllModelsKnown(archive, history, isModelKnown);

        // Verify the archive reconstructs cleanly before writing anything to disk.
        try
        {
            RunArchiveCodec.ReconstructRun(archive, archive.Floors[0].FloorNum, history);
        }
        catch (Exception ex) when (ex is not RunShareValidationException)
        {
            throw new RunShareValidationException("Shared run snapshots could not be reconstructed cleanly.", ex);
        }

        return new ShareImportPayload(history, archive, rawRunJson, archive.Floors.Count);
    }

    public static long ResolveUniqueStartTime(long desiredStartTime, Func<long, bool> startTimeExists)
    {
        long candidate = desiredStartTime > 0 ? desiredStartTime : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        while (startTimeExists(candidate))
        {
            candidate++;
        }
        return candidate;
    }

    public static RunHistory ApplyStartTime(string rawRunJson, RunArchive archive, long newStartTime)
    {
        archive.StartTime = newStartTime;
        JsonNode? root = JsonNode.Parse(rawRunJson);
        if (root is not JsonObject obj)
        {
            throw new RunShareValidationException("Invalid run history JSON structure.");
        }

        obj["start_time"] = newStartTime;
        string updatedJson = obj.ToJsonString();
        ReadSaveResult<RunHistory> result = JsonSerializationUtility.FromJson<RunHistory>(updatedJson);
        if (!result.Success || result.SaveData == null)
        {
            throw new RunShareValidationException("Failed to update StartTime on imported run history.");
        }

        return result.SaveData;
    }

    private static RunHistory ParseAndValidateRunHistory(string rawRunJson, int maxHistorySchemaVersion)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(rawRunJson);
        }
        catch (JsonException ex)
        {
            throw new RunShareValidationException("Shared run history contains invalid JSON.", ex);
        }

        if (root is not JsonObject obj || !obj.TryGetPropertyValue("schema_version", out JsonNode? schemaNode) || schemaNode == null)
        {
            throw new RunShareValidationException("Shared run history is missing schema_version.");
        }

        int schemaVersion;
        try
        {
            schemaVersion = schemaNode.GetValue<int>();
        }
        catch (Exception ex)
        {
            throw new RunShareValidationException("Shared run history has an invalid schema_version.", ex);
        }

        if (schemaVersion <= 0 || schemaVersion > maxHistorySchemaVersion)
        {
            throw new RunShareValidationException(
                $"Shared run history uses schema v{schemaVersion}, which is incompatible with this game version (v{maxHistorySchemaVersion}).");
        }

        ReadSaveResult<RunHistory> parsed = JsonSerializationUtility.FromJson<RunHistory>(rawRunJson);
        if (!parsed.Success || parsed.SaveData == null)
        {
            throw new RunShareValidationException(
                $"Failed to parse shared run history: {parsed.ErrorMessage ?? parsed.Status.ToString()}");
        }

        RunHistory history = parsed.SaveData;
        if (history.Players.Count != 1)
        {
            throw new RunShareValidationException("Only singleplayer runs can be imported.");
        }
        if (history.StartTime <= 0)
        {
            throw new RunShareValidationException("Shared run history has an invalid StartTime.");
        }

        return history;
    }

    private static RunArchive ParseAndValidateRunArchive(
        byte[] archiveBytes,
        RunHistory history,
        int expectedRunSchemaVersion)
    {
        RunArchive archive;
        try
        {
            archive = RunArchiveCodec.DeserializeCompressed(archiveBytes);
        }
        catch (Exception ex)
        {
            throw new RunShareValidationException("Shared Run Resumer archive is corrupted or unreadable.", ex);
        }

        if (archive.ArchiveFormatVersion != RunArchiveCodec.CurrentArchiveVersion)
        {
            throw new RunShareValidationException(
                $"Unsupported Run Resumer archive format v{archive.ArchiveFormatVersion} (expected v{RunArchiveCodec.CurrentArchiveVersion}).");
        }

        if (archive.SchemaVersion != expectedRunSchemaVersion)
        {
            throw new RunShareValidationException(
                $"Shared run was saved with game schema v{archive.SchemaVersion}, but this game expects v{expectedRunSchemaVersion}.");
        }

        if (archive.Floors.Count == 0)
        {
            throw new RunShareValidationException("Shared Run Resumer archive contains no floor snapshots.");
        }

        if (archive.StartTime != history.StartTime)
        {
            throw new RunShareValidationException("Shared run history and snapshot archive belong to different runs.");
        }

        return archive;
    }

    private static void ValidateAllModelsKnown(
        RunArchive archive,
        RunHistory history,
        Func<ModelId, bool> isModelKnown)
    {
        List<string> missing = new List<string>();
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

        void CheckModelId(ModelId? id)
        {
            if (id == null || id == ModelId.none)
            {
                return;
            }
            string key = id.ToString();
            if (seen.Add(key) && !isModelKnown(id))
            {
                missing.Add(key);
            }
        }

        foreach (string rawId in archive.ModelIds)
        {
            if (string.IsNullOrWhiteSpace(rawId))
            {
                continue;
            }
            ModelId parsed;
            try
            {
                parsed = ModelId.Deserialize(rawId);
            }
            catch (Exception ex)
            {
                throw new RunShareValidationException($"Shared archive contains malformed model ID '{rawId}'.", ex);
            }
            CheckModelId(parsed);
        }

        CheckModelId(history.KilledByEncounter);
        CheckModelId(history.KilledByEvent);
        foreach (ModelId actId in history.Acts)
        {
            CheckModelId(actId);
        }
        foreach (SerializableModifier mod in history.Modifiers)
        {
            CheckModelId(mod.Id);
        }

        RunHistoryPlayer player = history.Players[0];
        CheckModelId(player.Character);
        foreach (SerializableCard card in player.Deck)
        {
            CheckModelId(card.Id);
            CheckModelId(card.Enchantment?.Id);
        }
        foreach (SerializableRelic relic in player.Relics)
        {
            CheckModelId(relic.Id);
        }
        foreach (SerializablePotion potion in player.Potions)
        {
            CheckModelId(potion.Id);
        }
        foreach (List<MapPointHistoryEntry> actEntries in history.MapPointHistory)
        {
            foreach (MapPointHistoryEntry entry in actEntries)
            {
                foreach (MapPointRoomHistoryEntry room in entry.Rooms)
                {
                    CheckModelId(room.ModelId);
                }
            }
        }

        if (missing.Count > 0)
        {
            string preview = string.Join(", ", missing.Take(3));
            string extra = missing.Count > 3 ? $" (+{missing.Count - 3} more)" : string.Empty;
            throw new RunShareValidationException(
                $"Shared run references missing content: {preview}{extra}. Ensure matching game version and mods are installed.");
        }
    }

    private static byte[] ReadZipEntryBytes(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxEntryBytes)
        {
            throw new RunShareValidationException($"Entry '{entry.FullName}' exceeds maximum allowed size.");
        }

        using Stream stream = entry.Open();
        using MemoryStream ms = new MemoryStream();
        byte[] buffer = new byte[16384];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > MaxEntryBytes)
            {
                throw new RunShareValidationException($"Entry '{entry.FullName}' exceeds maximum allowed size.");
            }
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    private static void WriteZipEntry(ZipArchive zip, string entryName, byte[] data)
    {
        ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        stream.Write(data, 0, data.Length);
    }
}
