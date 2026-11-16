using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Godot;
using Google.Protobuf;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.MapDrawing;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Unlocks;
using RunResumer.Proto;

namespace RunResumer.Storage;

internal static class RunArchiveCodec
{
    public const int CurrentArchiveVersion = 1;

    public static byte[] SerializeCompressed(RunArchive archive)
    {
        using MemoryStream output = new MemoryStream();
        using (BrotliStream brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            archive.WriteTo(brotli);
        }
        return output.ToArray();
    }

    public static RunArchive DeserializeCompressed(byte[] compressedBytes)
    {
        using MemoryStream input = new MemoryStream(compressedBytes);
        using BrotliStream brotli = new BrotliStream(input, CompressionMode.Decompress);
        return RunArchive.Parser.ParseFrom(brotli);
    }

    public static RunArchive CreateNewArchive(SerializableRun run)
    {
        SerializablePlayer player = run.Players[0];
        RunArchive archive = new RunArchive
        {
            ArchiveFormatVersion = CurrentArchiveVersion,
            SchemaVersion = run.SchemaVersion,
            StartTime = run.StartTime,
            GameMode = (int)run.GameMode,
            IsCustomGame = run.IsCustomGame,
            Ascension = run.Ascension,
            PlatformType = (int)run.PlatformType,
            HasDailyTime = run.DailyTime.HasValue,
            DailyTimeUnixSeconds = run.DailyTime?.ToUnixTimeSeconds() ?? 0L,
            RngSeed = run.SerializableRng?.Seed ?? string.Empty,
            PlayerNetId = player.NetId,
            PlayerRngSeed = player.Rng?.Seed ?? 0UL
        };

        StringTableBuilder modelIds = new StringTableBuilder(archive.ModelIds);
        StringTableBuilder propNames = new StringTableBuilder(archive.PropNames);
        archive.PlayerCharacterIdIdx = InternModelId(modelIds, player.CharacterId);

        foreach (SerializableModifier modifier in run.Modifiers)
        {
            archive.Modifiers.Add(new PbModifier
            {
                IdIdx = InternModelId(modelIds, modifier.Id),
                Props = EncodeSavedProperties(modifier.Props, modelIds, propNames)
            });
        }

        return archive;
    }

