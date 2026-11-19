using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Runtime.Loader;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.MapDrawing;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Unlocks;
using RunResumer;
using RunResumer.Proto;
using RunResumer.Storage;

namespace RunResumer.Tests;

internal static class Program
{
    public static int Main()
    {
        TestLoaderIsolatedGetTypes();
        TestHarmonyPatchTargetsResolve();
        TestRoundTripAndCompression();
        TestShareExportImportAndValidation();
        Console.WriteLine("ALL TESTS PASSED");
        return 0;
    }

    private sealed class IsolatedModLoadContext : AssemblyLoadContext
    {
        public IsolatedModLoadContext() : base(isCollectible: true) { }
        protected override Assembly? Load(AssemblyName assemblyName) => null;
    }

    private static void TestLoaderIsolatedGetTypes()
    {
        string? repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        string loaderOutDir = Path.Combine(repoRoot, "bin", "Release", "net9.0");
        string loaderDll = Path.Combine(loaderOutDir, "RunResumer.dll");
        Assert(File.Exists(loaderDll), $"Expected {loaderDll} to exist");
        Assert(File.Exists(Path.Combine(loaderOutDir, "RunResumer.Core.dll")), "Expected RunResumer.Core.dll in loader output");
        Assert(File.Exists(Path.Combine(loaderOutDir, "Google.Protobuf.dll")), "Expected Google.Protobuf.dll in loader output");

        // Verify RunResumer.dll has zero static reference to Google.Protobuf or RunResumer.Core
        IsolatedModLoadContext alc = new IsolatedModLoadContext();
        Assembly loaded = alc.LoadFromAssemblyPath(loaderDll);
        string[] referenced = loaded.GetReferencedAssemblies().Select(a => a.Name ?? "").ToArray();
        Assert(!referenced.Contains("Google.Protobuf"), "RunResumer.dll must not statically reference Google.Protobuf");
        Assert(!referenced.Contains("RunResumer.Core"), "RunResumer.dll must not statically reference RunResumer.Core");

        // Simulates ModManager.TryLoadMod calling GetTypes() before any initializer runs
        Type[] types = loaded.GetTypes();
        Type? entryType = types.FirstOrDefault(t => t.GetCustomAttribute<ModInitializerAttribute>() != null);
        Assert(entryType != null, "ModInitializerAttribute not found on RunResumer.dll");

        // Hook resolver (as RunResumerEntry.Initialize does) and load RunResumer.Core.dll inside the same ALC
        alc.Resolving += (_, name) =>
        {
            string candidate = Path.Combine(loaderOutDir, name.Name + ".dll");
            return File.Exists(candidate) ? alc.LoadFromAssemblyPath(candidate) : null;
        };
        Assembly coreLoaded = alc.LoadFromAssemblyPath(Path.Combine(loaderOutDir, "RunResumer.Core.dll"));
        Type[] coreTypes = coreLoaded.GetTypes();
        Assert(coreTypes.Any(t => t.Name == "RunArchive"), "RunArchive type missing from RunResumer.Core.dll");
        using (Stream? iconStream = coreLoaded.GetManifestResourceStream("RunResumer.Assets.run_resumer_icon.png"))
        {
            Assert(iconStream != null, "Embedded icon resource RunResumer.Assets.run_resumer_icon.png missing from RunResumer.Core.dll");
            byte[] header = new byte[4];
            int read = iconStream!.Read(header, 0, 4);
            Assert(read == 4 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47,
                "Embedded icon resource is not a valid PNG");
        }
        alc.Unload();

        Console.WriteLine("[PASS] Verified RunResumer.dll loads cleanly via ModManager.GetTypes() + isolated AssemblyLoadContext");
    }

