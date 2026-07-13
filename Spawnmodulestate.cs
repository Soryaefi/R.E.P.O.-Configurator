using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using RoomRangeConfig.SpawnModule.ExtendedClasses;

namespace RoomRangeConfig.SpawnModule
{
    // Credit: original logic by Index154 (REPO_SpawnConfig), CC BY-NC 4.0
    // (https://github.com/Index154/REPO_SpawnConfig). Originally its own
    // BepInPlugin ("Index154.SpawnConfig"); folded in here as a plain static
    // module so it shares RoomRangeConfig's single plugin GUID, config file,
    // and Harmony instance instead of running as a second plugin.
    public static class SpawnModuleState
    {
        public static SpawnConfigManager configManager;
        public static ManualLogSource Logger;
        public static bool missingProperties;

        public static string exportPath;
        public static string spawnGroupsPath;
        public static string spawnGroupsBackupPath;
        public static string defaultSpawnGroupsPath;
        public static string groupsPerLevelPath;
        public static string defaultGroupsPerLevelPath;
        public static string groupsPerLevelExplainedPath;
        public static string spawnGroupsExplainedPath;

        // Called once from Plugin.Awake(), before harmony.PatchAll().
        public static void Setup(ConfigFile config, ManualLogSource logger)
        {
            Logger = logger;
            configManager = new SpawnConfigManager();
            configManager.Setup(config);

            exportPath = Path.Combine(Paths.ConfigPath, "SpawnConfig");
            spawnGroupsPath = Path.Combine(exportPath, "SpawnGroups.json");
            spawnGroupsBackupPath = Path.Combine(exportPath, "SpawnGroups_Backup.json");
            defaultSpawnGroupsPath = Path.Combine(exportPath, "Defaults", "SpawnGroups-Readonly.json");
            groupsPerLevelPath = Path.Combine(exportPath, "GroupsPerLevel.json");
            defaultGroupsPerLevelPath = Path.Combine(exportPath, "Defaults", "GroupsPerLevel-Readonly.json");
            groupsPerLevelExplainedPath = Path.Combine(exportPath, "GroupsPerLevel-Explained.json");
            spawnGroupsExplainedPath = Path.Combine(exportPath, "SpawnGroups-Explained.json");

            Directory.CreateDirectory(exportPath);
            Directory.CreateDirectory(Path.Combine(exportPath, "Defaults"));
        }

        public static void ReadAndUpdateJSON()
        {
            List<ExtendedEnemySetup> customSetupsList = SpawnJsonManager.GetSpawnGroupsFromJSON(spawnGroupsPath);
            List<ExtendedGroupCounts> customGroupCountsList = SpawnJsonManager.GetGroupCountsFromJSON(groupsPerLevelPath);
            List<ExtendedGroupCounts> extendedGroupCountsList = ListManager.extendedGroupCounts.Select(obj => obj.Value).ToList();

            File.WriteAllText(defaultGroupsPerLevelPath, SpawnJsonManager.GroupCountsToJSON(extendedGroupCountsList));
            if (customGroupCountsList.Count < 1)
            {
                Logger.LogInfo("No custom group count config found! Creating default file");
                File.WriteAllText(groupsPerLevelPath, SpawnJsonManager.GroupCountsToJSON(extendedGroupCountsList));
                customGroupCountsList = extendedGroupCountsList;
            }
            if (customGroupCountsList[0].level != 1)
            {
                Logger.LogError("Your custom group count config must contain at least a valid level 1 entry!");
                customGroupCountsList = extendedGroupCountsList;
            }

            List<ExtendedEnemySetup> extendedSetupsList = ListManager.extendedSetups.Select(obj => obj.Value).ToList();
            File.WriteAllText(defaultSpawnGroupsPath, SpawnJsonManager.SpawnGroupsToJSON(extendedSetupsList));
            if (customSetupsList.Count < 1)
            {
                Logger.LogInfo("No custom spawn groups config found! Creating default file");
                File.WriteAllText(spawnGroupsPath, SpawnJsonManager.SpawnGroupsToJSON(extendedSetupsList));
                customSetupsList = extendedSetupsList;
            }

            bool updatedFile = false;
            foreach (ExtendedEnemySetup custom in customSetupsList)
            {
                if (custom.Update()) updatedFile = true;
            }

            Dictionary<string, ExtendedEnemySetup> tempDict = customSetupsList.ToDictionary(obj => obj.name);
            foreach (KeyValuePair<string, ExtendedEnemySetup> source in ListManager.extendedSetups)
            {
                if (!tempDict.ContainsKey(source.Value.name) && configManager.addMissingGroups.Value)
                {
                    Logger.LogInfo("Missing group entry found: " + source.Value.name);
                    tempDict.Add(source.Value.name, source.Value);
                    updatedFile = true;
                }
            }
            customSetupsList = tempDict.Values.ToList();

            if (updatedFile || missingProperties)
            {
                Logger.LogInfo("-----------------------------------------------------------");
                Logger.LogInfo("Automatic changes have been made to SpawnConfig.json!");
                File.WriteAllText(spawnGroupsPath, SpawnJsonManager.SpawnGroupsToJSON(customSetupsList));
            }

            ListManager.extendedSetups = customSetupsList.ToDictionary(obj => obj.name);
            ListManager.extendedGroupCounts = customGroupCountsList.ToDictionary(obj => obj.level);
            File.WriteAllText(groupsPerLevelPath, SpawnJsonManager.GroupCountsToJSON(customGroupCountsList));

            if (File.Exists(spawnGroupsExplainedPath)) File.Delete(spawnGroupsExplainedPath);
            if (File.Exists(groupsPerLevelExplainedPath)) File.Delete(groupsPerLevelExplainedPath);
        }
    }
}