    public static void UpsertFloor(RunArchive archive, SerializableRun run, int floorNum)
    {
        if (run.Players.Count != 1)
        {
            throw new InvalidOperationException($"RunResumer only supports singleplayer runs (got {run.Players.Count} players).");
        }

        archive.SchemaVersion = run.SchemaVersion;

        StringTableBuilder modelIds = new StringTableBuilder(archive.ModelIds);
        StringTableBuilder propNames = new StringTableBuilder(archive.PropNames);
        StringTableBuilder epochIds = new StringTableBuilder(archive.EpochIds);

        SerializablePlayer player = run.Players[0];

        PbFloorSnapshot snapshot = new PbFloorSnapshot
        {
            FloorNum = floorNum,
            CurrentActIndex = run.CurrentActIndex,
            Odds = new PbRunOddsSet
            {
                UnknownMonsterOdds = run.SerializableOdds.UnknownMapPointMonsterOddsValue,
                UnknownEliteOdds = run.SerializableOdds.UnknownMapPointEliteOddsValue,
                UnknownTreasureOdds = run.SerializableOdds.UnknownMapPointTreasureOddsValue,
                UnknownShopOdds = run.SerializableOdds.UnknownMapPointShopOddsValue
            },
            SharedGrabBagRefIdx = InternPoolItem(
                archive.GrabBagsPool,
                EncodeRelicGrabBag(run.SerializableSharedRelicGrabBag, modelIds)),
            SaveTime = run.SaveTime,
            RunTime = run.RunTime,
            WinTime = run.WinTime,
            NumReloads = run.NumReloads,
            DrawingsRefIdx = run.MapDrawings == null
                ? 0u
                : InternPoolItem(archive.DrawingsPool, EncodeMapDrawings(run.MapDrawings)),
            ExtraRunFields = new PbExtraRunFields
            {
                StartedWithNeow = run.ExtraFields.StartedWithNeow,
                TestSubjectKills = run.ExtraFields.TestSubjectKills,
                FreedRepy = run.ExtraFields.FreedRepy,
                UsedDebugCommand = run.ExtraFields.UsedDebugCommand
            },
            DiscoveriesRefIdx = InternPoolItem(
                archive.DiscoveriesPool,
                EncodeDiscoveries(run.EventsSeen, player, modelIds, epochIds))
        };

        foreach (SerializableActModel act in run.Acts)
        {
            PbActStatic actStatic = new PbActStatic
            {
                ActIdIdx = InternModelId(modelIds, act.Id),
                BossIdIdx = InternModelId(modelIds, act.SerializableRooms.BossId),
                SecondBossIdIdx = InternModelId(modelIds, act.SerializableRooms.SecondBossId),
                AncientIdIdx = InternModelId(modelIds, act.SerializableRooms.AncientId)
            };
            foreach (ModelId id in act.SerializableRooms.EventIds)
            {
                actStatic.EventIdsIdx.Add(InternModelId(modelIds, id));
            }
            foreach (ModelId id in act.SerializableRooms.NormalEncounterIds)
            {
                actStatic.NormalEncounterIdsIdx.Add(InternModelId(modelIds, id));
            }
            foreach (ModelId id in act.SerializableRooms.EliteEncounterIds)
            {
                actStatic.EliteEncounterIdsIdx.Add(InternModelId(modelIds, id));
            }

            snapshot.Acts.Add(new PbActState
            {
                ActStaticRefIdx = InternPoolItem(archive.ActStaticsPool, actStatic),
                EventsVisited = act.SerializableRooms.EventsVisited,
                NormalEncountersVisited = act.SerializableRooms.NormalEncountersVisited,
                EliteEncountersVisited = act.SerializableRooms.EliteEncountersVisited,
                BossEncountersVisited = act.SerializableRooms.BossEncountersVisited,
                SavedMapRefIdx = act.SavedMap == null
                    ? 0u
                    : InternPoolItem(archive.MapsPool, EncodeActMap(act.SavedMap))
            });
        }

        if (run.SerializableRng?.Rngs != null)
        {
            foreach (KeyValuePair<RunRngType, SerializableRng> kvp in run.SerializableRng.Rngs.OrderBy(k => (int)k.Key))
            {
                snapshot.RunRngs.Add(new PbRngEntry
                {
                    RngType = (int)kvp.Key,
                    Counter = kvp.Value.counter,
                    S0 = kvp.Value.state0,
                    S1 = kvp.Value.state1,
                    S2 = kvp.Value.state2,
                    S3 = kvp.Value.state3
                });
            }
        }

        foreach (MapCoord coord in run.VisitedMapCoords)
        {
            snapshot.VisitedMapCoords.Add(new PbMapCoord { Col = coord.col, Row = coord.row });
        }

        PbPlayerSnapshot pbPlayer = new PbPlayerSnapshot
        {
            CurrentHp = player.CurrentHp,
            MaxHp = player.MaxHp,
            MaxEnergy = player.MaxEnergy,
            MaxPotionSlotCount = player.MaxPotionSlotCount,
            Gold = player.Gold,
            BaseOrbSlotCount = player.BaseOrbSlotCount,
            DeckRefIdx = InternPoolItem(archive.DecksPool, EncodeDeck(player.Deck, modelIds, propNames)),
            RelicsRefIdx = InternPoolItem(archive.RelicsPool, EncodeRelics(player.Relics, modelIds, propNames)),
            PotionsRefIdx = InternPoolItem(archive.PotionsPool, EncodePotions(player.Potions, modelIds)),
            Odds = new PbPlayerOddsSet
            {
                CardRarityOdds = player.Odds.CardRarityOddsValue,
                PotionRewardOdds = player.Odds.PotionRewardOddsValue
            },
            RelicGrabBagRefIdx = InternPoolItem(
                archive.GrabBagsPool,
                EncodeRelicGrabBag(player.RelicGrabBag, modelIds)),
            ExtraFields = new PbExtraPlayerFields
            {
                CardShopRemovalsUsed = player.ExtraFields.CardShopRemovalsUsed,
                WongoPoints = player.ExtraFields.WongoPoints,
                CccomboBadgeUnlocked = player.ExtraFields.CccomboBadgeUnlocked,
                DamageDealt = player.ExtraFields.DamageDealt,
                DebuffsApplied = player.ExtraFields.DebuffsApplied,
                IsPlayingRotatingCharacter = player.ExtraFields.IsPlayingRotatingCharacter
            }
        };

        if (player.Rng?.Rngs != null)
        {
            foreach (KeyValuePair<PlayerRngType, SerializableRng> kvp in player.Rng.Rngs.OrderBy(k => (int)k.Key))
            {
                pbPlayer.PlayerRngs.Add(new PbRngEntry
                {
                    RngType = (int)kvp.Key,
                    Counter = kvp.Value.counter,
                    S0 = kvp.Value.state0,
                    S1 = kvp.Value.state1,
                    S2 = kvp.Value.state2,
                    S3 = kvp.Value.state3
                });
            }
        }

        snapshot.Player = pbPlayer;

        // If the player reloaded a floor and re-entered it (or overwrote an existing floor), truncate >= floorNum.
        for (int i = archive.Floors.Count - 1; i >= 0; i--)
        {
            if (archive.Floors[i].FloorNum >= floorNum)
            {
                archive.Floors.RemoveAt(i);
            }
        }
        archive.Floors.Add(snapshot);
    }

