using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace RoomRangeConfig.SpawnModule.ExtendedClasses
{
    // JSON-facing representation of an EnemySetup.  The game keeps EnemySetup
    // assets in memory, so this class deliberately stores prefab names rather
    // than Unity object references.
    public class ExtendedEnemySetup
    {
        public ExtendedEnemySetup() { }

        public ExtendedEnemySetup(EnemySetup enemySetup, int difficulty)
        {
            name = enemySetup.name;

            if (enemySetup.levelsCompletedCondition)
            {
                levelRangeCondition = true;
                minLevel = enemySetup.levelsCompletedMin + 1;
                maxLevel = enemySetup.levelsCompletedMax + 1;
            }

            if (maxLevel == 1)
                maxLevel = 0;

            runsPlayed = enemySetup.runsPlayed;
            spawnObjects = (enemySetup.spawnObjects ?? new List<PrefabRef>())
                .Where(obj => obj != null && !obj.PrefabName.Contains("Director"))
                .Select(obj => obj.PrefabName)
                .ToList();

            float baseWeight = 100f;
            if (enemySetup.rarityPreset)
            {
                baseWeight = (float)Math.Round(-2.08f * (100f - enemySetup.rarityPreset.chance) + 98.45f);
                if (baseWeight > 100f) baseWeight = 100f;
                if (baseWeight == 98f) baseWeight = 100f;
                if (baseWeight <= 15f && baseWeight > 2f) baseWeight = 5f;
                if (baseWeight < 2f) baseWeight = 2f;
                SpawnModuleState.Logger?.LogDebug(name + " (rarity) = " + enemySetup.rarityPreset.chance);
            }

            difficulty1Weight = difficulty == 1 ? baseWeight : 0f;
            difficulty2Weight = difficulty == 2 ? baseWeight : 0f;
            difficulty3Weight = difficulty == 3 ? baseWeight : 0f;

            foreach (string levelName in ListManager.loadedLevelNames)
            {
                if (!levelWeightMultipliers.ContainsKey(levelName))
                    levelWeightMultipliers.Add(levelName, 1f);
            }
        }

        public EnemySetup GetEnemySetup()
        {
            EnemySetup result = ScriptableObject.CreateInstance<EnemySetup>();
            result.name = name;
            result.spawnObjects = new List<PrefabRef>();
            result.levelsCompletedCondition = levelRangeCondition;
            result.levelsCompletedMin = minLevel - 1;
            result.levelsCompletedMax = maxLevel - 1;
            result.runsPlayed = runsPlayed;

            foreach (string objectName in spawnObjects ?? new List<string>())
            {
                if (ListManager.spawnObjectsDict.TryGetValue(objectName, out PrefabRef prefab))
                    result.spawnObjects.Add(prefab);
            }

            return result;
        }

        public float GetWeight(int difficulty, List<EnemySetup> enemyList, string currentLevelName)
        {
            if (levelWeightMultipliers == null)
                levelWeightMultipliers = new Dictionary<string, float>();

            float weight = difficulty == 1 ? difficulty1Weight
                : difficulty == 2 ? difficulty2Weight
                : difficulty3Weight;

            if (levelWeightMultipliers.TryGetValue(currentLevelName, out float levelMultiplier))
                weight *= levelMultiplier;

            if (SpawnModuleState.configManager != null && SpawnModuleState.configManager.enableVarietyPlus.Value)
            {
                int previousLevel = RunManager.instance != null ? RunManager.instance.levelsCompleted : 0;
                int levelsToCheck = SpawnModuleState.configManager.consecutiveLevelCount.Value;
                levelsToCheck = Math.Min(levelsToCheck, previousLevel);

                for (int i = 0; i < levelsToCheck; i++)
                {
                    int historyIndex = previousLevel - 1 - i;
                    if (historyIndex >= 0 && historyIndex < ListManager.previousSpawns.Count &&
                        ListManager.previousSpawns[historyIndex].Contains(name))
                    {
                        weight *= (float)ListManager.GetLevelNumMultiplier(i + 1);
                    }
                }
            }

            if (enemyList != null && enemyList.Any(obj => obj != null && obj.name == name))
            {
                weight *= (float)(SpawnModuleState.configManager?.repeatMultiplier.Value ?? 1.0);
                if (!allowDuplicates)
                    weight = 0f;
            }

            return weight < 0.1f ? 0f : weight;
        }

        // Kept for compatibility with older SpawnGroups.json files.  The old
        // implementation reflected over properties even though the model uses
        // public fields, so unchanged values were never migrated.  Fields are
        // intentionally handled explicitly here.
        public void UpdateWithDefaults(ExtendedEnemySetup defaultSetup)
        {
            if (defaultSetup == null) return;

            if (!ListManager.extendedSetups.TryGetValue(defaultSetup.name, out ExtendedEnemySetup runtimeDefaults) ||
                ReferenceEquals(runtimeDefaults, this))
                return;

            FieldInfo[] fields = typeof(ExtendedEnemySetup).GetFields(BindingFlags.Instance | BindingFlags.Public);
            foreach (FieldInfo field in fields)
            {
                object defaultValue = field.GetValue(defaultSetup);
                object currentValue = field.GetValue(this);
                object runtimeValue = field.GetValue(runtimeDefaults);
                if (!Equals(defaultValue, currentValue) || Equals(runtimeValue, defaultValue))
                    continue;

                ManualLogSource logger = SpawnModuleState.Logger;
                logger?.LogInfo("Updating unmodified field " + field.Name + ": " + currentValue + " => " + runtimeValue);
                field.SetValue(this, runtimeValue);
            }
        }

        public bool Update()
        {
            bool changed = false;

            if (spawnObjects == null)
            {
                spawnObjects = new List<string>();
                changed = true;
            }
            if (levelWeightMultipliers == null)
            {
                levelWeightMultipliers = new Dictionary<string, float>();
                changed = true;
            }

            // Legacy names used by earlier SpawnConfig releases.
            if (levelsCompletedCondition)
            {
                levelRangeCondition = true;
                minLevel = levelsCompletedMin;
                maxLevel = levelsCompletedMax;
                levelsCompletedCondition = false;
                changed = true;
            }

            if (!levelRangeCondition && maxLevel == 10)
            {
                maxLevel = 0;
                changed = true;
            }

            if (thisGroupOnly)
            {
                soloGroup = true;
                thisGroupOnly = false;
                changed = true;
            }

            if (SpawnModuleState.configManager != null && SpawnModuleState.configManager.removeUnloadedLevelWeights.Value)
            {
                var retained = levelWeightMultipliers
                    .Where(pair => ListManager.loadedLevelNames.Contains(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
                if (retained.Count != levelWeightMultipliers.Count)
                {
                    levelWeightMultipliers = retained;
                    changed = true;
                }
            }

            foreach (string levelName in ListManager.loadedLevelNames)
            {
                if (!levelWeightMultipliers.ContainsKey(levelName))
                {
                    levelWeightMultipliers.Add(levelName, 1f);
                    changed = true;
                }
            }

            return changed;
        }

        public string name = "Nameless";
        public bool levelsCompletedCondition = false;
        public int levelsCompletedMax = 10;
        public int levelsCompletedMin = 0;
        public bool levelRangeCondition = false;
        public int minLevel = 0;
        public int maxLevel = 0;
        public int runsPlayed = 0;
        public List<string> spawnObjects = new List<string>();
        public float difficulty1Weight = 0f;
        public float difficulty2Weight = 0f;
        public float difficulty3Weight = 0f;
        public Dictionary<string, float> levelWeightMultipliers = new Dictionary<string, float>();
        public bool thisGroupOnly = false;
        public bool soloGroup = false;
        public bool allowDuplicates = true;
        public double alterAmountChance = 0.0;
        public int alterAmountMin = 0;
        public int alterAmountMax = 0;
    }
}