    private static void TestHarmonyPatchTargetsResolve()
    {
        Assembly modAssembly = typeof(RunResumerCoreEntry).Assembly;
        int checkedPatches = 0;

        foreach (Type type in modAssembly.GetTypes())
        {
            List<HarmonyMethod> infos = HarmonyMethodExtensions.GetFromType(type);
            if (infos == null || infos.Count == 0)
            {
                continue;
            }

            HarmonyMethod merged = HarmonyMethod.Merge(infos);
            Assert(merged.declaringType != null, $"Patch {type.FullName} missing declaringType");
            Assert(!string.IsNullOrEmpty(merged.methodName), $"Patch {type.FullName} missing methodName");

            MethodBase? target = merged.methodType switch
            {
                MethodType.Getter => AccessTools.PropertyGetter(merged.declaringType, merged.methodName),
                MethodType.Setter => AccessTools.PropertySetter(merged.declaringType, merged.methodName),
                MethodType.Constructor => AccessTools.Constructor(merged.declaringType, merged.argumentTypes),
                _ => AccessTools.Method(merged.declaringType, merged.methodName, merged.argumentTypes)
            };

            Assert(target != null, $"Could not resolve Harmony target {merged.declaringType?.FullName}.{merged.methodName} on {type.FullName}");

            // Verify patch method parameter names/types exist on the patch class.
            MethodInfo? prefix = AccessTools.Method(type, "Prefix");
            MethodInfo? postfix = AccessTools.Method(type, "Postfix");
            Assert(prefix != null || postfix != null, $"Patch {type.FullName} has neither Prefix nor Postfix");
            checkedPatches++;
        }

        Assert(checkedPatches == 18, $"Expected 18 patch classes, checked {checkedPatches}");
        Console.WriteLine($"[PASS] Verified all {checkedPatches} Harmony patch targets resolve against sts2.dll");
    }

    private static void TestRoundTripAndCompression()
    {
        const int totalFloors = 50;
        List<SerializableRun> originalFloors = new();
        List<List<MapPointHistoryEntry>> fullMapPointHistory = new()
        {
            new List<MapPointHistoryEntry>(),
            new List<MapPointHistoryEntry>(),
            new List<MapPointHistoryEntry>()
        };

        SerializableRun? current = null;
        long totalRawJsonBytes = 0;

        for (int floor = 1; floor <= totalFloors; floor++)
        {
            int actIdx = floor <= 16 ? 0 : (floor <= 33 ? 1 : 2);
            current = BuildFloorRun(floor, actIdx, fullMapPointHistory);
            originalFloors.Add(current);

            string json = JsonSerializationUtility.ToJson(current);
            totalRawJsonBytes += Encoding.UTF8.GetByteCount(json);

            // Simulate appending floor to history after room completion
            fullMapPointHistory[actIdx].Add(new MapPointHistoryEntry
            {
                MapPointType = floor % 5 == 0 ? MapPointType.Elite : MapPointType.Monster,
                Rooms = new List<MapPointRoomHistoryEntry>
                {
                    new MapPointRoomHistoryEntry
                    {
                        RoomType = RoomType.Monster,
                        ModelId = new ModelId("ENCOUNTER", "SLIMES_SMALL")
                    }
                },
                PlayerStats = new List<PlayerMapPointHistoryEntry>
                {
                    new PlayerMapPointHistoryEntry
                    {
                        PlayerId = 1uL,
                        CurrentHp = 70 - (floor % 12),
                        MaxHp = 80 + actIdx * 5,
                        CurrentGold = 99 + floor * 15
                    }
                }
            });
        }

        RunArchive archive = RunArchiveCodec.CreateNewArchive(originalFloors[0]);
        for (int i = 0; i < originalFloors.Count; i++)
        {
            RunArchiveCodec.UpsertFloor(archive, originalFloors[i], i + 1);
        }

        // Verify reload truncation: re-upserting floor 25 drops floors 26..50, then re-adding restores 50.
        RunArchiveCodec.UpsertFloor(archive, originalFloors[24], 25);
        Assert(archive.Floors.Count == 25, $"Expected 25 floors after truncation, got {archive.Floors.Count}");
        for (int i = 25; i < originalFloors.Count; i++)
        {
            RunArchiveCodec.UpsertFloor(archive, originalFloors[i], i + 1);
        }

        Stopwatch sw = Stopwatch.StartNew();
        byte[] compressed = RunArchiveCodec.SerializeCompressed(archive);
        RunArchive decodedArchive = RunArchiveCodec.DeserializeCompressed(compressed);
        sw.Stop();

        RunHistory fullHistory = new RunHistory
        {
            StartTime = originalFloors[0].StartTime,
            MapPointHistory = fullMapPointHistory
        };

        for (int floor = 1; floor <= totalFloors; floor++)
        {
            SerializableRun expected = originalFloors[floor - 1];
            SerializableRun actual = RunArchiveCodec.ReconstructRun(decodedArchive, floor, fullHistory);

            string expectedJson = JsonSerializationUtility.ToJson(expected);
            string actualJson = JsonSerializationUtility.ToJson(actual);
            if (!string.Equals(expectedJson, actualJson, StringComparison.Ordinal))
            {
                throw new Exception($"Round-trip JSON mismatch at floor {floor}!\nExpected:\n{expectedJson}\nActual:\n{actualJson}");
            }
        }

        double ratio = (double)compressed.Length / totalRawJsonBytes * 100.0;
        Console.WriteLine(
            $"[PASS] 50-floor round-trip exact JSON match! Raw JSON total={totalRawJsonBytes:N0} bytes, " +
            $"Compressed Protobuf={compressed.Length:N0} bytes ({ratio:F2}% of raw, {sw.ElapsedMilliseconds} ms round-trip)");
    }

