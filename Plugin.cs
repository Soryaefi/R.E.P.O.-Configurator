using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using RoomRangeConfig.SpawnModule;

namespace RoomRangeConfig
{
    // A single milestone: from `Level` onward, use this room range and extraction count,
    // until a higher-level milestone takes over.
    public class Milestone
    {
        public int Level;
        public int MinRooms;
        public int MaxRooms;
        public int Extractions;
    }

    // One slot's worth of bound ConfigEntries. REPOConfig can only render
    // bool/int/float/string entries - it has no "list" or "add row" widget -
    // so a variable-length milestone list isn't something it can display.
    // Instead we bind a fixed number of slots (see Plugin.MaxMilestoneSlots),
    // each in its own section, and each slot's "Enabled" checkbox acts as the
    // add/remove control: check it to make that milestone count, uncheck to
    // drop it. This is the closest REPOConfig-native equivalent to a dynamic
    // "Add Milestone" button.
    public class MilestoneSlot
    {
        public ConfigEntry<bool> Enabled;
        public ConfigEntry<int> Level;
        public ConfigEntry<int> MinRooms;
        public ConfigEntry<int> MaxRooms;
        public ConfigEntry<int> Extractions;
    }

    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "SavagePacifist.roomrangeconfig";
        public const string PluginName = "RoomRangeConfig";
        public const string PluginVersion = "1.6.1";

        // How many "Milestone N" sections get bound in REPOConfig. Raise this
        // if you want even more than 12 - it just means more slots shown in-game
        // (unused ones just stay unchecked and have no effect).
        public const int MaxMilestoneSlots = 12;

        public static ManualLogSource Log;
        public static Plugin Instance;

        private ConfigEntry<bool> _modEnabled;
        private readonly List<MilestoneSlot> _slots = new List<MilestoneSlot>();

        // Built from whichever slots are Enabled, sorted by Level. Rebuilt any
        // time a slot's value changes (so REPOConfig edits apply live).
        public static List<Milestone> Milestones { get; private set; } = new List<Milestone>();

        // Defaults for the first few slots mirror the mod's original defaults;
        // slots beyond that start disabled with placeholder values.
        // NOTE: `extractions` here is the actual extraction point count
        // (1-10) - no offset. Previously this stored (actual - 1) to work
        // around what looked like a REPOConfig slider display quirk, but the
        // slider itself only ever supported -1..9; there's no separate
        // "true" value being compensated for, that -1..9 range was just
        // confusing to look at in-game. The slider now runs 1..10 directly.
        private static readonly (bool enabled, int level, int min, int max, int extractions)[] SlotDefaults =
        {
            (true,   1,  5,  7, 1),
            (true,   4,  7,  9, 2),
            (true,   8,  9, 12, 3),
            (false, 12, 10, 13, 3),
            (false, 16, 11, 14, 4),
            (false, 20, 12, 15, 4),
            (false, 24, 13, 16, 5),
            (false, 28, 14, 17, 5),
            (false, 32, 15, 18, 6),
            (false, 36, 16, 19, 6),
            (false, 40, 17, 20, 7),
            (false, 44, 18, 21, 7),
        };

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            _modEnabled = Config.Bind(
                "General",
                "Mod Enabled",
                true,
                "Enable custom room/extraction milestones. If disabled, vanilla level generation is used.");

