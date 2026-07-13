using System;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RoomRangeConfig.SpawnModule;
using RoomRangeConfig.SpawnModule.ExtendedClasses;
using UnityEngine;

namespace RoomRangeConfig.Patches
{
    // Owns the runtime half of SpawnConfig: it discovers the vanilla enemy
    // assets, loads the JSON overrides, and replaces EnemyDirector.AmountSetup
    // with the weighted selection described by those overrides.
    [HarmonyPatch(typeof(EnemyDirector))]
    public static class EnemyDirectorPatch
    {
        public static bool setupDone;
        public static int currentDifficultyPick = 3;
        public static bool onlyOneSetup;
        public static int enemySpawnCount;

        private static bool setupAttemptStarted;
        private static bool bundleLoaderHooked;

        [HarmonyPatch("Start")]
        [HarmonyPostfix]
        private static void SetupAfterStart(EnemyDirector __instance)
        {
            if (setupDone || __instance == null)
                return;

            // EnemyDirector.Start can run before custom asset bundles have
            // finished loading.  The original SpawnConfig listens to
            // REPOLib.BundleLoader.OnAllBundlesLoaded; keep that timing when
            // REPOLib is present, and retain a coroutine fallback so the
            // merged plugin does not need a hard compile-time REPOLib DLL.
            TryHookBundleLoader(__instance);
            SetupOnStart(__instance);

            if (!setupDone && !setupAttemptStarted)
            {
                setupAttemptStarted = true;
                __instance.StartCoroutine(WaitForAssets(__instance));
            }
        }

        private static IEnumerator WaitForAssets(EnemyDirector instance)
        {
            const int maxAttempts = 240;
            for (int attempt = 0; attempt < maxAttempts && !setupDone; attempt++)
            {
                SetupOnStart(instance);
                if (setupDone)
                    yield break;

                yield return new WaitForSecondsRealtime(0.25f);
            }

            if (!setupDone)
                SpawnModuleState.Logger.LogWarning("SpawnConfig enemy data was not ready after waiting for game assets; vanilla enemy spawning will remain active.");
        }

        private static void TryHookBundleLoader(EnemyDirector instance)
        {
            if (bundleLoaderHooked)
                return;

            Type bundleLoaderType = AccessTools.TypeByName("REPOLib.BundleLoader");
            EventInfo bundlesLoaded = bundleLoaderType?.GetEvent("OnAllBundlesLoaded",
                BindingFlags.Public | BindingFlags.Static);
            if (bundlesLoaded == null || bundlesLoaded.EventHandlerType != typeof(Action))
                return;

            Action callback = () => SetupOnStart(instance);
            bundlesLoaded.AddEventHandler(null, callback);
            bundleLoaderHooked = true;
        }