    public static SerializableRun ReconstructRun(RunArchive archive, int floorNum, RunHistory history)
    {
        PbFloorSnapshot? snapshot = archive.Floors.FirstOrDefault(f => f.FloorNum == floorNum);
        if (snapshot == null)
        {
            throw new KeyNotFoundException($"Floor {floorNum} not found in archive for run {archive.StartTime}.");
        }

        List<SerializableActModel> acts = new List<SerializableActModel>(snapshot.Acts.Count);
        foreach (PbActState actState in snapshot.Acts)
        {
            PbActStatic actStatic = archive.ActStaticsPool[(int)actState.ActStaticRefIdx - 1];
            SerializableRoomSet rooms = new SerializableRoomSet
            {
                EventIds = actStatic.EventIdsIdx.Select(idx => DecodeModelId(archive.ModelIds, idx)!).ToList(),
                EventsVisited = actState.EventsVisited,
                NormalEncounterIds = actStatic.NormalEncounterIdsIdx.Select(idx => DecodeModelId(archive.ModelIds, idx)!).ToList(),
                NormalEncountersVisited = actState.NormalEncountersVisited,
                EliteEncounterIds = actStatic.EliteEncounterIdsIdx.Select(idx => DecodeModelId(archive.ModelIds, idx)!).ToList(),
                EliteEncountersVisited = actState.EliteEncountersVisited,
                BossEncountersVisited = actState.BossEncountersVisited,
                BossId = DecodeModelId(archive.ModelIds, actStatic.BossIdIdx),
                SecondBossId = DecodeModelId(archive.ModelIds, actStatic.SecondBossIdIdx),
                AncientId = DecodeModelId(archive.ModelIds, actStatic.AncientIdIdx)
            };

            acts.Add(new SerializableActModel
            {
                Id = DecodeModelId(archive.ModelIds, actStatic.ActIdIdx),
                SerializableRooms = rooms,
                SavedMap = actState.SavedMapRefIdx == 0
                    ? null
                    : DecodeActMap(archive.MapsPool[(int)actState.SavedMapRefIdx - 1])
            });
        }

        List<SerializableModifier> modifiers = archive.Modifiers.Select(m => new SerializableModifier
        {
            Id = DecodeModelId(archive.ModelIds, m.IdIdx),
            Props = DecodeSavedProperties(m.Props, archive.ModelIds, archive.PropNames)
        }).ToList();

        SerializableRunRngSet runRng = new SerializableRunRngSet
        {
            Seed = archive.RngSeed
        };
        foreach (PbRngEntry entry in snapshot.RunRngs)
        {
            runRng.Rngs[(RunRngType)entry.RngType] = new SerializableRng
            {
                counter = entry.Counter,
                state0 = entry.S0,
                state1 = entry.S1,
                state2 = entry.S2,
                state3 = entry.S3
            };
        }

        PbDiscoverySnapshot discoveries = archive.DiscoveriesPool[(int)snapshot.DiscoveriesRefIdx - 1];
        PbPlayerSnapshot pbPlayer = snapshot.Player;

        SerializablePlayerRngSet playerRng = new SerializablePlayerRngSet
        {
            Seed = archive.PlayerRngSeed
        };
        foreach (PbRngEntry entry in pbPlayer.PlayerRngs)
        {
            playerRng.Rngs[(PlayerRngType)entry.RngType] = new SerializableRng
            {
                counter = entry.Counter,
                state0 = entry.S0,
                state1 = entry.S1,
                state2 = entry.S2,
                state3 = entry.S3
            };
        }

        SerializablePlayer player = new SerializablePlayer
        {
            NetId = archive.PlayerNetId,
            CharacterId = DecodeModelId(archive.ModelIds, archive.PlayerCharacterIdIdx),
            CurrentHp = pbPlayer.CurrentHp,
            MaxHp = pbPlayer.MaxHp,
            MaxEnergy = pbPlayer.MaxEnergy,
            MaxPotionSlotCount = pbPlayer.MaxPotionSlotCount,
            Gold = pbPlayer.Gold,
            BaseOrbSlotCount = pbPlayer.BaseOrbSlotCount,
            Deck = DecodeDeck(archive.DecksPool[(int)pbPlayer.DeckRefIdx - 1], archive.ModelIds, archive.PropNames),
            Relics = DecodeRelics(archive.RelicsPool[(int)pbPlayer.RelicsRefIdx - 1], archive.ModelIds, archive.PropNames),
            Potions = DecodePotions(archive.PotionsPool[(int)pbPlayer.PotionsRefIdx - 1], archive.ModelIds),
            Rng = playerRng,
            Odds = new SerializablePlayerOddsSet
            {
                CardRarityOddsValue = pbPlayer.Odds.CardRarityOdds,
                PotionRewardOddsValue = pbPlayer.Odds.PotionRewardOdds
            },
            RelicGrabBag = DecodeRelicGrabBag(archive.GrabBagsPool[(int)pbPlayer.RelicGrabBagRefIdx - 1], archive.ModelIds),
            ExtraFields = new SerializableExtraPlayerFields
            {
                CardShopRemovalsUsed = pbPlayer.ExtraFields.CardShopRemovalsUsed,
                WongoPoints = pbPlayer.ExtraFields.WongoPoints,
                CccomboBadgeUnlocked = pbPlayer.ExtraFields.CccomboBadgeUnlocked,
                DamageDealt = pbPlayer.ExtraFields.DamageDealt,
                DebuffsApplied = pbPlayer.ExtraFields.DebuffsApplied,
                IsPlayingRotatingCharacter = pbPlayer.ExtraFields.IsPlayingRotatingCharacter
            },
            UnlockState = new SerializableUnlockState
            {
                UnlockedEpochs = discoveries.UnlockedEpochIdx.Select(i => archive.EpochIds[(int)i - 1]).ToList(),
                EncountersSeen = discoveries.EncountersSeenIdx.Select(i => DecodeModelId(archive.ModelIds, i)!).ToList(),
                NumberOfRuns = discoveries.NumberOfRuns
            },
            DiscoveredCards = discoveries.DiscoveredCardsIdx.Select(i => DecodeModelId(archive.ModelIds, i)!).ToList(),
            DiscoveredEnemies = discoveries.DiscoveredEnemiesIdx.Select(i => DecodeModelId(archive.ModelIds, i)!).ToList(),
            DiscoveredEpochs = discoveries.DiscoveredEpochIdx.Select(i => archive.EpochIds[(int)i - 1]).ToList(),
            DiscoveredPotions = discoveries.DiscoveredPotionsIdx.Select(i => DecodeModelId(archive.ModelIds, i)!).ToList(),
            DiscoveredRelics = discoveries.DiscoveredRelicsIdx.Select(i => DecodeModelId(archive.ModelIds, i)!).ToList()
        };

        return new SerializableRun
        {
            SchemaVersion = archive.SchemaVersion,
            Acts = acts,
            Modifiers = modifiers,
            DailyTime = archive.HasDailyTime ? DateTimeOffset.FromUnixTimeSeconds(archive.DailyTimeUnixSeconds) : null,
            CurrentActIndex = snapshot.CurrentActIndex,
            EventsSeen = discoveries.EventsSeenIdx.Select(i => DecodeModelId(archive.ModelIds, i)!).ToList(),
            PreFinishedRoom = null,
            SerializableOdds = new SerializableRunOddsSet
            {
                UnknownMapPointMonsterOddsValue = snapshot.Odds.UnknownMonsterOdds,
                UnknownMapPointEliteOddsValue = snapshot.Odds.UnknownEliteOdds,
                UnknownMapPointTreasureOddsValue = snapshot.Odds.UnknownTreasureOdds,
                UnknownMapPointShopOddsValue = snapshot.Odds.UnknownShopOdds
            },
            SerializableSharedRelicGrabBag = DecodeRelicGrabBag(archive.GrabBagsPool[(int)snapshot.SharedGrabBagRefIdx - 1], archive.ModelIds),
            Players = new List<SerializablePlayer> { player },
            SerializableRng = runRng,
            VisitedMapCoords = snapshot.VisitedMapCoords.Select(c => new MapCoord(c.Col, c.Row)).ToList(),
            MapPointHistory = SliceMapPointHistory(history.MapPointHistory, floorNum - 1),
            SaveTime = snapshot.SaveTime,
            StartTime = archive.StartTime,
            RunTime = snapshot.RunTime,
            WinTime = snapshot.WinTime,
            Ascension = archive.Ascension,
            NumReloads = snapshot.NumReloads,
            PlatformType = (PlatformType)archive.PlatformType,
            MapDrawings = snapshot.DrawingsRefIdx == 0
                ? null
                : DecodeMapDrawings(archive.DrawingsPool[(int)snapshot.DrawingsRefIdx - 1]),
            ExtraFields = new SerializableExtraRunFields
            {
                StartedWithNeow = snapshot.ExtraRunFields.StartedWithNeow,
                TestSubjectKills = snapshot.ExtraRunFields.TestSubjectKills,
                FreedRepy = snapshot.ExtraRunFields.FreedRepy,
                UsedDebugCommand = snapshot.ExtraRunFields.UsedDebugCommand
            },
            GameMode = (GameMode)archive.GameMode,
            IsCustomGame = archive.IsCustomGame
        };
    }