            for (int i = 0; i < MaxMilestoneSlots; i++)
            {
                string section = $"Milestone {i + 1}";
                (bool enabled, int level, int min, int max, int extractions) d = i < SlotDefaults.Length
                    ? SlotDefaults[i]
                    : (false, 1, 5, 7, 1);

                var slot = new MilestoneSlot
                {
                    Enabled = Config.Bind(section, "Enabled", d.enabled,
                        "Include this milestone. Uncheck to remove it without deleting its values."),
                    Level = Config.Bind(section, "Milestone", d.level,
                        new ConfigDescription("Applies from this level onward, until a higher enabled milestone's level is reached.",
                            new AcceptableValueRange<int>(1, 1000))),
                    MinRooms = Config.Bind(section, "Minimum Rooms", d.min,
                        new ConfigDescription("Lowest room count that can be rolled for this milestone.",
                            new AcceptableValueRange<int>(1, 100))),
                    MaxRooms = Config.Bind(section, "Maximum Rooms", d.max,
                        new ConfigDescription("Highest room count that can be rolled for this milestone.",
                            new AcceptableValueRange<int>(1, 100))),
                    Extractions = Config.Bind(section, "Extraction Points", d.extractions,
                        new ConfigDescription("Number of extraction points for this milestone.",
                            new AcceptableValueRange<int>(1, 10))),
                };

                slot.Enabled.SettingChanged += (_, _) => RebuildMilestones();
                slot.Level.SettingChanged += (_, _) => RebuildMilestones();
                slot.MinRooms.SettingChanged += (_, _) => RebuildMilestones();
                slot.MaxRooms.SettingChanged += (_, _) => RebuildMilestones();
                slot.Extractions.SettingChanged += (_, _) => RebuildMilestones();

                _slots.Add(slot);
            }

            RebuildMilestones();

            RoomCapManager.Init(Config);
            ValuablesConfig.Init(Config);
            EnemyRespawnRoomConfig.Init(Config);
            LobbySizeConfig.Init(Config);
            EnemyRelocationConfig.Init(Config);

            // Folded-in SpawnConfig module (see SpawnModuleState). Setup() binds
            // its config entries and must run before PatchAll() below, per its
            // own contract.
            // NOTE: SpawnModuleState.ReadAndUpdateJSON() is intentionally NOT
            // called here. It uses ListManager.extendedSetups/extendedGroupCounts
            // as its source of truth for what the game actually has, and nothing
            // in the files wired up so far populates those from the game's real
            // enemy/spawn data - that population step (scanning EnemySetup /
            // PrefabRef assets, filling ListManager.levelNumbers and the
            // difficultyXCounts lists) is still missing. Calling ReadAndUpdateJSON()
            // before that runs would write out empty JSON and then throw on
            // customGroupCountsList[0]. Wire the call in wherever that scan
            // finishes (see chat message for what's still needed).
            SpawnModuleState.Setup(Config, Logger);

            // Valuables (value/count), the enemy-respawn fallback, and enemy
            // relocation-on-despawn are all attribute-based Harmony patches -
            // PatchAll() below picks them up automatically, no manual TryPatch
            // call needed.
            var harmony = new Harmony(PluginGuid);
            harmony.PatchAll();

