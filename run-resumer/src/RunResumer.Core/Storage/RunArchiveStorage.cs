using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using MegaCrit.Sts2.Core.TestSupport;
using RunResumer.Proto;

namespace RunResumer.Storage;

internal sealed record ProfileStorageInfo(
    int ProfileId,
    bool IsCurrentProfile,
    int FileCount,
    long TotalBytes);

internal sealed record PruneResult(
    int ProfileId,
    int DeletedCount,
    long FreedBytes);

internal static class RunArchiveStorage
{
    private const string ArchiveDirName = "run_resumer";
    private const string ArchiveExtension = ".rrpb";

    private static readonly AccessTools.FieldRef<SaveManager, ISaveStore> SaveStoreRef =
        AccessTools.FieldRefAccess<SaveManager, ISaveStore>("_saveStore");

    private sealed record ArchiveIndex(HashSet<int> Floors, bool IsImported);

    private static readonly object _sync = new object();
    private static readonly Dictionary<(int profileId, long startTime), ArchiveIndex> _floorIndexCache = new();
    private static (int profileId, long startTime, RunArchive archive)? _activeArchive;

    public static void InvalidateCache()
    {
        lock (_sync)
        {
            _floorIndexCache.Clear();
            _activeArchive = null;
        }
    }

    public static void CaptureFloor(SerializableRun run)
    {
        if (TestMode.IsOn || run.Players.Count != 1)
        {
            return;
        }

        int floorNum = run.FloorReached + 1;
        int profileId = SaveManager.Instance.CurrentProfileId;
        string path = GetArchiveOsPath(run.StartTime);

        lock (_sync)
        {
            try
            {
                RunArchive archive = GetOrLoadArchiveForWrite(profileId, run, path);
                RunArchiveCodec.UpsertFloor(archive, run, floorNum);

                byte[] compressed = RunArchiveCodec.SerializeCompressed(archive);
                WriteAtomic(path, compressed);

                UpdateFloorIndexCacheLocked(profileId, archive);
            }
            catch (Exception ex)
            {
                Log.Error($"[RunResumer] Failed to capture floor {floorNum} for run {run.StartTime}: {ex}");
            }
        }
    }

    public static bool HasFloor(RunHistory? history, int floorNum)
    {
        if (TestMode.IsOn || history == null || history.Players.Count != 1 || floorNum <= 0)
        {
            return false;
        }

        int profileId = SaveManager.Instance.CurrentProfileId;
        lock (_sync)
        {
            return GetOrLoadArchiveIndexLocked(profileId, history.StartTime).Floors.Contains(floorNum);
        }
    }

    public static bool HasAnyFloor(RunHistory? history)
    {
        if (TestMode.IsOn || history == null || history.Players.Count != 1)
        {
            return false;
        }

        int profileId = SaveManager.Instance.CurrentProfileId;
        lock (_sync)
        {
            return GetOrLoadArchiveIndexLocked(profileId, history.StartTime).Floors.Count > 0;
        }
    }

    public static bool IsImported(RunHistory? history)
    {
        if (TestMode.IsOn || history == null || history.Players.Count != 1)
        {
            return false;
        }

        return IsImported(history.StartTime);
    }

    public static bool IsImported(long startTime)
    {
        if (TestMode.IsOn || startTime <= 0)
        {
            return false;
        }

        int profileId = SaveManager.Instance.CurrentProfileId;
        lock (_sync)
        {
            return GetOrLoadArchiveIndexLocked(profileId, startTime).IsImported;
        }
    }