    private static List<List<MapPointHistoryEntry>> SliceMapPointHistory(
        IReadOnlyList<List<MapPointHistoryEntry>> fullHistory,
        int entriesToTake)
    {
        List<List<MapPointHistoryEntry>> result = new List<List<MapPointHistoryEntry>>();
        int remaining = entriesToTake;
        foreach (List<MapPointHistoryEntry> actEntries in fullHistory)
        {
            if (remaining <= 0)
            {
                break;
            }
            int take = Math.Min(remaining, actEntries.Count);
            if (take > 0)
            {
                List<MapPointHistoryEntry> slice = new List<MapPointHistoryEntry>(take);
                for (int i = 0; i < take; i++)
                {
                    slice.Add(CloneLastIfNeeded(actEntries[i], isLast: remaining - take == 0 && i == take - 1));
                }
                result.Add(slice);
                remaining -= take;
            }
        }
        return result;
    }

    private static MapPointHistoryEntry CloneLastIfNeeded(MapPointHistoryEntry entry, bool isLast)
    {
        if (!isLast)
        {
            return entry;
        }
        // RunManager.UpdatePlayerStatsInMapPointHistory mutates PlayerStats on the last entry;
        // clone PlayerStats on the boundary entry so the in-memory RunHistory remains untouched.
        return new MapPointHistoryEntry
        {
            MapPointType = entry.MapPointType,
            Rooms = entry.Rooms,
            PlayerStats = entry.PlayerStats.Select(p => new PlayerMapPointHistoryEntry
            {
                PlayerId = p.PlayerId,
                CurrentGold = p.CurrentGold,
                CurrentHp = p.CurrentHp,
                MaxHp = p.MaxHp,
                DamageTaken = p.DamageTaken,
                HpHealed = p.HpHealed,
                MaxHpLost = p.MaxHpLost,
                MaxHpGained = p.MaxHpGained,
                GoldGained = p.GoldGained,
                GoldSpent = p.GoldSpent,
                GoldLost = p.GoldLost,
                GoldStolen = p.GoldStolen,
                CardsGained = p.CardsGained,
                CardsRemoved = p.CardsRemoved,
                CardsTransformed = p.CardsTransformed,
                CardsEnchanted = p.CardsEnchanted,
                CardChoices = p.CardChoices,
                RelicChoices = p.RelicChoices,
                PotionChoices = p.PotionChoices,
                AncientChoices = p.AncientChoices,
                BlitzMerchantChoices = p.BlitzMerchantChoices,
                RestSiteChoices = p.RestSiteChoices,
                PotionUsed = p.PotionUsed,
                PotionDiscarded = p.PotionDiscarded,
                EventChoices = p.EventChoices,
                UpgradedCards = p.UpgradedCards,
                DowngradedCards = p.DowngradedCards,
                RelicsRemoved = p.RelicsRemoved,
                CompletedQuests = p.CompletedQuests,
                IsAffectedByFurCoat = p.IsAffectedByFurCoat
            }).ToList()
        };
    }

