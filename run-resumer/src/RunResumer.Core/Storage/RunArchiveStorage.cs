using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.TestSupport;
using RunResumer.Proto;

namespace RunResumer.Storage;

internal static class RunArchiveStorage
{
    private const string ArchiveDirName = "run_resumer";
    private const string ArchiveExtension = ".rrpb";

    private static readonly object _sync = new object();
    private static readonly Dictionary<(int profileId, long startTime), HashSet<int>> _floorIndexCache = new();
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
        long startTime = history.StartTime;

        lock (_sync)
        {
            if (_floorIndexCache.TryGetValue((profileId, startTime), out HashSet<int>? cached))
            {
                return cached.Contains(floorNum);
            }

            string path = GetArchiveOsPath(startTime);
            if (!File.Exists(path))
            {
                _floorIndexCache[(profileId, startTime)] = new HashSet<int>();
                return false;
            }

            try
            {
                RunArchive archive = RunArchiveCodec.DeserializeCompressed(File.ReadAllBytes(path));
                HashSet<int> floors = UpdateFloorIndexCacheLocked(profileId, archive);
                return floors.Contains(floorNum);
            }
            catch (Exception ex)
            {
                Log.Warn($"[RunResumer] Failed to index archive {path}: {ex.Message}");
                _floorIndexCache[(profileId, startTime)] = new HashSet<int>();
                return false;
            }
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

    private static HashSet<int> UpdateFloorIndexCacheLocked(int profileId, RunArchive archive)
    {
        HashSet<int> floors = new HashSet<int>(archive.Floors.Count);
        foreach (PbFloorSnapshot floor in archive.Floors)
        {
            floors.Add(floor.FloorNum);
        }
        _floorIndexCache[(profileId, archive.StartTime)] = floors;
        return floors;
    }

    private static string GetArchiveDirectoryOsPath()
    {
        string userPath = SaveManager.Instance.GetProfileScopedPath(ArchiveDirName);
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
