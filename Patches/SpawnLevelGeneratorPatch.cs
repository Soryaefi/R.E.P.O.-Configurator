using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RoomRangeConfig.SpawnModule;
using UnityEngine;

namespace RoomRangeConfig.Patches
{
    // Applies the extended SpawnGroups.json behavior at the point where the
    // game is about to instantiate one EnemySetup group.
    [HarmonyPatch(typeof(LevelGenerator))]
    public static class SpawnLevelGeneratorPatch
    {
        public static int PickNonDirector(EnemySetup enemySetup)
        {
            if (enemySetup == null || enemySetup.spawnObjects == null)
                return -1;

            List<int> candidates = new List<int>();
            for (int i = 0; i < enemySetup.spawnObjects.Count; i++)
            {
                PrefabRef prefab = enemySetup.spawnObjects[i];
                if (prefab != null && !prefab.PrefabName.Contains("Director"))
                    candidates.Add(i);
            }

            return candidates.Count == 0
                ? -1
                : candidates[Random.Range(0, candidates.Count)];
        }

        [HarmonyPatch("EnemySpawn")]
        [HarmonyPrefix]
        public static void LogAndModifySpawns(EnemySetup enemySetup, Vector3 position, LevelGenerator __instance)
        {
            if (enemySetup == null)
                return;

            int currentLevelIndex = ListManager.previousSpawns.Count - 1;
            if (currentLevelIndex >= 0 && !ListManager.previousSpawns[currentLevelIndex].Contains(enemySetup.name))
                ListManager.previousSpawns[currentLevelIndex].Add(enemySetup.name);

            AddDirectorIfNeeded(enemySetup, "Gnome", "Enemy - Gnome Director");
            AddDirectorIfNeeded(enemySetup, "Bang", "Enemy - Bang Director");

            if (enemySetup.spawnObjects == null)
                enemySetup.spawnObjects = new List<PrefabRef>();

            if (ListManager.extendedSetups.TryGetValue(enemySetup.name, out var extended))
            {
                int maxRoll = extended.alterAmountChance <= 0.0
                    ? int.MaxValue
                    : Math.Max(1, (int)Math.Round(1.0 / extended.alterAmountChance));

                if (maxRoll != int.MaxValue && Random.Range(1, maxRoll + 1) == 1 && enemySetup.spawnObjects.Count > 0)
                {
                    int amountChange = Random.Range(extended.alterAmountMin, extended.alterAmountMax + 1);
                    while (amountChange > 0)
                    {
                        int index = PickNonDirector(enemySetup);
                        if (index < 0) break;
                        enemySetup.spawnObjects.Add(enemySetup.spawnObjects[index]);
                        amountChange--;
                    }

                    while (amountChange < 0 && enemySetup.spawnObjects.Count > 0)
                    {
                        int index = PickNonDirector(enemySetup);
                        if (index < 0) break;
                        enemySetup.spawnObjects.RemoveAt(index);
                        amountChange++;
                    }
                }
            }

            Dictionary<string, int> counts = new Dictionary<string, int>();
            foreach (PrefabRef spawnObject in enemySetup.spawnObjects)
            {
                EnemyDirectorPatch.enemySpawnCount++;
                if (spawnObject == null) continue;

                counts.TryGetValue(spawnObject.PrefabName, out int count);
                counts[spawnObject.PrefabName] = count + 1;
            }

            string logString = string.Join(
                ", ",
                counts.Where(pair => !pair.Key.Contains("Director"))
                    .Select(pair => pair.Key + " x " + pair.Value));

            if (string.IsNullOrEmpty(logString))
                logString = "No spawn objects found in group...";

            if (SpawnModuleState.configManager.preventSpawns.Value)
            {
                enemySetup.spawnObjects.Clear();
                SpawnModuleState.Logger.LogInfo("Forcibly prevented enemy spawn!");
            }
            else
            {
                SpawnModuleState.Logger.LogInfo("Spawning [" + enemySetup.name + "]   (" +
                    logString.Replace("Enemy - ", "") + ")");
            }
        }

        private static void AddDirectorIfNeeded(EnemySetup setup, string enemyMarker, string directorName)
        {
            if (setup.spawnObjects == null || !setup.spawnObjects.Any(obj =>
                    obj != null && obj.PrefabName.Contains(enemyMarker)))
                return;

            if (setup.spawnObjects.Any(obj => obj != null && obj.PrefabName == directorName))
                return;

            if (ListManager.spawnObjectsDict.TryGetValue(directorName, out PrefabRef director))
                setup.spawnObjects.Insert(0, director);
        }
    }
}
