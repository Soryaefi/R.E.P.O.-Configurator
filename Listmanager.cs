using System.Collections.Generic;
using RoomRangeConfig.SpawnModule.ExtendedClasses;

namespace RoomRangeConfig.SpawnModule
{
    // Credit: original logic by Index154 (REPO_SpawnConfig), CC BY-NC 4.0.
    public class ListManager
    {
        public static double GetLevelNumMultiplier(int levelsAgo)
        {
            var cfg = SpawnModuleState.configManager;
            if (levelsAgo == 1)
            {
                return cfg.consecutiveWeightMultiplierMin.Value;
            }
            if (levelsAgo == cfg.consecutiveLevelCount.Value)
            {
                return cfg.consecutiveWeightMultiplierMax.Value;
            }
            double step = (cfg.consecutiveWeightMultiplierMax.Value - cfg.consecutiveWeightMultiplierMin.Value)
                          / (double)(cfg.consecutiveLevelCount.Value - 1);
            return cfg.consecutiveWeightMultiplierMin.Value + (levelsAgo - 1) * step;
        }

        public static Dictionary<string, EnemySetup> enemySetupsDict = new Dictionary<string, EnemySetup>();
        public static Dictionary<string, PrefabRef> spawnObjectsDict = new Dictionary<string, PrefabRef>();
        public static Dictionary<string, ExtendedEnemySetup> extendedSetups = new Dictionary<string, ExtendedEnemySetup>();
        public static Dictionary<string, ExtendedSpawnObject> extendedSpawnObjects = new Dictionary<string, ExtendedSpawnObject>();
        public static Dictionary<int, ExtendedGroupCounts> extendedGroupCounts = new Dictionary<int, ExtendedGroupCounts>();
        public static List<List<string>> previousSpawns = new List<List<string>>();
        public static List<int> difficulty1Counts = new List<int>();
        public static List<int> difficulty2Counts = new List<int>();
        public static List<int> difficulty3Counts = new List<int>();
        public static List<string> loadedLevelNames = new List<string>();
        public static List<int> levelNumbers = new List<int>();
    }
}