        public static void SetupOnStart(EnemyDirector instance)
        {
            if (setupDone || instance == null || RunManager.instance == null || RunManager.instance.levels == null)
                return;

            try
            {
                List<EnemySetup>[] difficulties =
                {
                    instance.enemiesDifficulty3,
                    instance.enemiesDifficulty2,
                    instance.enemiesDifficulty1
                };

                if (difficulties.Any(list => list == null) || difficulties.All(list => list.Count == 0) ||
                    RunManager.instance.levels.Count == 0)
                    return;

                ListManager.loadedLevelNames.Clear();
                foreach (Level level in RunManager.instance.levels)
                {
                    if (level != null)
                        ListManager.loadedLevelNames.Add(level.name.Replace("Level - ", ""));
                }

                ListManager.enemySetupsDict.Clear();
                ListManager.spawnObjectsDict.Clear();
                ListManager.extendedSetups.Clear();
                ListManager.extendedGroupCounts.Clear();
                ListManager.levelNumbers.Clear();
                ListManager.difficulty1Counts.Clear();
                ListManager.difficulty2Counts.Clear();
                ListManager.difficulty3Counts.Clear();

                int difficulty = 3;
                foreach (List<EnemySetup> list in difficulties)
                {
                    foreach (EnemySetup setup in list)
                    {
                        if (setup == null) continue;

                        ListManager.enemySetupsDict[setup.name] = setup;
                        foreach (PrefabRef spawnObject in setup.spawnObjects ?? new List<PrefabRef>())
                        {
                            if (spawnObject != null)
                                ListManager.spawnObjectsDict[spawnObject.PrefabName] = spawnObject;
                        }

                        if (!ListManager.extendedSetups.ContainsKey(setup.name))
                            ListManager.extendedSetups.Add(setup.name, new ExtendedEnemySetup(setup, difficulty));
                    }
                    difficulty--;
                }

                int previousIndex = -1;
                for (int level = 0; level < 21; level++)
                {
                    float firstCurvePosition = Mathf.Clamp01(level / 9f);
                    float secondCurvePosition = Mathf.Clamp01((level - 9) / 10f);

                    int count3;
                    int count2;
                    int count1;
                    if (secondCurvePosition > 0f)
                    {
                        count3 = (int)instance.amountCurve3_2.Evaluate(secondCurvePosition);
                        count2 = (int)instance.amountCurve2_2.Evaluate(secondCurvePosition);
                        count1 = (int)instance.amountCurve1_2.Evaluate(secondCurvePosition);
                    }
                    else
                    {
                        count3 = (int)instance.amountCurve3_1.Evaluate(firstCurvePosition);
                        count2 = (int)instance.amountCurve2_1.Evaluate(firstCurvePosition);
                        count1 = (int)instance.amountCurve1_1.Evaluate(firstCurvePosition);
                    }

                    if (level == 0 || count3 != ListManager.difficulty3Counts[previousIndex] ||
                        count2 != ListManager.difficulty2Counts[previousIndex] ||
                        count1 != ListManager.difficulty1Counts[previousIndex])
                    {
                        ListManager.levelNumbers.Add(level + 1);
                        ListManager.difficulty3Counts.Add(count3);
                        ListManager.difficulty2Counts.Add(count2);
                        ListManager.difficulty1Counts.Add(count1);
                        previousIndex++;
                    }
                }

                for (int i = 0; i < ListManager.levelNumbers.Count; i++)
                {
                    var groupCounts = new ExtendedGroupCounts(i);
                    ListManager.extendedGroupCounts[groupCounts.level] = groupCounts;
                }

                SpawnModuleState.ReadAndUpdateJSON();
                RemoveInvalidGroups();
                setupDone = true;
                SpawnModuleState.Logger.LogInfo("SpawnConfig enemy data initialized: " +
                    ListManager.extendedSetups.Count + " groups, " +
                    ListManager.spawnObjectsDict.Count + " spawn objects.");
            }
            catch (Exception ex)
            {
                SpawnModuleState.Logger.LogError("SpawnConfig initialization failed; vanilla enemy spawning will remain active. " + ex);
            }
        }

        private static void RemoveInvalidGroups()
        {
            List<string> invalidGroups = new List<string>();
            foreach (KeyValuePair<string, ExtendedEnemySetup> pair in ListManager.extendedSetups)
            {
                ExtendedEnemySetup setup = pair.Value;
                bool invalid = false;
                List<int> remove = new List<int>();

                for (int i = 0; i < (setup.spawnObjects?.Count ?? 0); i++)
                {
                    if (ListManager.spawnObjectsDict.ContainsKey(setup.spawnObjects[i])) continue;

                    if (SpawnModuleState.configManager.ignoreInvalidGroups.Value)
                    {
                        invalid = true;
                        SpawnModuleState.Logger.LogError("Unable to resolve enemy name '" + setup.spawnObjects[i] +
                            "' in group '" + setup.name + "'. This group will be ignored.");
                    }
                    else
                    {
                        remove.Add(i);
                        SpawnModuleState.Logger.LogError("Unable to resolve enemy name '" + setup.spawnObjects[i] +
                            "' in group '" + setup.name + "'. This enemy will be removed.");
                    }
                }

                for (int i = remove.Count - 1; i >= 0; i--)
                    setup.spawnObjects.RemoveAt(remove[i]);

                if (!invalid && setup.spawnObjects.Count == 0)
                    invalid = true;

                if (invalid)
                    invalidGroups.Add(pair.Key);
            }

            foreach (string group in invalidGroups)
                ListManager.extendedSetups.Remove(group);
        }

