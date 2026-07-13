using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RoomRangeConfig
{
    // ---------------------------------------------------------------------
    // CONFIRMED against decompiled ValuableDirector.cs (SetupHost/SpawnValuable/
    // SpawnCosmeticWorldObject) in addition to ValuableObject.cs. This adds a
    // real Minimum Amount / Minimum Value floor on top of the existing
    // Maximum Count / per-item Min-Max Value range, using a "top-up" pass
    // rather than trying to raise the game's own internal caps:
    //
    //   - SetupHost()'s valuable-spawn loop reads `totalMaxAmount` fresh every
    //     iteration (not cached), so it CAN be nudged live - but the seven
    //     per-type caps (tinyMaxAmount, smallMaxAmount, ...) get copied into a
    //     local `_maxAmount` array ONCE, before the loop starts and before our
    //     SpawnValuable hook ever fires. By the time our hook runs, that
    //     snapshot already happened, so raising those seven fields from a
    //     SpawnValuable patch does nothing for the current level. Doing this
    //     "properly" would mean transpiling SetupHost itself (a coroutine, so
    //     same MethodType.Enumerator trick as TileGenerationTranspiler) -
    //     fragile, and still ultimately bounded by physical ValuableVolume
    //     counts in the level.
    //   - Cleaner approach: let the vanilla loop run untouched, then - right
    //     before VolumesAndSwitchSetup() (called once, near the very end of
    //     SetupHost, after both the valuables loop AND the cosmetic-object
    //     loop finish) - check our own tallies against the configured
    //     Minimum Amount / Minimum Value. If short, walk the SAME private
    //     per-type volume/valuable lists the game itself used
    //     (tinyVolumes/tinyValuables, smallVolumes/smallValuables, ...) and
    //     force-call the game's own SpawnValuable directly on whichever
    //     ValuableVolumes are still genuinely free, until either the minimum
    //     is met or there are no free volumes left. "Free" is hard-capped by
    //     what physically exists in the level - if the level just doesn't
    //     have enough spawn points, the minimum is unreachable and we log
    //     that and stop, we don't invent spawn points.
    //   - "Free" volumes are tracked ourselves (a HashSet<ValuableVolume>),
    //     not read off the game, because the index that tracks per-type usage
    //     (`_volumeIndex`) is a local inside SetupHost's coroutine, not a
    //     field - reflection can't reach it. We mark a volume used from a
    //     Postfix on SpawnValuable (valuables) AND on SpawnCosmeticWorldObject
    //     (cosmetic props draw from the exact same per-type volume lists, so
    //     ignoring them would risk our top-up picking a volume a cosmetic
    //     prop already occupied).
    //   - Calling SpawnValuable ourselves still goes through Harmony, so our
    //     existing per-item value-override patch (on DollarValueSetLogic) and
    //     the Maximum Count cap both apply normally to forced spawns too -
    //     no bypass needed, since Minimum Amount is clamped <= Maximum Count
    //     at config time (see RebuildMilestones).
    //
    // dollarValueOriginal/dollarValueCurrent, and all seven per-type
    // volume/valuable/path fields used below, are `internal`/`private` in a
    // different assembly, so they're read via AccessTools.Field, same as
    // ModuleAmount/ExtractionAmount in Plugin.cs. SpawnValuable is private
    // too, so forced calls go through AccessTools.Method + Invoke.
    // ---------------------------------------------------------------------

    // REPOConfig has no true 3-state checkbox, so the "off / cap the
    // amount / cap the value" choice is a 3-option dropdown instead - the
    // standard BepInEx equivalent for picking one of several named options.
    public enum ValuablesMode
    {
        Disabled,
        MaxAmount,
        MaxValue
    }

    public class ValuablesMilestone
    {
        public int Level;
        public int MinValue;
        public int MaxValue;
        public int MinAmount;
        public int MaxCount;
        public int MinTotalValue;
    }

    public class ValuablesMilestoneSlot
    {
        public ConfigEntry<bool> Enabled;
        public ConfigEntry<int> Level;
        public ConfigEntry<int> MinValue;
        // Text boxes rather than sliders: sliders aren't precise at these
        // ranges (per-item up to a million, total up to several million), so
        // these are free-typed digits, sanitized in code - see
        // SanitizeAmountEntry.
        public ConfigEntry<string> MaxValueText;
        public ConfigEntry<int> MinAmount;
        public ConfigEntry<int> MaxCount;
        public ConfigEntry<string> MinTotalValueText;
    }

    public static class ValuablesConfig
    {
        public const int MaxValuablesMilestoneSlots = 8;

        // Per-item Maximum Value textbox rules: anything above this is
        // clamped down to it; anything that isn't a plain whole number
        // (letters, blank, decimals, negative signs, etc.) resets to the
        // fallback default.
        public const int MaxValueCap = 1000000;
        public const int MaxValueFallbackDefault = 100000;

        // Total-value floor textbox rules: a level's total is a sum across
        // many items, so this cap is intentionally much higher than a single
        // item's cap above. 0 means "no minimum" (disabled), which also
        // doubles as the fallback for invalid input, since defaulting an
        // unrecognized value to some arbitrary positive minimum would be
        // surprising.
        public const int MinTotalValueCap = 5000000;
        public const int MinTotalValueFallbackDefault = 0;

        private static ConfigEntry<ValuablesMode> _mode;
        private static readonly List<ValuablesMilestoneSlot> _slots = new List<ValuablesMilestoneSlot>();

        public static List<ValuablesMilestone> Milestones { get; private set; } = new List<ValuablesMilestone>();

        private static readonly FieldInfo DollarValueOriginalField =
            AccessTools.Field(typeof(ValuableObject), "dollarValueOriginal");
        private static readonly FieldInfo DollarValueCurrentField =
            AccessTools.Field(typeof(ValuableObject), "dollarValueCurrent");

        // Index order used consistently across all three arrays below:
        // Tiny, Small, Medium, Big, Wide, Tall, Very Tall.
        private static readonly string[] TypeNames =
        {
            "Tiny", "Small", "Medium", "Big", "Wide", "Tall", "Very Tall"
        };
        private static readonly FieldInfo[] VolumesFields =
        {
            AccessTools.Field(typeof(ValuableDirector), "tinyVolumes"),
            AccessTools.Field(typeof(ValuableDirector), "smallVolumes"),
            AccessTools.Field(typeof(ValuableDirector), "mediumVolumes"),
            AccessTools.Field(typeof(ValuableDirector), "bigVolumes"),
            AccessTools.Field(typeof(ValuableDirector), "wideVolumes"),
            AccessTools.Field(typeof(ValuableDirector), "tallVolumes"),
            AccessTools.Field(typeof(ValuableDirector), "veryTallVolumes"),
        };
        private static readonly FieldInfo[] ValuablesFields =
        {
            AccessTools.Field(typeof(ValuableDirector), "tinyValuables"),
            AccessTools.Field(typeof(ValuableDirector), "smallValuables"),
            AccessTools.Field(typeof(ValuableDirector), "mediumValuables"),
            AccessTools.Field(typeof(ValuableDirector), "bigValuables"),
            AccessTools.Field(typeof(ValuableDirector), "wideValuables"),
            AccessTools.Field(typeof(ValuableDirector), "tallValuables"),
            AccessTools.Field(typeof(ValuableDirector), "veryTallValuables"),
        };
        private static readonly FieldInfo[] PathFields =
        {
            AccessTools.Field(typeof(ValuableDirector), "tinyPath"),
            AccessTools.Field(typeof(ValuableDirector), "smallPath"),
            AccessTools.Field(typeof(ValuableDirector), "mediumPath"),
            AccessTools.Field(typeof(ValuableDirector), "bigPath"),
            AccessTools.Field(typeof(ValuableDirector), "widePath"),
            AccessTools.Field(typeof(ValuableDirector), "tallPath"),
            AccessTools.Field(typeof(ValuableDirector), "veryTallPath"),
        };
        private static readonly MethodInfo SpawnValuableMethod =
            AccessTools.Method(typeof(ValuableDirector), "SpawnValuable",
                new[] { typeof(PrefabRef), typeof(ValuableVolume), typeof(string) });

        private static int _lastSeenLevelsCompleted = -1;
        private static int _spawnedThisLevel;
        private static int _totalValueThisLevel;
        private static readonly HashSet<ValuableVolume> _usedVolumesThisLevel = new HashSet<ValuableVolume>();

        public static void Init(ConfigFile config)
        {
            _mode = config.Bind(
                "Valuables", "Mode", ValuablesMode.Disabled,
                "Disabled: valuables milestones have no effect. " +
                "Max Amount: control total item count per milestone (Minimum/Maximum Amount below) - cash values are left untouched. " +
                "Max Value: control per-item cash value per milestone (Minimum/Maximum Value below), plus a floor on the level's total cash value (Minimum Total Value). " +
                "Both minimums are hard-capped by however many valuable spawn points actually exist in the level - if a level doesn't have enough spots, the minimum can't be fully reached and the mod logs that rather than inventing spawn points.");

            for (int i = 0; i < MaxValuablesMilestoneSlots; i++)
            {
                string section = $"Valuables Milestone {i + 1}";
                bool defaultEnabled = i == 0;

                var slot = new ValuablesMilestoneSlot
                {
                    Enabled = config.Bind(section, "Enabled", defaultEnabled,
                        "Include this valuables milestone. Uncheck to remove it without deleting its values."),
                    // Same -1 storage convention as the room milestones in
                    // Plugin.cs, to keep slider behavior consistent.
                    Level = config.Bind(section, "Milestone", 0,
                        new ConfigDescription("Applies from this level onward (stored as actual level - 1, same as the room milestones).",
                            new AcceptableValueRange<int>(0, 999))),
                    MinValue = config.Bind(section, "Minimum Value", 10,
                        new ConfigDescription("Lowest possible cash value for a single spawned valuable. Only used when Mode is 'Max Value'.",
                            new AcceptableValueRange<int>(0, 100000))),
                    MaxValueText = config.Bind(section, "Maximum Value", MaxValueFallbackDefault.ToString(),
                        $"Highest possible cash value for a single spawned valuable, typed as plain digits (a slider isn't precise enough up to {MaxValueCap:N0}). " +
                        $"Only used when Mode is 'Max Value'. Values above {MaxValueCap:N0} are clamped down to {MaxValueCap:N0}. " +
                        $"Anything that isn't a whole number (letters, blank, decimals, etc.) resets to the default {MaxValueFallbackDefault:N0}."),
                    MinAmount = config.Bind(section, "Minimum Amount", 0,
                        new ConfigDescription("Force extra valuables to spawn (into whatever spawn points the level has left over) until at least this many total valuables exist on the level. 0 = no minimum. Only used when Mode is 'Max Amount'. Hard-capped by the level's actual spawn points - if there aren't enough, this is logged and the mod stops there.",
                            new AcceptableValueRange<int>(0, 1000))),
                    MaxCount = config.Bind(section, "Maximum Count", 20,
                        new ConfigDescription("Hard cap on number of valuables the level generates per level (0 = unlimited). Only used when Mode is 'Max Amount'. Over-cap valuables are simply never spawned. Minimum Amount above is automatically kept at or below this.",
                            new AcceptableValueRange<int>(0, 1000))),
                    MinTotalValueText = config.Bind(section, "Minimum Total Value", MinTotalValueFallbackDefault.ToString(),
                        $"Force extra valuables to spawn (into whatever spawn points the level has left over, each rolled within Minimum/Maximum Value above) until the level's total cash value reaches this, typed as plain digits. " +
                        $"0 = no minimum. Only used when Mode is 'Max Value'. Values above {MinTotalValueCap:N0} are clamped down to {MinTotalValueCap:N0}. " +
                        $"Anything that isn't a whole number resets to {MinTotalValueFallbackDefault} (disabled). Hard-capped by the level's actual spawn points."),
                };

                // Correct any bad value already on disk (e.g. hand-edited
                // .cfg) before the first RebuildMilestones, not just on the
                // next live edit.
                SanitizeAmountEntry(slot.MaxValueText, MaxValueCap, MaxValueFallbackDefault);
                SanitizeAmountEntry(slot.MinTotalValueText, MinTotalValueCap, MinTotalValueFallbackDefault);

                slot.Enabled.SettingChanged += (_, _) => RebuildMilestones();
                slot.Level.SettingChanged += (_, _) => RebuildMilestones();
                slot.MinValue.SettingChanged += (_, _) => RebuildMilestones();
                slot.MaxValueText.SettingChanged += (_, _) =>
                {
                    SanitizeAmountEntry(slot.MaxValueText, MaxValueCap, MaxValueFallbackDefault);
                    RebuildMilestones();
                };
                slot.MinAmount.SettingChanged += (_, _) => RebuildMilestones();
                slot.MaxCount.SettingChanged += (_, _) => RebuildMilestones();
                slot.MinTotalValueText.SettingChanged += (_, _) =>
                {
                    SanitizeAmountEntry(slot.MinTotalValueText, MinTotalValueCap, MinTotalValueFallbackDefault);
                    RebuildMilestones();
                };

                _slots.Add(slot);
            }

            RebuildMilestones();
        }

        // Rewrites the textbox's own stored value if it's invalid, so what
        // the player sees in REPOConfig reflects the corrected number rather
        // than silently using a different number internally. Reassigning
        // entry.Value here re-fires SettingChanged once more, but by then the
        // text is valid so that second pass is a no-op. Shared by both the
        // per-item Maximum Value box and the Minimum Total Value box - same
        // rules, different cap/fallback.
        private static void SanitizeAmountEntry(ConfigEntry<string> entry, int cap, int fallback)
        {
            string raw = entry.Value?.Trim();

            if (!int.TryParse(raw, out int parsed) || parsed < 0)
            {
                Plugin.Log.LogWarning($"[RoomRangeConfig] Valuables: '{raw}' isn't a whole number - resetting to {fallback}.");
                entry.Value = fallback.ToString();
                return;
            }

            if (parsed > cap)
            {
                Plugin.Log.LogInfo($"[RoomRangeConfig] Valuables: {parsed} exceeds the cap - clamping to {cap}.");
                entry.Value = cap.ToString();
            }
        }

        private static void RebuildMilestones()
        {
            var built = new List<ValuablesMilestone>();

            foreach (var slot in _slots)
            {
                if (!slot.Enabled.Value)
                    continue;

                int minV = slot.MinValue.Value;
                int maxV = int.TryParse(slot.MaxValueText.Value, out int parsedMax) ? parsedMax : MaxValueFallbackDefault;
                if (minV > maxV) (minV, maxV) = (maxV, minV);

                int minAmount = slot.MinAmount.Value;
                int maxCount = slot.MaxCount.Value;
                if (maxCount > 0 && minAmount > maxCount)
                {
                    Plugin.Log.LogWarning($"[RoomRangeConfig] Valuables: a milestone had Minimum Amount ({minAmount}) > Maximum Count ({maxCount}) - clamping Minimum Amount down to match.");
                    minAmount = maxCount;
                }

                int minTotalValue = int.TryParse(slot.MinTotalValueText.Value, out int parsedMin) ? parsedMin : MinTotalValueFallbackDefault;

                built.Add(new ValuablesMilestone
                {
                    Level = slot.Level.Value + 1,
                    MinValue = minV,
                    MaxValue = maxV,
                    MinAmount = minAmount,
                    MaxCount = maxCount,
                    MinTotalValue = minTotalValue,
                });
            }

            Milestones = built.OrderBy(m => m.Level).ToList();
            Plugin.Log.LogInfo($"[RoomRangeConfig] Valuables: rebuilt {Milestones.Count} active milestone(s).");
        }

        public static ValuablesMilestone GetMilestoneForLevel(int level)
        {
            if (Milestones.Count == 0) return null;
            return Milestones.LastOrDefault(m => m.Level <= level) ?? Milestones.First();
        }

        public static bool IsEnabled() => Plugin.IsEnabled() && _mode != null && _mode.Value != ValuablesMode.Disabled;
        public static bool UseCountMode() => _mode != null && _mode.Value == ValuablesMode.MaxAmount;

        private static int CurrentLevel => RunManager.instance != null ? RunManager.instance.levelsCompleted + 1 : 1;

        // Called once from wherever first touches the per-level tallies each
        // level (ApplyValueOverride / ShouldAllowSpawn / TryTopUpMinimums) -
        // whichever runs first for a given level resets for everyone.
        private static void EnsureLevelTrackingFresh()
        {
            if (RunManager.instance == null) return;
            if (RunManager.instance.levelsCompleted == _lastSeenLevelsCompleted) return;

            _lastSeenLevelsCompleted = RunManager.instance.levelsCompleted;
            _spawnedThisLevel = 0;
            _totalValueThisLevel = 0;
            _usedVolumesThisLevel.Clear();
        }

        // Called from ValuableObjectValuePatch below - fires once per
        // successfully spawned valuable, natural or forced, regardless of
        // mode, so _totalValueThisLevel always reflects reality.
        public static void ApplyValueOverride(ValuableObject instance)
        {
            EnsureLevelTrackingFresh();

            if (IsEnabled() && !UseCountMode())
            {
                var milestone = GetMilestoneForLevel(CurrentLevel);
                if (milestone != null)
                {
                    int rolled = Random.Range(milestone.MinValue, milestone.MaxValue + 1);
                    DollarValueOriginalField.SetValue(instance, (float)rolled);
                    DollarValueCurrentField.SetValue(instance, (float)rolled);
                }
            }

            _totalValueThisLevel += (int)(float)DollarValueCurrentField.GetValue(instance);
        }

        // Called from ValuableDirectorSpawnPatch's Prefix below, BEFORE
        // ValuableDirector.SpawnValuable does anything - returning false
        // here skips the spawn entirely (no GameObject, no
        // DollarValueSetLogic call, no haul-goal contribution) rather than
        // spawning it and destroying it afterward.
        public static bool ShouldAllowSpawn()
        {
            EnsureLevelTrackingFresh();

            if (!IsEnabled() || !UseCountMode()) return true;

            var milestone = GetMilestoneForLevel(CurrentLevel);
            if (milestone == null) return true;

            if (milestone.MaxCount > 0 && _spawnedThisLevel >= milestone.MaxCount)
            {
                Plugin.Log.LogInfo($"[RoomRangeConfig] Valuables: skipping a spawn on level {CurrentLevel} (Maximum Count {milestone.MaxCount} reached) - nothing instantiated, nothing to destroy.");
                return false;
            }

            return true;
        }

        // Called from ValuableDirectorSpawnPatch's Postfix - only when the
        // Prefix actually allowed the spawn through (see __state usage on
        // the patch itself).
        public static void MarkSpawned() => _spawnedThisLevel++;

        // Called from both ValuableDirectorSpawnPatch and
        // ValuableDirectorCosmeticSpawnPatch below, so our "is this volume
        // free" bookkeeping accounts for cosmetic props too, not just
        // valuables - both draw from the same per-type volume lists.
        public static void MarkVolumeUsed(ValuableVolume volume)
        {
            if (volume != null) _usedVolumesThisLevel.Add(volume);
        }

        // Called from VolumesAndSwitchSetupPatch below, right before the
        // game finalizes volumes/switches for the level - i.e. after both
        // the valuables loop AND the cosmetic-object loop have already run,
        // so whatever's left really is free.
        public static void TryTopUpMinimums(ValuableDirector director)
        {
            EnsureLevelTrackingFresh();

            if (!IsEnabled() || director == null) return;

            var milestone = GetMilestoneForLevel(CurrentLevel);
            if (milestone == null) return;

            int targetAmount = UseCountMode() ? milestone.MinAmount : 0;
            int targetValue = !UseCountMode() ? milestone.MinTotalValue : 0;

            if (targetAmount <= 0 && targetValue <= 0) return;

            // Safety ceiling so a bug here can never turn into an infinite
            // loop - comfortably above anything a real config would ask for.
            const int maxIterations = 2000;
            int iterations = 0;

            while ((targetAmount > 0 && _spawnedThisLevel < targetAmount) ||
                   (targetValue > 0 && _totalValueThisLevel < targetValue))
            {
                if (++iterations > maxIterations)
                {
                    Plugin.Log.LogWarning("[RoomRangeConfig] Valuables: top-up hit its safety iteration limit - stopping early.");
                    break;
                }

                if (!TryForceSpawnOne(director))
                {
                    Plugin.Log.LogInfo($"[RoomRangeConfig] Valuables: no free spawn points left on level {CurrentLevel} - stopped short of the configured minimum (have {_spawnedThisLevel} valuables, {_totalValueThisLevel} total value). The level just doesn't have enough spots.");
                    break;
                }
            }
        }

        // Picks a random still-free ValuableVolume (across whichever types
        // currently have both an unused volume and a non-empty valuables
        // list) and force-spawns one valuable into it via the game's own
        // SpawnValuable. Returns false if nothing is free anywhere.
        private static bool TryForceSpawnOne(ValuableDirector director)
        {
            var eligibleTypes = new List<int>();
            for (int t = 0; t < TypeNames.Length; t++)
            {
                var volumes = (System.Collections.IList)VolumesFields[t].GetValue(director);
                var valuables = (System.Collections.IList)ValuablesFields[t].GetValue(director);
                if (valuables == null || valuables.Count == 0 || volumes == null) continue;

                bool hasFreeVolume = false;
                foreach (var v in volumes)
                {
                    if (v is ValuableVolume vv && !_usedVolumesThisLevel.Contains(vv))
                    {
                        hasFreeVolume = true;
                        break;
                    }
                }
                if (hasFreeVolume) eligibleTypes.Add(t);
            }

            if (eligibleTypes.Count == 0) return false;

            int type = eligibleTypes[Random.Range(0, eligibleTypes.Count)];
            var typeVolumes = (System.Collections.IList)VolumesFields[type].GetValue(director);
            var typeValuables = (System.Collections.IList)ValuablesFields[type].GetValue(director);

            var freeVolumes = new List<ValuableVolume>();
            foreach (var v in typeVolumes)
            {
                if (v is ValuableVolume vv && !_usedVolumesThisLevel.Contains(vv))
                    freeVolumes.Add(vv);
            }
            if (freeVolumes.Count == 0) return false;

            var chosenVolume = freeVolumes[Random.Range(0, freeVolumes.Count)];
            var chosenValuable = (PrefabRef)typeValuables[Random.Range(0, typeValuables.Count)];
            string path = (string)PathFields[type].GetValue(director);

            Plugin.Log.LogInfo($"[RoomRangeConfig] Valuables: forcing an extra {TypeNames[type]} valuable to reach the configured minimum.");
            SpawnValuableMethod.Invoke(director, new object[] { chosenValuable, chosenVolume, path });
            return true;
        }
    }

    [HarmonyPatch(typeof(ValuableObject), nameof(ValuableObject.DollarValueSetLogic))]
    public static class ValuableObjectValuePatch
    {
        private static void Postfix(ValuableObject __instance) => ValuablesConfig.ApplyValueOverride(__instance);
    }

    [HarmonyPatch(typeof(ValuableDirector), "SpawnValuable")]
    public static class ValuableDirectorSpawnPatch
    {
        private static bool Prefix(ValuableVolume _volume, out bool __state)
        {
            bool allow = ValuablesConfig.ShouldAllowSpawn();
            __state = allow;
            return allow;
        }

        private static void Postfix(ValuableVolume _volume, bool __state)
        {
            if (!__state) return;
            ValuablesConfig.MarkSpawned();
            ValuablesConfig.MarkVolumeUsed(_volume);
        }
    }

    // Cosmetic props are drawn from the exact same per-type ValuableVolume
    // lists as valuables (see SetupHost's shared `_volumes`/`_volumeIndex`
    // locals), so without this patch our top-up could pick a volume a
    // cosmetic prop already occupies. `_volumeIndex` is a mutable array
    // passed by reference, so a Prefix snapshot + Postfix diff tells us
    // which type's slot just advanced, and therefore which volume was used.
    [HarmonyPatch(typeof(ValuableDirector), "SpawnCosmeticWorldObject")]
    public static class ValuableDirectorCosmeticSpawnPatch
    {
        private static void Prefix(int[] _volumeIndex, out int[] __state)
        {
            __state = (int[])_volumeIndex.Clone();
        }

        private static void Postfix(int[] _volumeIndex, List<ValuableVolume>[] _volumes, int[] __state)
        {
            for (int i = 0; i < _volumeIndex.Length; i++)
            {
                if (_volumeIndex[i] > __state[i])
                {
                    if (__state[i] < _volumes[i].Count)
                        ValuablesConfig.MarkVolumeUsed(_volumes[i][__state[i]]);
                    break;
                }
            }
        }
    }

    // Fires once, near the very end of SetupHost, master-side only (in
    // multiplayer it's the direct call inside master's own coroutine that we
    // want - the RPC broadcast to clients happens *inside* this method, one
    // level down, so patching here still only runs our top-up once).
    [HarmonyPatch(typeof(ValuableDirector), nameof(ValuableDirector.VolumesAndSwitchSetup))]
    public static class VolumesAndSwitchSetupPatch
    {
        private static void Prefix(ValuableDirector __instance) => ValuablesConfig.TryTopUpMinimums(__instance);
    }
}