            Log.LogInfo($"{PluginName} v{PluginVersion} loaded with {Milestones.Count} milestone(s). " +
                "Valuables: confirmed hook, active when Mode is set in config. " +
                "Enemy respawn fallback: confirmed hook, active when Enabled in config. " +
                "Lobby size override: confirmed hook, active when Enabled in config. " +
                "Enemy relocation on despawn: active when Enabled in config.");
        }

        private void RebuildMilestones()
        {
            var built = new List<Milestone>();

            foreach (var slot in _slots)
            {
                if (!slot.Enabled.Value)
                    continue;

                int min = slot.MinRooms.Value;
                int max = slot.MaxRooms.Value;
                if (min > max)
                {
                    Log.LogWarning($"[RoomRangeConfig] A milestone had Minimum Rooms ({min}) > Maximum Rooms ({max}) - swapping them.");
                    (min, max) = (max, min);
                }

                built.Add(new Milestone
                {
                    Level = slot.Level.Value,
                    MinRooms = min,
                    MaxRooms = max,
                    Extractions = slot.Extractions.Value
                });
            }

            if (built.Count == 0)
            {
                Log.LogWarning("[RoomRangeConfig] No milestones enabled - falling back to a single default milestone.");
                built.Add(new Milestone { Level = 1, MinRooms = 5, MaxRooms = 7, Extractions = 1 });
            }

            Milestones = built.OrderBy(m => m.Level).ToList();
            Log.LogInfo($"[RoomRangeConfig] Rebuilt {Milestones.Count} active milestone(s).");
        }

        public static bool IsEnabled() => Instance != null && Instance._modEnabled.Value;

        // Returns the milestone that applies to the given level: the highest-level
        // milestone whose Level is <= the current level.
        public static Milestone GetMilestoneForLevel(int level)
        {
            return Milestones.LastOrDefault(m => m.Level <= level) ?? Milestones.First();
        }
    }

    // ---------------------------------------------------------------------
    // Real hook, confirmed via LevelScaling's decompiled source:
    //   - LevelGenerator.ModuleAmount / LevelGenerator.ExtractionAmount are the
    //     fields that control room and extraction counts.
    //   - They get assigned from the level's own data *inside* TileGeneration,
    //     mid-method - so a plain Prefix/Postfix on TileGeneration either runs
    //     too early (gets overwritten) or too late (grid already built).
    //   - The fix is a transpiler that splices a call to our getter in right
    //     after the original assignment, before the value is used to build
    //     the grid. This mirrors LevelScaling's proven injection point.
    //
    //   NOTE: ModuleAmount is `internal` and ExtractionAmount is `private` on
    //   LevelGenerator, both in Assembly-CSharp - a different assembly from
    //   this plugin. C# access modifiers are enforced at compile time based
    //   on assembly boundaries, so direct field access (LevelGenerator.Instance.ModuleAmount)
    //   will NOT compile here no matter what. AccessTools.Field(...) from
    //   Harmony uses reflection, which bypasses that check at runtime - so we
    //   use it for reads too, not just for the transpiler's Stfld targets.
    // ---------------------------------------------------------------------
    [HarmonyPatch(typeof(LevelGenerator))]
    public static class LevelGeneratorPatch
    {
        // levelsCompleted is 0-indexed (0 before your first level), so the
        // "level number" milestones are written against is +1.
        public static int CurrentLevel => RunManager.instance.levelsCompleted + 1;

        // CONFIRMED against decompiled LevelGenerator.cs / Level.cs.
        //   - LevelGenerator.Generate() does `this.Level = RunManager.instance.levelCurrent;`
        //     early on (before TileGeneration() is started), so LevelGenerator.Instance.Level
        //     is already populated by the time our transpiled TileGeneration runs.
        //   - LevelGenerator.Level is a PUBLIC field - no reflection needed, unlike
        //     ModuleAmount/ExtractionAmount.
        //   - Level is a ScriptableObject (Level.cs), so `.name` is the standard
        //     UnityEngine.Object.name (the asset's name) - always public, nothing guessed.
        // Falls back to a shared "Unknown Map" slot only if LevelGenerator.Instance or
        // its Level hasn't been set yet (shouldn't happen at the point this is called,
        // but kept as a safety net rather than throwing).
        private static string GetCurrentMapName()
        {
            var level = LevelGenerator.Instance != null ? LevelGenerator.Instance.Level : null;
            return level != null ? level.name : "Unknown Map";
        }

        private static readonly FieldInfo ModuleAmountField =
            AccessTools.Field(typeof(LevelGenerator), "ModuleAmount");
        private static readonly FieldInfo ExtractionAmountField =
            AccessTools.Field(typeof(LevelGenerator), "ExtractionAmount");

        private static int GetNewModuleCount()
        {
            int fallback = (int)ModuleAmountField.GetValue(LevelGenerator.Instance);

            if (!Plugin.IsEnabled() || !SemiFunc.RunIsLevel())
                return fallback;

            string mapName = GetCurrentMapName();
            if (RoomCapManager.TryGetCap(mapName, out int capped))
            {
                Plugin.Log.LogInfo($"[RoomRangeConfig] Map '{mapName}' room cap bypass active -> rooms {capped} (milestones ignored)");
                return capped;
            }

            var milestone = Plugin.GetMilestoneForLevel(CurrentLevel);
            int value = UnityEngine.Random.Range(milestone.MinRooms, milestone.MaxRooms + 1);

            Plugin.Log.LogInfo($"[RoomRangeConfig] Level {CurrentLevel} -> rooms {value} (range {milestone.MinRooms}-{milestone.MaxRooms})");
            return value;
        }

        private static int GetNewExtractionCount()
        {
            int fallback = (int)ExtractionAmountField.GetValue(LevelGenerator.Instance);

            if (!Plugin.IsEnabled() || !SemiFunc.RunIsLevel())
                return fallback;

            var milestone = Plugin.GetMilestoneForLevel(CurrentLevel);

            Plugin.Log.LogInfo($"[RoomRangeConfig] Level {CurrentLevel} -> extractions {milestone.Extractions}");
            return milestone.Extractions;
        }

        // Same injection strategy as LevelScaling: find where TileGeneration
        // first reads/assigns ModuleAmount and ExtractionAmount, and splice
        // in a call to our own getters right after, overwriting the value
        // before it's used to size the level grid.
        // TileGeneration is a C# iterator (yield return inside it), so the compiler
        // moves its real body into a hidden nested class's MoveNext() method - the
        // "TileGeneration" method itself is just a tiny stub that constructs that
        // state machine. MethodType.Enumerator tells Harmony to resolve and patch
        // that generated MoveNext instead of the stub.
        [HarmonyPatch("TileGeneration", MethodType.Enumerator)]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> TileGenerationTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);

            if (list.Count <= 20)
            {
                Plugin.Log.LogError("[RoomRangeConfig] TileGeneration has fewer than 20 instructions - abnormal, skipping patch.");
                return list;
            }

            int moduleInsertIndex = -1;
            for (int i = 3; i < list.Count - 1; i++)
            {
                if (list[i].opcode == OpCodes.Ldfld
                    && (FieldInfo)list[i].operand == ModuleAmountField
                    && list[i + 1].opcode == OpCodes.Ldc_I4_3)
                {
                    moduleInsertIndex = i - 3;
                    break;
                }
            }

            if (moduleInsertIndex == -1)
            {
                Plugin.Log.LogError("[RoomRangeConfig] Could not find ModuleAmount insertion point - game may have updated. Mod will not affect room count.");
                return list;
            }

            int extractionInsertIndex = -1;
            for (int j = 1; j < list.Count - 1; j++)
            {
                if (list[j].opcode == OpCodes.Ldarg_0
                    && list[j + 1].opcode == OpCodes.Ldc_I4_M1
                    && list[j + 4].opcode == OpCodes.Ldfld
                    && (FieldInfo)list[j + 4].operand == ExtractionAmountField)
                {
                    extractionInsertIndex = j - 1;
                    break;
                }
            }

            if (extractionInsertIndex == -1)
            {
                Plugin.Log.LogError("[RoomRangeConfig] Could not find ExtractionAmount insertion point - game may have updated. Mod will not affect extraction count.");
                return list;
            }

            var moduleInject = new List<CodeInstruction>
            {
                new CodeInstruction(OpCodes.Ldloc_1),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(LevelGeneratorPatch), nameof(GetNewModuleCount))),
                new CodeInstruction(OpCodes.Stfld, ModuleAmountField)
            };

            var extractionInject = new List<CodeInstruction>
            {
                new CodeInstruction(OpCodes.Ldloc_1),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(LevelGeneratorPatch), nameof(GetNewExtractionCount))),
                new CodeInstruction(OpCodes.Stfld, ExtractionAmountField)
            };

            // Insert the later index first so the earlier index isn't shifted.
            list.InsertRange(extractionInsertIndex, extractionInject);
            list.InsertRange(moduleInsertIndex, moduleInject);

            return list;
        }
    }
}