        [HarmonyPatch("AmountSetup")]
        [HarmonyPrefix]
        private static bool AmountSetupOverride(EnemyDirector __instance)
        {
            if (!SemiFunc.IsMasterClientOrSingleplayer())
                return true;

            if (!setupDone)
                SetupOnStart(__instance);
            if (!setupDone)
                return true;

            __instance.enemyListCurrent.Clear();
            __instance.enemyList.Clear();
            enemySpawnCount = 0;
            onlyOneSetup = false;

            __instance.enemiesDifficulty1.Clear();
            __instance.enemiesDifficulty2.Clear();
            __instance.enemiesDifficulty3.Clear();

            foreach (ExtendedEnemySetup setup in ListManager.extendedSetups.Values)
            {
                EnemySetup gameSetup = setup.GetEnemySetup();
                if (setup.difficulty1Weight > 0f) __instance.enemiesDifficulty1.Add(gameSetup);
                if (setup.difficulty2Weight > 0f) __instance.enemiesDifficulty2.Add(gameSetup);
                if (setup.difficulty3Weight > 0f) __instance.enemiesDifficulty3.Add(gameSetup);
            }

            AddFallbackSetup(__instance.enemiesDifficulty1);
            AddFallbackSetup(__instance.enemiesDifficulty2);
            AddFallbackSetup(__instance.enemiesDifficulty3);

            int currentLevel = RunManager.instance.levelsCompleted + 1;
            while (ListManager.previousSpawns.Count >= currentLevel)
                ListManager.previousSpawns.RemoveAt(0);
            while (ListManager.previousSpawns.Count < currentLevel)
                ListManager.previousSpawns.Add(new List<string>());

            int configKey = FindGroupCountConfig(currentLevel);
            if (configKey == 0)
                return true;

            int groupCount1;
            int groupCount2;
            int groupCount3;
            PickGroupCounts(__instance, ListManager.extendedGroupCounts[configKey], out groupCount1, out groupCount2, out groupCount3);

            __instance.totalAmount = groupCount1 + groupCount2 + groupCount3;
            for (int i = 0; i < groupCount3; i++) PickEnemiesCustom(__instance.enemiesDifficulty3, __instance);
            for (int i = 0; i < groupCount2; i++) PickEnemiesCustom(__instance.enemiesDifficulty2, __instance);
            for (int i = 0; i < groupCount1; i++) PickEnemiesCustom(__instance.enemiesDifficulty1, __instance);

            SpawnModuleState.Logger.LogInfo("Spawned a total of [" + __instance.totalAmount + "] enemy groups.");
            return false;
        }

        private static void AddFallbackSetup(List<EnemySetup> list)
        {
            if (list.Count > 0) return;
            EnemySetup fallback = ScriptableObject.CreateInstance<EnemySetup>();
            fallback.name = "Fallback";
            fallback.spawnObjects = new List<PrefabRef>();
            list.Add(fallback);
        }

        private static int FindGroupCountConfig(int currentLevel)
        {
            for (int level = currentLevel; level >= 1; level--)
            {
                if (ListManager.extendedGroupCounts.ContainsKey(level))
                    return level;
            }

            SpawnModuleState.Logger.LogError("No group-count configuration exists for level 1; using vanilla enemy setup.");
            return 0;
        }