    private static void TestShareExportImportAndValidation()
    {
        List<List<MapPointHistoryEntry>> fullMapPointHistory = new()
        {
            new List<MapPointHistoryEntry>(),
            new List<MapPointHistoryEntry>(),
            new List<MapPointHistoryEntry>()
        };

        List<SerializableRun> floors = new();
        for (int floor = 1; floor <= 5; floor++)
        {
            SerializableRun run = BuildFloorRun(floor, 0, fullMapPointHistory);
            floors.Add(run);
            fullMapPointHistory[0].Add(new MapPointHistoryEntry
            {
                MapPointType = MapPointType.Monster,
                Rooms = new List<MapPointRoomHistoryEntry>
                {
                    new MapPointRoomHistoryEntry
                    {
                        RoomType = RoomType.Monster,
                        ModelId = new ModelId("ENCOUNTER", "SLIMES_SMALL")
                    }
                },
                PlayerStats = new List<PlayerMapPointHistoryEntry>
                {
                    new PlayerMapPointHistoryEntry
                    {
                        PlayerId = 1uL,
                        CurrentHp = 70,
                        MaxHp = 80,
                        CurrentGold = 99 + floor * 10
                    }
                }
            });
        }

        RunArchive archive = RunArchiveCodec.CreateNewArchive(floors[0]);
        for (int i = 0; i < floors.Count; i++)
        {
            RunArchiveCodec.UpsertFloor(archive, floors[i], i + 1);
        }

        RunHistory history = new RunHistory
        {
            SchemaVersion = 11,
            StartTime = floors[0].StartTime,
            Seed = "TESTSEED123",
            Ascension = 10,
            BuildId = "v0.113.1",
            Players = new List<RunHistoryPlayer>
            {
                new RunHistoryPlayer
                {
                    Id = 1uL,
                    Character = new ModelId("CHARACTER", "IRONCLAD")
                }
            },
            MapPointHistory = fullMapPointHistory
        };

        byte[] shareBytes = RunShareService.CreateShareBytes(history, archive);

        ShareImportPayload parsed = RunShareService.ValidateAndParseShare(
            shareBytes,
            expectedRunSchemaVersion: floors[0].SchemaVersion,
            maxHistorySchemaVersion: 11,
            isModelKnown: _ => true);

        Assert(parsed.FloorCount == 5, $"Expected 5 floors in parsed share, got {parsed.FloorCount}");
        Assert(parsed.History.StartTime == history.StartTime, "Parsed history StartTime mismatch");
        Assert(parsed.Archive.StartTime == archive.StartTime, "Parsed archive StartTime mismatch");

        for (int floor = 1; floor <= 5; floor++)
        {
            string expectedJson = JsonSerializationUtility.ToJson(floors[floor - 1]);
            string actualJson = JsonSerializationUtility.ToJson(
                RunArchiveCodec.ReconstructRun(parsed.Archive, floor, parsed.History));
            Assert(string.Equals(expectedJson, actualJson, StringComparison.Ordinal), $"Share round-trip mismatch on floor {floor}");
        }

        // Verify StartTime collision resolution updates both RunHistory and RunArchive cleanly.
        long collidedStartTime = RunShareService.ResolveUniqueStartTime(
            parsed.History.StartTime,
            candidate => candidate == history.StartTime || candidate == history.StartTime + 1);
        Assert(collidedStartTime == history.StartTime + 2, $"Expected StartTime+2 on collision, got {collidedStartTime}");

        RunHistory renamedHistory = RunShareService.ApplyStartTime(parsed.RawRunJson, parsed.Archive, collidedStartTime);
        Assert(renamedHistory.StartTime == collidedStartTime, "Renamed RunHistory StartTime mismatch");
        Assert(parsed.Archive.StartTime == collidedStartTime, "Renamed RunArchive StartTime mismatch");
        SerializableRun renamedReconstructed = RunArchiveCodec.ReconstructRun(parsed.Archive, 3, renamedHistory);
        Assert(renamedReconstructed.StartTime == collidedStartTime, "Reconstructed run StartTime mismatch after collision rename");

        // Verify missing ModelId rejection.
        ExpectShareRejection(
            () => RunShareService.ValidateAndParseShare(
                shareBytes,
                expectedRunSchemaVersion: floors[0].SchemaVersion,
                maxHistorySchemaVersion: 11,
                isModelKnown: id => id.Entry != "REWARD_CARD_10"),
            "missing content");

        // Verify schema mismatch rejection.
        ExpectShareRejection(
            () => RunShareService.ValidateAndParseShare(
                shareBytes,
                expectedRunSchemaVersion: floors[0].SchemaVersion + 1,
                maxHistorySchemaVersion: 11,
                isModelKnown: _ => true),
            "schema");

        // Verify corrupt archive rejection.
        ExpectShareRejection(
            () => RunShareService.ValidateAndParseShare(
                new byte[] { 1, 2, 3, 4 },
                expectedRunSchemaVersion: floors[0].SchemaVersion,
                maxHistorySchemaVersion: 11,
                isModelKnown: _ => true),
            "corrupted");

        string defaultFileName = RunShareService.BuildDefaultFileName(history);
        Assert(defaultFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase), $"Expected .zip default filename, got {defaultFileName}");