    public static bool TryReadArchive(long startTime, out RunArchive? archive)
    {
        archive = null;
        if (TestMode.IsOn)
        {
            return false;
        }

        string path = GetArchiveOsPath(startTime);
        lock (_sync)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                archive = RunArchiveCodec.DeserializeCompressed(File.ReadAllBytes(path));
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"[RunResumer] Failed to read archive {path}: {ex.Message}");
                return false;
            }
        }
    }

    public static bool DoesArchiveExist(long startTime)
    {
        if (TestMode.IsOn)
        {
            return false;
        }

        lock (_sync)
        {
            return File.Exists(GetArchiveOsPath(startTime));
        }
    }

    public static void WriteImportedArchive(RunArchive archive)
    {
        if (TestMode.IsOn)
        {
            return;
        }

        int profileId = SaveManager.Instance.CurrentProfileId;
        string path = GetArchiveOsPath(archive.StartTime);
        byte[] compressed = RunArchiveCodec.SerializeCompressed(archive);

        lock (_sync)
        {
            WriteAtomic(path, compressed);
            UpdateFloorIndexCacheLocked(profileId, archive);
        }
    }

    public static void DeleteArchiveIfExists(long startTime)
    {
        if (TestMode.IsOn)
        {
            return;
        }

        int profileId = SaveManager.Instance.CurrentProfileId;
        string path = GetArchiveOsPath(startTime);
        lock (_sync)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            _floorIndexCache.Remove((profileId, startTime));
            if (_activeArchive is { startTime: var activeStart } && activeStart == startTime)
            {
                _activeArchive = null;
            }
        }
    }

    private static ArchiveIndex GetOrLoadArchiveIndexLocked(int profileId, long startTime)
    {
        if (_floorIndexCache.TryGetValue((profileId, startTime), out ArchiveIndex? cached))
        {
            return cached;
        }

        string path = GetArchiveOsPath(startTime);
        if (!File.Exists(path))
        {
            ArchiveIndex empty = new ArchiveIndex(new HashSet<int>(), IsImported: false);
            _floorIndexCache[(profileId, startTime)] = empty;
            return empty;
        }

        try
        {
            RunArchive archive = RunArchiveCodec.DeserializeCompressed(File.ReadAllBytes(path));
            return UpdateFloorIndexCacheLocked(profileId, archive);
        }
        catch (Exception ex)
        {
            Log.Warn($"[RunResumer] Failed to index archive {path}: {ex.Message}");
            ArchiveIndex empty = new ArchiveIndex(new HashSet<int>(), IsImported: false);
            _floorIndexCache[(profileId, startTime)] = empty;
            return empty;
        }
    }

    public static SerializableRun? LoadFloor(RunHistory history, int floorNum)
    {
        if (TestMode.IsOn || history.Players.Count != 1)
        {
            return null;
        }

        string path = GetArchiveOsPath(history.StartTime);
        lock (_sync)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            RunArchive archive = RunArchiveCodec.DeserializeCompressed(File.ReadAllBytes(path));
            return RunArchiveCodec.ReconstructRun(archive, floorNum, history);
        }
    }

    public static void CleanupOrphans()
    {
        if (TestMode.IsOn)
        {
            return;
        }

        try
        {
            string dirPath = GetArchiveDirectoryOsPath();
            if (!Directory.Exists(dirPath))
            {
                return;
            }

            HashSet<long> knownStartTimes = new HashSet<long>();
            foreach (string name in SaveManager.Instance.GetAllRunHistoryNames())
            {
                string stem = Path.GetFileNameWithoutExtension(name);
                if (long.TryParse(stem, out long startTime))
                {
                    knownStartTimes.Add(startTime);
                }
            }

            if (SaveManager.Instance.HasRunSave)
            {
                ReadSaveResult<SerializableRun> current = SaveManager.Instance.LoadRunSave();
                if (current.Success && current.SaveData != null)
                {
                    knownStartTimes.Add(current.SaveData.StartTime);
                }
            }

            if (RunManager.Instance.IsInProgress && RunManager.Instance.ShouldSave)
            {
                knownStartTimes.Add(RunManager.Instance.ToSave(null).StartTime);
            }

            int deleted = 0;
            lock (_sync)
            {
                foreach (string file in Directory.EnumerateFiles(dirPath, "*" + ArchiveExtension))
                {
                    string stem = Path.GetFileNameWithoutExtension(file);
                    if (long.TryParse(stem, out long startTime) && !knownStartTimes.Contains(startTime))
                    {
                        try
                        {
                            File.Delete(file);
                            _floorIndexCache.Remove((SaveManager.Instance.CurrentProfileId, startTime));
                            if (_activeArchive is { startTime: var activeStart } && activeStart == startTime)
                            {
                                _activeArchive = null;
                            }
                            deleted++;
                        }
                        catch (Exception ex)
                        {
                            Log.Warn($"[RunResumer] Could not delete orphan archive {file}: {ex.Message}");
                        }
                    }
                }
            }

            if (deleted > 0)
            {
                Log.Info($"[RunResumer] Cleaned up {deleted} orphan replay archive(s).");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[RunResumer] Orphan cleanup skipped: {ex.Message}");
        }
    }

    private static RunArchive GetOrLoadArchiveForWrite(int profileId, SerializableRun run, string path)
    {
        if (_activeArchive is { } cached && cached.profileId == profileId && cached.startTime == run.StartTime)
        {
            return cached.archive;
        }

        RunArchive archive;
        if (File.Exists(path))
        {
            try
            {
                archive = RunArchiveCodec.DeserializeCompressed(File.ReadAllBytes(path));
                if (archive.ArchiveFormatVersion != RunArchiveCodec.CurrentArchiveVersion)
                {
                    archive = RunArchiveCodec.CreateNewArchive(run);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[RunResumer] Existing archive {path} unreadable, recreating: {ex.Message}");
                archive = RunArchiveCodec.CreateNewArchive(run);
            }
        }
        else
        {
            archive = RunArchiveCodec.CreateNewArchive(run);
        }

        _activeArchive = (profileId, run.StartTime, archive);
        return archive;
    }

    private static ArchiveIndex UpdateFloorIndexCacheLocked(int profileId, RunArchive archive)
    {
        HashSet<int> floors = new HashSet<int>(archive.Floors.Count);
        foreach (PbFloorSnapshot floor in archive.Floors)
        {
            floors.Add(floor.FloorNum);
        }
        ArchiveIndex index = new ArchiveIndex(floors, archive.IsImported);
        _floorIndexCache[(profileId, archive.StartTime)] = index;
        return index;
    }

    public static IReadOnlyList<ProfileStorageInfo> GetAllProfileStorageStats()
    {
        var results = new List<ProfileStorageInfo>(ProfileSaveManager.maxProfileCount);
        if (TestMode.IsOn)
        {
            return results;
        }

        int currentProfileId = SaveManager.Instance.IsProfileInitialized
            ? SaveManager.Instance.CurrentProfileId
            : -1;

        lock (_sync)
        {
            for (int profileId = 1; profileId <= ProfileSaveManager.maxProfileCount; profileId++)
            {
                string dirPath = GetArchiveDirectoryOsPath(profileId);
                (int fileCount, long totalBytes) = MeasureArchiveDirectory(dirPath);
                results.Add(new ProfileStorageInfo(
                    profileId,
                    profileId == currentProfileId,
                    fileCount,
                    totalBytes));
            }
        }

        return results;
    }

    public static PruneResult DeleteOldestArchives(int profileId, int count)
    {
        if (TestMode.IsOn || count <= 0 || profileId < 1 || profileId > ProfileSaveManager.maxProfileCount)
        {
            return new PruneResult(profileId, 0, 0L);
        }

        long? protectedStartTime = null;
        if (SaveManager.Instance.IsProfileInitialized &&
            profileId == SaveManager.Instance.CurrentProfileId &&
            RunManager.Instance.IsInProgress &&
            RunManager.Instance.ShouldSave)
        {
            try
            {
                protectedStartTime = RunManager.Instance.ToSave(null).StartTime;
            }
            catch
            {
                // Ignore if run state is not serializable at this moment
            }
        }

        string dirPath = GetArchiveDirectoryOsPath(profileId);
        lock (_sync)
        {
            (int deletedCount, long freedBytes, List<long> deletedStartTimes) =
                DeleteOldestArchivesInDirectory(dirPath, count, protectedStartTime);

            foreach (long startTime in deletedStartTimes)
            {
                _floorIndexCache.Remove((profileId, startTime));
                if (_activeArchive is { profileId: var activeProfile, startTime: var activeStart } &&
                    activeProfile == profileId && activeStart == startTime)
                {
                    _activeArchive = null;
                }
            }

            if (deletedCount > 0)
            {
                Log.Info($"[RunResumer] Pruned {deletedCount} oldest archive(s) from Profile {profileId} (freed {FormatBytes(freedBytes)}).");
            }

            return new PruneResult(profileId, deletedCount, freedBytes);
        }
    }

    internal static (int FileCount, long TotalBytes) MeasureArchiveDirectory(string dirPath)
    {
        if (string.IsNullOrEmpty(dirPath) || !Directory.Exists(dirPath))
        {
            return (0, 0L);
        }

        int fileCount = 0;
        long totalBytes = 0L;
        foreach (string file in Directory.EnumerateFiles(dirPath, "*" + ArchiveExtension))
        {
            try
            {
                FileInfo info = new FileInfo(file);
                fileCount++;
                totalBytes += info.Length;
            }
            catch
            {
                // Skip unreadable or vanished files
            }
        }

        return (fileCount, totalBytes);
    }

    internal static (int DeletedCount, long FreedBytes, List<long> DeletedStartTimes) DeleteOldestArchivesInDirectory(
        string dirPath,
        int count,
        long? protectedStartTime = null)
    {
        var deletedStartTimes = new List<long>();
        if (count <= 0 || string.IsNullOrEmpty(dirPath) || !Directory.Exists(dirPath))
        {
            return (0, 0L, deletedStartTimes);
        }

        var entries = new List<(string FilePath, long SortKey, long? StartTime, long SizeBytes)>();
        foreach (string file in Directory.EnumerateFiles(dirPath, "*" + ArchiveExtension))
        {
            try
            {
                FileInfo info = new FileInfo(file);
                string stem = Path.GetFileNameWithoutExtension(file);
                long? parsedStartTime = long.TryParse(stem, NumberStyles.Integer, CultureInfo.InvariantCulture, out long st)
                    ? st
                    : null;

                if (protectedStartTime.HasValue && parsedStartTime == protectedStartTime.Value)
                {
                    continue;
                }

                long sortKey = parsedStartTime ?? new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();
                entries.Add((file, sortKey, parsedStartTime, info.Length));
            }
            catch
            {
                // Skip vanished/inaccessible entries
            }
        }

        entries.Sort((a, b) =>
        {
            int cmp = a.SortKey.CompareTo(b.SortKey);
            return cmp != 0 ? cmp : string.Compare(a.FilePath, b.FilePath, StringComparison.Ordinal);
        });

        int deletedCount = 0;
        long freedBytes = 0L;
        int toDelete = Math.Min(count, entries.Count);

        for (int i = 0; i < toDelete; i++)
        {
            var entry = entries[i];
            try
            {
                File.Delete(entry.FilePath);
                deletedCount++;
                freedBytes += entry.SizeBytes;
                if (entry.StartTime.HasValue)
                {
                    deletedStartTimes.Add(entry.StartTime.Value);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[RunResumer] Could not delete archive {entry.FilePath}: {ex.Message}");
            }
        }

        return (deletedCount, freedBytes, deletedStartTimes);
    }

    internal static string FormatBytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        if (bytes < 1024L)
        {
            return $"{bytes} B";
        }

        double kb = bytes / 1024.0;
        if (kb < 1024.0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{kb:0.#} KB");
        }

        double mb = kb / 1024.0;
        if (mb < 1024.0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{mb:0.##} MB");
        }

        double gb = mb / 1024.0;
        return string.Create(CultureInfo.InvariantCulture, $"{gb:0.##} GB");
    }

    private static string GetArchiveDirectoryOsPath()
    {
        string userPath = SaveManager.Instance.GetProfileScopedPath(ArchiveDirName);
        return ProjectSettings.GlobalizePath(userPath);
    }

    private static string GetArchiveDirectoryOsPath(int profileId)
    {
        ISaveStore saveStore = SaveStoreRef(SaveManager.Instance);
        string userPath = saveStore.GetFullPath(Path.Combine(UserDataPathProvider.GetProfileDir(profileId), ArchiveDirName));
        return ProjectSettings.GlobalizePath(userPath);
    }

    private static string GetArchiveOsPath(long startTime)
    {
        return Path.Combine(GetArchiveDirectoryOsPath(), $"{startTime}{ArchiveExtension}");
    }

    private static void WriteAtomic(string destinationPath, byte[] data)
    {
        string? dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = destinationPath + ".tmp";
        File.WriteAllBytes(tempPath, data);
        File.Move(tempPath, destinationPath, overwrite: true);
    }
}