    private static PbDiscoverySnapshot EncodeDiscoveries(
        List<ModelId> eventsSeen,
        SerializablePlayer player,
        StringTableBuilder modelIds,
        StringTableBuilder epochIds)
    {
        PbDiscoverySnapshot snap = new PbDiscoverySnapshot
        {
            NumberOfRuns = player.UnlockState.NumberOfRuns
        };
        foreach (ModelId id in eventsSeen)
        {
            snap.EventsSeenIdx.Add(InternModelId(modelIds, id));
        }
        foreach (string epoch in player.UnlockState.UnlockedEpochs)
        {
            snap.UnlockedEpochIdx.Add(epochIds.Intern(epoch));
        }
        foreach (ModelId id in player.UnlockState.EncountersSeen)
        {
            snap.EncountersSeenIdx.Add(InternModelId(modelIds, id));
        }
        foreach (ModelId id in player.DiscoveredCards)
        {
            snap.DiscoveredCardsIdx.Add(InternModelId(modelIds, id));
        }
        foreach (ModelId id in player.DiscoveredEnemies)
        {
            snap.DiscoveredEnemiesIdx.Add(InternModelId(modelIds, id));
        }
        foreach (string epoch in player.DiscoveredEpochs)
        {
            snap.DiscoveredEpochIdx.Add(epochIds.Intern(epoch));
        }
        foreach (ModelId id in player.DiscoveredPotions)
        {
            snap.DiscoveredPotionsIdx.Add(InternModelId(modelIds, id));
        }
        foreach (ModelId id in player.DiscoveredRelics)
        {
            snap.DiscoveredRelicsIdx.Add(InternModelId(modelIds, id));
        }
        return snap;
    }

    private static PbCardList EncodeDeck(List<SerializableCard> deck, StringTableBuilder modelIds, StringTableBuilder propNames)
    {
        PbCardList list = new PbCardList();
        foreach (SerializableCard card in deck)
        {
            list.Cards.Add(EncodeCard(card, modelIds, propNames));
        }
        return list;
    }

    private static List<SerializableCard> DecodeDeck(PbCardList list, IReadOnlyList<string> modelIds, IReadOnlyList<string> propNames)
    {
        return list.Cards.Select(c => DecodeCard(c, modelIds, propNames)).ToList();
    }

    private static PbCard EncodeCard(SerializableCard card, StringTableBuilder modelIds, StringTableBuilder propNames)
    {
        PbCard pb = new PbCard
        {
            IdIdx = InternModelId(modelIds, card.Id),
            CurrentUpgradeLevel = card.CurrentUpgradeLevel,
            HasFloorAdded = card.FloorAddedToDeck.HasValue,
            FloorAddedToDeck = card.FloorAddedToDeck ?? 0
        };
        if (card.Enchantment != null)
        {
            pb.Enchantment = new PbEnchantment
            {
                IdIdx = InternModelId(modelIds, card.Enchantment.Id),
                Amount = card.Enchantment.Amount,
                Props = EncodeSavedProperties(card.Enchantment.Props, modelIds, propNames)
            };
        }
        pb.Props = EncodeSavedProperties(card.Props, modelIds, propNames);
        return pb;
    }