        private static void PickGroupCounts(EnemyDirector director, ExtendedGroupCounts config,
            out int count1, out int count2, out int count3)
        {
            count1 = count2 = count3 = 0;
            int playerCount = 1;
            if (GameManager.instance != null && GameManager.instance.gameMode == 1)
                playerCount = Math.Max(1, LevelGenerator.Instance.ModulesReadyPlayers);

            List<GroupCountEntry> allEntries = config.possibleGroupCounts ?? new List<GroupCountEntry>();
            List<GroupCountEntry> selectable = allEntries
                .Where(entry => entry != null && playerCount >= entry.minPlayerCount &&
                    (entry.maxPlayerCount == 0 || playerCount <= entry.maxPlayerCount))
                .ToList();
            if (selectable.Count == 0)
                selectable = allEntries.Where(entry => entry != null).ToList();
            if (selectable.Count == 0)
                return;

            int totalWeight = selectable.Sum(entry => Math.Max(0, entry.weight));
            GroupCountEntry selected = selectable[0];
            if (totalWeight > 0)
            {
                int roll = UnityEngine.Random.Range(1, totalWeight + 1);
                foreach (GroupCountEntry entry in selectable)
                {
                    roll -= Math.Max(0, entry.weight);
                    if (roll <= 0)
                    {
                        selected = entry;
                        break;
                    }
                }
            }

            if (selected.counts == null)
                selected.counts = new List<int>();
            while (selected.counts.Count < 3)
                selected.counts.Add(0);

            count1 = Math.Max(0, selected.counts[0]);
            count2 = Math.Max(0, selected.counts[1]);
            count3 = Math.Max(0, selected.counts[2]);
            int multiplier = SpawnModuleState.configManager.groupCountMultiplier.Value;
            count1 *= multiplier;
            count2 *= multiplier;
            count3 *= multiplier;
        }

        public static void PickEnemiesCustom(List<EnemySetup> setups, EnemyDirector director)
        {
            if (!SemiFunc.IsMasterClientOrSingleplayer() || onlyOneSetup)
                return;

            currentDifficultyPick = setups == director.enemiesDifficulty1 ? 1
                : setups == director.enemiesDifficulty2 ? 2 : 3;

            int runsPlayed = DataDirector.instance != null
                ? DataDirector.instance.SettingValueFetch(DataDirector.Setting.RunsPlayed)
                : 0;
            string levelName = RunManager.instance.levelCurrent.name.Replace("Level - ", "");
            List<EnemySetup> possible = new List<EnemySetup>();
            float weightSum = 0f;

            foreach (EnemySetup enemy in setups)
            {
                if (enemy == null || enemy.name == "Fallback") continue;
                if (enemy.levelsCompletedCondition &&
                    (RunManager.instance.levelsCompleted < enemy.levelsCompletedMin ||
                     (enemy.levelsCompletedMax != -1 && RunManager.instance.levelsCompleted > enemy.levelsCompletedMax)))
                    continue;
                if (runsPlayed < enemy.runsPlayed) continue;

                float weight = ListManager.extendedSetups.TryGetValue(enemy.name, out var ext)
                    ? ext.GetWeight(currentDifficultyPick, director.enemyList, levelName)
                    : 1f;
                if (weight < 0.1f) continue;

                possible.Add(enemy);
                weightSum += weight;
            }

            if (possible.Count == 0 || weightSum <= 0f)
            {
                director.totalAmount--;
                return;
            }

            float roll = UnityEngine.Random.Range(0f, weightSum);
            EnemySetup selected = possible[possible.Count - 1];
            foreach (EnemySetup enemy in possible)
            {
                float weight = ListManager.extendedSetups[enemy.name]
                    .GetWeight(currentDifficultyPick, director.enemyList, levelName);
                roll -= weight;
                if (roll <= 0f)
                {
                    selected = enemy;
                    break;
                }
            }

            if (ListManager.extendedSetups.TryGetValue(selected.name, out var selectedExt) &&
                selectedExt.soloGroup)
            {
                director.enemyList.Clear();
                director.enemyList.Add(selected);
                director.totalAmount = 1;
                onlyOneSetup = true;
            }
            else
            {
                director.enemyList.Add(selected);
            }
        }

        [HarmonyPatch("DebugResult")]
        [HarmonyPrefix]
        private static void LogDebugResult()
        {
            SpawnModuleState.Logger.LogInfo("Spawned a total of [" + enemySpawnCount + "] enemy objects.");
        }
    }
}