        Console.WriteLine("[PASS] Verified .zip export/import round-trip, collision handling, and validation rejections");
    }

    private static void ExpectShareRejection(Action action, string expectedSubstring)
    {
        try
        {
            action();
        }
        catch (RunShareValidationException ex)
        {
            Assert(
                ex.Message.Contains(expectedSubstring, StringComparison.OrdinalIgnoreCase),
                $"Expected rejection message containing '{expectedSubstring}', got: {ex.Message}");
            return;
        }
        throw new Exception($"Expected RunShareValidationException containing '{expectedSubstring}', but no exception was thrown");
    }

    private static SerializableRun BuildFloorRun(
        int floor,
        int actIdx,
        List<List<MapPointHistoryEntry>> historySoFar)
    {
        List<SerializableActModel> acts = new();
        for (int a = 0; a < 3; a++)
        {
            acts.Add(new SerializableActModel
            {
                Id = new ModelId("ACT", $"ACT_{a + 1}"),
                SerializableRooms = new SerializableRoomSet
                {
                    EventIds = Enumerable.Range(0, 12).Select(i => new ModelId("EVENT", $"EVENT_{i}")).ToList(),
                    EventsVisited = a < actIdx ? 4 : (a == actIdx ? floor / 6 : 0),
                    NormalEncounterIds = Enumerable.Range(0, 14).Select(i => new ModelId("ENCOUNTER", $"MONSTER_{i}")).ToList(),
                    NormalEncountersVisited = a < actIdx ? 7 : (a == actIdx ? floor / 3 : 0),
                    EliteEncounterIds = Enumerable.Range(0, 5).Select(i => new ModelId("ENCOUNTER", $"ELITE_{i}")).ToList(),
                    EliteEncountersVisited = a < actIdx ? 2 : (a == actIdx ? floor / 10 : 0),
                    BossEncountersVisited = a < actIdx ? 1 : 0,
                    BossId = new ModelId("ENCOUNTER", $"BOSS_{a + 1}"),
                    SecondBossId = a == 2 ? new ModelId("ENCOUNTER", "BOSS_4") : null,
                    AncientId = new ModelId("EVENT", $"ANCIENT_{a + 1}")
                },
                SavedMap = a == actIdx ? BuildMap() : null
            });
        }

        List<SerializableCard> deck = new();
        int cardCount = 10 + floor / 3;
        for (int c = 0; c < cardCount; c++)
        {
            SerializableCard card = new SerializableCard
            {
                Id = new ModelId("CARD", c < 5 ? "STRIKE_IRONCLAD" : (c < 9 ? "DEFEND_IRONCLAD" : $"REWARD_CARD_{c}")),
                CurrentUpgradeLevel = c % 4 == 0 ? 1 : 0,
                FloorAddedToDeck = c >= 10 ? (c - 9) * 3 : null
            };
            if (c == 10)
            {
                card.Enchantment = new SerializableEnchantment
                {
                    Id = new ModelId("ENCHANTMENT", "SHARP"),
                    Amount = 2,
                    Props = new SavedProperties
                    {
                        ints = new List<SavedProperties.SavedProperty<int>> { new("Bonus", 3) }
                    }
                };
            }
            if (c == 2)
            {
                card.Props = new SavedProperties
                {
                    ints = new List<SavedProperties.SavedProperty<int>> { new("TimesPlayed", floor) },
                    bools = new List<SavedProperties.SavedProperty<bool>> { new("Activated", true) }
                };
            }
            deck.Add(card);
        }

        List<SerializableRelic> relics = new()
        {
            new SerializableRelic { Id = new ModelId("RELIC", "BURNING_BLOOD"), FloorAddedToDeck = 1 }
        };
        for (int r = 1; r <= floor / 5; r++)
        {
            relics.Add(new SerializableRelic
            {
                Id = new ModelId("RELIC", $"TEST_RELIC_{r}"),
                FloorAddedToDeck = r * 5,
                Props = r == 1
                    ? new SavedProperties { ints = new List<SavedProperties.SavedProperty<int>> { new("Counter", floor % 7) } }
                    : null
            });
        }

        List<SerializablePotion> potions = new()
        {
            new SerializablePotion { Id = new ModelId("POTION", "FIRE_POTION"), SlotIndex = 0 }
        };
        if (floor % 2 == 0)
        {
            potions.Add(new SerializablePotion { Id = new ModelId("POTION", "BLOCK_POTION"), SlotIndex = 1 });
        }

        List<List<MapPointHistoryEntry>> slicedHistory = new();
        foreach (List<MapPointHistoryEntry> actEntries in historySoFar)
        {
            if (actEntries.Count > 0)
            {
                slicedHistory.Add(actEntries.ToList());
            }
        }

        int actStartFloor = actIdx == 0 ? 1 : (actIdx == 1 ? 17 : 34);
        List<MapCoord> visitedCoords = new();
        for (int f = actStartFloor; f <= floor; f++)
        {
            visitedCoords.Add(new MapCoord((f - actStartFloor) % 7, f - actStartFloor));
        }

        return new SerializableRun
        {
            SchemaVersion = 21,
            Acts = acts,
            Modifiers = new List<SerializableModifier>
            {
                new SerializableModifier { Id = new ModelId("MODIFIER", "DRAFT") }
            },
            DailyTime = DateTimeOffset.FromUnixTimeSeconds(1760000000L),
            CurrentActIndex = actIdx,
            EventsSeen = Enumerable.Range(0, floor / 7).Select(i => new ModelId("EVENT", $"SEEN_{i}")).ToList(),
            PreFinishedRoom = null,
            SerializableOdds = new SerializableRunOddsSet
            {
                UnknownMapPointMonsterOddsValue = 0.1f + (floor % 4) * 0.1f,
                UnknownMapPointEliteOddsValue = 0.0f,
                UnknownMapPointTreasureOddsValue = 0.02f,
                UnknownMapPointShopOddsValue = 0.03f
            },
            SerializableSharedRelicGrabBag = BuildGrabBag(floor / 5),
            Players = new List<SerializablePlayer>
            {
                new SerializablePlayer
                {
                    NetId = 1uL,
                    CharacterId = new ModelId("CHARACTER", "IRONCLAD"),
                    CurrentHp = 72 - (floor % 15),
                    MaxHp = 80 + actIdx * 5,
                    MaxEnergy = 3,
                    MaxPotionSlotCount = 3,
                    Gold = 99 + floor * 15,
                    BaseOrbSlotCount = 0,
                    Deck = deck,
                    Relics = relics,
                    Potions = potions,
                    Rng = new SerializablePlayerRngSet
                    {
                        Seed = 123456789UL,
                        Rngs = new Dictionary<PlayerRngType, SerializableRng>
                        {
                            [PlayerRngType.Rewards] = new SerializableRng { counter = floor * 3, state0 = 11, state1 = 22, state2 = 33, state3 = 44 },
                            [PlayerRngType.Shops] = new SerializableRng { counter = floor, state0 = 55, state1 = 66, state2 = 77, state3 = 88 }
                        }
                    },
                    Odds = new SerializablePlayerOddsSet
                    {
                        CardRarityOddsValue = 0.03f + (floor % 5) * 0.01f,
                        PotionRewardOddsValue = 0.4f
                    },
                    RelicGrabBag = BuildGrabBag(floor / 10),
                    ExtraFields = new SerializableExtraPlayerFields
                    {
                        CardShopRemovalsUsed = floor / 15,
                        DamageDealt = floor * 42
                    },
                    UnlockState = new SerializableUnlockState
                    {
                        UnlockedEpochs = new List<string> { "NEOW_EPOCH", "IRONCLAD_EPOCH" },
                        EncountersSeen = new List<ModelId> { new("ENCOUNTER", "SLIMES_SMALL") },
                        NumberOfRuns = 12
                    },
                    DiscoveredCards = new List<ModelId> { new("CARD", "BASH") },
                    DiscoveredEnemies = new List<ModelId> { new("MONSTER", "JAW_WORM") },
                    DiscoveredEpochs = new List<string>(),
                    DiscoveredPotions = new List<ModelId> { new("POTION", "FIRE_POTION") },
                    DiscoveredRelics = new List<ModelId> { new("RELIC", "BURNING_BLOOD") }
                }
            },
            SerializableRng = new SerializableRunRngSet
            {
                Seed = "TESTSEED123",
                Rngs = new Dictionary<RunRngType, SerializableRng>
                {
                    [RunRngType.UpFront] = new SerializableRng { counter = 100, state0 = 1, state1 = 2, state2 = 3, state3 = 4 },
                    [RunRngType.MonsterAi] = new SerializableRng { counter = floor * 2, state0 = 5, state1 = 6, state2 = 7, state3 = 8 }
                }
            },
            VisitedMapCoords = visitedCoords,
            MapPointHistory = slicedHistory,
            SaveTime = 1760001000L + floor * 60,
            StartTime = 1760000500L,
            RunTime = floor * 45L,
            WinTime = 0L,
            Ascension = 10,
            NumReloads = 0,
            PlatformType = PlatformType.Steam,
            MapDrawings = new SerializableMapDrawings
            {
                drawings = new List<SerializablePlayerMapDrawings>
                {
                    new SerializablePlayerMapDrawings
                    {
                        playerId = 1uL,
                        lines = new List<SerializableMapDrawingLine>
                        {
                            new SerializableMapDrawingLine
                            {
                                isEraser = false,
                                mapPoints = new List<Vector2> { new(0.1f, 0.2f), new(0.3f, 0.4f) }
                            }
                        }
                    }
                }
            },
            ExtraFields = new SerializableExtraRunFields
            {
                StartedWithNeow = true,
                TestSubjectKills = 0,
                FreedRepy = false,
                UsedDebugCommand = false
            },
            GameMode = GameMode.Standard,
            IsCustomGame = false
        };
    }

    private static SerializableActMap BuildMap()
    {
        List<SerializableMapPoint> points = new();
        for (int r = 0; r < 15; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                points.Add(new SerializableMapPoint
                {
                    Coord = new MapCoord(c, r),
                    PointType = (MapPointType)((r + c) % 6 + 1),
                    CanBeModified = true,
                    ChildCoords = r < 14
                        ? new List<MapCoord> { new(c, r + 1) }
                        : new List<MapCoord> { new(0, 15) }
                });
            }
        }

        return new SerializableActMap
        {
            Points = points,
            BossPoint = new SerializableMapPoint
            {
                Coord = new MapCoord(0, 15),
                PointType = MapPointType.Boss,
                CanBeModified = false
            },
            StartingPoint = new SerializableMapPoint
            {
                Coord = new MapCoord(0, -1),
                PointType = MapPointType.Ancient,
                CanBeModified = false,
                ChildCoords = new List<MapCoord> { new(0, 0), new(1, 0) }
            },
            StartMapPointCoords = new List<MapCoord> { new(0, 0), new(1, 0) },
            GridWidth = 7,
            GridHeight = 15
        };
    }

    private static SerializableRelicGrabBag BuildGrabBag(int offset)
    {
        return new SerializableRelicGrabBag
        {
            RelicIdLists = new Dictionary<RelicRarity, List<ModelId>>
            {
                [RelicRarity.Common] = Enumerable.Range(offset, Math.Max(1, 20 - offset))
                    .Select(i => new ModelId("RELIC", $"COMMON_{i}"))
                    .ToList(),
                [RelicRarity.Uncommon] = Enumerable.Range(0, 15)
                    .Select(i => new ModelId("RELIC", $"UNCOMMON_{i}"))
                    .ToList(),
                [RelicRarity.Rare] = Enumerable.Range(0, 12)
                    .Select(i => new ModelId("RELIC", $"RARE_{i}"))
                    .ToList()
            }
        };
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception("Assertion failed: " + message);
        }
    }
}