    private static SerializableCard DecodeCard(PbCard pb, IReadOnlyList<string> modelIds, IReadOnlyList<string> propNames)
    {
        return new SerializableCard
        {
            Id = DecodeModelId(modelIds, pb.IdIdx),
            CurrentUpgradeLevel = pb.CurrentUpgradeLevel,
            Enchantment = pb.Enchantment == null
                ? null
                : new SerializableEnchantment
                {
                    Id = DecodeModelId(modelIds, pb.Enchantment.IdIdx),
                    Amount = pb.Enchantment.Amount,
                    Props = DecodeSavedProperties(pb.Enchantment.Props, modelIds, propNames)
                },
            Props = DecodeSavedProperties(pb.Props, modelIds, propNames),
            FloorAddedToDeck = pb.HasFloorAdded ? pb.FloorAddedToDeck : null
        };
    }

    private static PbRelicList EncodeRelics(List<SerializableRelic> relics, StringTableBuilder modelIds, StringTableBuilder propNames)
    {
        PbRelicList list = new PbRelicList();
        foreach (SerializableRelic relic in relics)
        {
            list.Relics.Add(new PbRelic
            {
                IdIdx = InternModelId(modelIds, relic.Id),
                Props = EncodeSavedProperties(relic.Props, modelIds, propNames),
                HasFloorAdded = relic.FloorAddedToDeck.HasValue,
                FloorAddedToDeck = relic.FloorAddedToDeck ?? 0
            });
        }
        return list;
    }

    private static List<SerializableRelic> DecodeRelics(PbRelicList list, IReadOnlyList<string> modelIds, IReadOnlyList<string> propNames)
    {
        return list.Relics.Select(r => new SerializableRelic
        {
            Id = DecodeModelId(modelIds, r.IdIdx),
            Props = DecodeSavedProperties(r.Props, modelIds, propNames),
            FloorAddedToDeck = r.HasFloorAdded ? r.FloorAddedToDeck : null
        }).ToList();
    }

    private static PbPotionList EncodePotions(List<SerializablePotion> potions, StringTableBuilder modelIds)
    {
        PbPotionList list = new PbPotionList();
        foreach (SerializablePotion potion in potions)
        {
            list.Potions.Add(new PbPotion
            {
                IdIdx = InternModelId(modelIds, potion.Id),
                SlotIndex = potion.SlotIndex
            });
        }
        return list;
    }

    private static List<SerializablePotion> DecodePotions(PbPotionList list, IReadOnlyList<string> modelIds)
    {
        return list.Potions.Select(p => new SerializablePotion
        {
            Id = DecodeModelId(modelIds, p.IdIdx),
            SlotIndex = p.SlotIndex
        }).ToList();
    }

    private static PbSavedProperties? EncodeSavedProperties(
        SavedProperties? props,
        StringTableBuilder modelIds,
        StringTableBuilder propNames)
    {
        if (props == null)
        {
            return null;
        }

        PbSavedProperties pb = new PbSavedProperties();
        if (props.ints != null)
        {
            foreach (SavedProperties.SavedProperty<int> item in props.ints)
            {
                pb.Ints.Add(new PbSavedInt { NameIdx = propNames.Intern(item.name), Value = item.value });
            }
        }
        if (props.bools != null)
        {
            foreach (SavedProperties.SavedProperty<bool> item in props.bools)
            {
                pb.Bools.Add(new PbSavedBool { NameIdx = propNames.Intern(item.name), Value = item.value });
            }
        }
        if (props.strings != null)
        {
            foreach (SavedProperties.SavedProperty<string> item in props.strings)
            {
                pb.Strings.Add(new PbSavedString { NameIdx = propNames.Intern(item.name), Value = item.value ?? string.Empty });
            }
        }
        if (props.intArrays != null)
        {
            foreach (SavedProperties.SavedProperty<int[]> item in props.intArrays)
            {
                PbSavedIntArray arr = new PbSavedIntArray { NameIdx = propNames.Intern(item.name) };
                if (item.value != null)
                {
                    arr.Values.AddRange(item.value);
                }
                pb.IntArrays.Add(arr);
            }
        }
        if (props.modelIds != null)
        {
            foreach (SavedProperties.SavedProperty<ModelId> item in props.modelIds)
            {
                pb.ModelIds.Add(new PbSavedModelId
                {
                    NameIdx = propNames.Intern(item.name),
                    ModelIdIdx = InternModelId(modelIds, item.value)
                });
            }
        }
        if (props.cards != null)
        {
            foreach (SavedProperties.SavedProperty<SerializableCard> item in props.cards)
            {
                pb.Cards.Add(new PbSavedCard
                {
                    NameIdx = propNames.Intern(item.name),
                    Value = EncodeCard(item.value, modelIds, propNames)
                });
            }
        }
        if (props.cardArrays != null)
        {
            foreach (SavedProperties.SavedProperty<SerializableCard[]> item in props.cardArrays)
            {
                PbSavedCardArray arr = new PbSavedCardArray { NameIdx = propNames.Intern(item.name) };
                if (item.value != null)
                {
                    foreach (SerializableCard c in item.value)
                    {
                        arr.Values.Add(EncodeCard(c, modelIds, propNames));
                    }
                }
                pb.CardArrays.Add(arr);
            }
        }
        return pb;
    }

    private static SavedProperties? DecodeSavedProperties(
        PbSavedProperties? pb,
        IReadOnlyList<string> modelIds,
        IReadOnlyList<string> propNames)
    {
        if (pb == null)
        {
            return null;
        }

        SavedProperties props = new SavedProperties();
        if (pb.Ints.Count > 0)
        {
            props.ints = pb.Ints.Select(i => new SavedProperties.SavedProperty<int>(propNames[(int)i.NameIdx - 1], i.Value)).ToList();
        }
        if (pb.Bools.Count > 0)
        {
            props.bools = pb.Bools.Select(b => new SavedProperties.SavedProperty<bool>(propNames[(int)b.NameIdx - 1], b.Value)).ToList();
        }
        if (pb.Strings.Count > 0)
        {
            props.strings = pb.Strings.Select(s => new SavedProperties.SavedProperty<string>(propNames[(int)s.NameIdx - 1], s.Value)).ToList();
        }
        if (pb.IntArrays.Count > 0)
        {
            props.intArrays = pb.IntArrays.Select(a => new SavedProperties.SavedProperty<int[]>(propNames[(int)a.NameIdx - 1], a.Values.ToArray())).ToList();
        }
        if (pb.ModelIds.Count > 0)
        {
            props.modelIds = pb.ModelIds.Select(m => new SavedProperties.SavedProperty<ModelId>(
                propNames[(int)m.NameIdx - 1],
                DecodeModelId(modelIds, m.ModelIdIdx) ?? ModelId.none)).ToList();
        }
        if (pb.Cards.Count > 0)
        {
            props.cards = pb.Cards.Select(c => new SavedProperties.SavedProperty<SerializableCard>(
                propNames[(int)c.NameIdx - 1],
                DecodeCard(c.Value, modelIds, propNames))).ToList();
        }
        if (pb.CardArrays.Count > 0)
        {
            props.cardArrays = pb.CardArrays.Select(a => new SavedProperties.SavedProperty<SerializableCard[]>(
                propNames[(int)a.NameIdx - 1],
                a.Values.Select(c => DecodeCard(c, modelIds, propNames)).ToArray())).ToList();
        }
        return props;
    }

    private static PbActMap EncodeActMap(SerializableActMap map)
    {
        PbActMap pb = new PbActMap
        {
            BossPoint = EncodeMapPoint(map.BossPoint),
            HasSecondBoss = map.SecondBossPoint != null,
            SecondBossPoint = map.SecondBossPoint != null ? EncodeMapPoint(map.SecondBossPoint) : null,
            StartingPoint = EncodeMapPoint(map.StartingPoint),
            HasStartMapPointCoords = map.StartMapPointCoords != null,
            GridWidth = map.GridWidth,
            GridHeight = map.GridHeight
        };
        foreach (SerializableMapPoint point in map.Points)
        {
            pb.Points.Add(EncodeMapPoint(point));
        }
        if (map.StartMapPointCoords != null)
        {
            foreach (MapCoord coord in map.StartMapPointCoords)
            {
                pb.StartMapPointCoords.Add(new PbMapCoord { Col = coord.col, Row = coord.row });
            }
        }
        return pb;
    }

    private static SerializableActMap DecodeActMap(PbActMap pb)
    {
        return new SerializableActMap
        {
            Points = pb.Points.Select(DecodeMapPoint).ToList(),
            BossPoint = DecodeMapPoint(pb.BossPoint),
            SecondBossPoint = pb.HasSecondBoss ? DecodeMapPoint(pb.SecondBossPoint) : null,
            StartingPoint = DecodeMapPoint(pb.StartingPoint),
            StartMapPointCoords = pb.HasStartMapPointCoords
                ? pb.StartMapPointCoords.Select(c => new MapCoord(c.Col, c.Row)).ToList()
                : null,
            GridWidth = pb.GridWidth,
            GridHeight = pb.GridHeight
        };
    }

    private static PbMapPoint EncodeMapPoint(SerializableMapPoint point)
    {
        PbMapPoint pb = new PbMapPoint
        {
            Coord = new PbMapCoord { Col = point.Coord.col, Row = point.Coord.row },
            PointType = (int)point.PointType,
            CanBeModified = point.CanBeModified,
            HasChildCoords = point.ChildCoords != null
        };
        if (point.ChildCoords != null)
        {
            foreach (MapCoord child in point.ChildCoords)
            {
                pb.ChildCoords.Add(new PbMapCoord { Col = child.col, Row = child.row });
            }
        }
        return pb;
    }

    private static SerializableMapPoint DecodeMapPoint(PbMapPoint pb)
    {
        return new SerializableMapPoint
        {
            Coord = new MapCoord(pb.Coord.Col, pb.Coord.Row),
            PointType = (MapPointType)pb.PointType,
            CanBeModified = pb.CanBeModified,
            ChildCoords = pb.HasChildCoords
                ? pb.ChildCoords.Select(c => new MapCoord(c.Col, c.Row)).ToList()
                : null
        };
    }

    private static PbRelicGrabBag EncodeRelicGrabBag(SerializableRelicGrabBag bag, StringTableBuilder modelIds)
    {
        PbRelicGrabBag pb = new PbRelicGrabBag();
        foreach (KeyValuePair<RelicRarity, List<ModelId>> kvp in bag.RelicIdLists.OrderBy(k => (int)k.Key))
        {
            PbRelicRarityList bucket = new PbRelicRarityList { Rarity = (int)kvp.Key };
            foreach (ModelId id in kvp.Value)
            {
                bucket.RelicIdIdx.Add(InternModelId(modelIds, id));
            }
            pb.Buckets.Add(bucket);
        }
        return pb;
    }

    private static SerializableRelicGrabBag DecodeRelicGrabBag(PbRelicGrabBag pb, IReadOnlyList<string> modelIds)
    {
        SerializableRelicGrabBag bag = new SerializableRelicGrabBag();
        foreach (PbRelicRarityList bucket in pb.Buckets)
        {
            bag.RelicIdLists[(RelicRarity)bucket.Rarity] = bucket.RelicIdIdx
                .Select(i => DecodeModelId(modelIds, i)!)
                .ToList();
        }
        return bag;
    }

    private static PbMapDrawings EncodeMapDrawings(SerializableMapDrawings drawings)
    {
        PbMapDrawings pb = new PbMapDrawings();
        foreach (SerializablePlayerMapDrawings playerDrawings in drawings.drawings)
        {
            PbPlayerMapDrawings pbPlayer = new PbPlayerMapDrawings { PlayerId = playerDrawings.playerId };
            foreach (SerializableMapDrawingLine line in playerDrawings.lines)
            {
                PbMapDrawingLine pbLine = new PbMapDrawingLine { IsEraser = line.isEraser };
                foreach (Vector2 pt in line.mapPoints)
                {
                    pbLine.PointsX.Add(pt.X);
                    pbLine.PointsY.Add(pt.Y);
                }
                pbPlayer.Lines.Add(pbLine);
            }
            pb.Drawings.Add(pbPlayer);
        }
        return pb;
    }

    private static SerializableMapDrawings DecodeMapDrawings(PbMapDrawings pb)
    {
        SerializableMapDrawings drawings = new SerializableMapDrawings();
        foreach (PbPlayerMapDrawings player in pb.Drawings)
        {
            SerializablePlayerMapDrawings playerDrawings = new SerializablePlayerMapDrawings { playerId = player.PlayerId };
            foreach (PbMapDrawingLine line in player.Lines)
            {
                SerializableMapDrawingLine decodedLine = new SerializableMapDrawingLine { isEraser = line.IsEraser };
                int count = Math.Min(line.PointsX.Count, line.PointsY.Count);
                for (int i = 0; i < count; i++)
                {
                    decodedLine.mapPoints.Add(new Vector2(line.PointsX[i], line.PointsY[i]));
                }
                playerDrawings.lines.Add(decodedLine);
            }
            drawings.drawings.Add(playerDrawings);
        }
        return drawings;
    }

    private static uint InternModelId(StringTableBuilder table, ModelId? id)
    {
        return id == null ? 0u : table.Intern(id.ToString());
    }

    private static ModelId? DecodeModelId(IReadOnlyList<string> table, uint idx)
    {
        return idx == 0 ? null : ModelId.Deserialize(table[(int)idx - 1]);
    }

    private static uint InternPoolItem<T>(Google.Protobuf.Collections.RepeatedField<T> pool, T item)
        where T : class, IEquatable<T>
    {
        for (int i = 0; i < pool.Count; i++)
        {
            if (pool[i].Equals(item))
            {
                return (uint)(i + 1);
            }
        }
        pool.Add(item);
        return (uint)pool.Count;
    }

    private sealed class StringTableBuilder
    {
        private readonly Google.Protobuf.Collections.RepeatedField<string> _target;
        private readonly Dictionary<string, uint> _index;

        public StringTableBuilder(Google.Protobuf.Collections.RepeatedField<string> target)
        {
            _target = target;
            _index = new Dictionary<string, uint>(target.Count, StringComparer.Ordinal);
            for (int i = 0; i < target.Count; i++)
            {
                _index[target[i]] = (uint)(i + 1);
            }
        }

        public uint Intern(string value)
        {
            if (_index.TryGetValue(value, out uint existing))
            {
                return existing;
            }
            _target.Add(value);
            uint newIndex = (uint)_target.Count;
            _index[value] = newIndex;
            return newIndex;
        }
    }
}
