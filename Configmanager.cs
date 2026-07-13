using System;
using BepInEx.Configuration;

namespace RoomRangeConfig.SpawnModule
{
    // Credit: original logic by Index154 (REPO_SpawnConfig), CC BY-NC 4.0.
    // https://github.com/Index154/REPO_SpawnConfig
    public class SpawnConfigManager
    {
        internal void Setup(ConfigFile configFile)
        {
            preventSpawns = configFile.Bind("SpawnConfig - General", "Prevent enemy spawning", false,
                new ConfigDescription("Prevent enemy spawning entirely, turning the game into a no-stakes gathering simulator or for when you want to test something in peace", null, Array.Empty<object>()));
            addMissingGroups = configFile.Bind("SpawnConfig - General", "Re-add missing groups", false,
                new ConfigDescription("Whether the mod should update your custom SpawnGroups config at launch by adding all loaded enemy groups that are missing from it", null, Array.Empty<object>()));
            removeUnloadedLevelWeights = configFile.Bind("SpawnConfig - General", "Remove unused level weight multipliers", false,
                new ConfigDescription("Installing a mod which adds a new level will automatically add a config entry for it to the levelWeightMultipliers of every enemy group in your SpawnGroups config. These config entries will remain even if you later uninstall or disable that mod again. If you want to quickly get rid of unused entries in a situation like that then you can enable this setting and launch the game once to have the mod automatically remove them", null, Array.Empty<object>()));
            repeatMultiplier = configFile.Bind("SpawnConfig - General", "Repeat spawn weight multiplier", 0.5,
                new ConfigDescription("All three weights of an enemy group will be multiplied by this value for the current level after having been spawned once. Reduces the chance of encountering multiple copies of a group in the same level. Set to 1.0 to \"disable\"", null, Array.Empty<object>()));
            ignoreInvalidGroups = configFile.Bind("SpawnConfig - General", "Ignore groups with invalid spawnObjects", true,
                new ConfigDescription("If set to true, any group containing a single invalid spawn object will be ignored completely. If set to false, only the individual spawn object will be ignored and the group can still spawn as long as it contains at least one valid enemy", null, Array.Empty<object>()));
            groupCountMultiplier = configFile.Bind("SpawnConfig - General", "Group count multiplier", 1,
                new ConfigDescription("The amount of enemy groups spawned each level is multiplied by this number. This will apply on top of what you configure in `GroupsPerLevel.json`", new AcceptableValueRange<int>(1, 10), Array.Empty<object>()));
            enableVarietyPlus = configFile.Bind("SpawnConfig - Variety+", "Enable Variety+", true,
                new ConfigDescription("Set this to false to completely disable all the settings & systems in the Variety+ category", null, Array.Empty<object>()));
            consecutiveLevelCount = configFile.Bind("SpawnConfig - Variety+", "Consecutive level count", 2,
                new ConfigDescription("How many previous levels to take into account for applying the consecutive level weight multipliers. The min multiplier applies to groups spawned on the most recent level and the max multiplier applies to the least recent (equal to this setting)", new AcceptableValueRange<int>(2, 10), Array.Empty<object>()));
            consecutiveWeightMultiplierMin = configFile.Bind("SpawnConfig - Variety+", "Consecutive level weight multiplier min", 0.6,
                new ConfigDescription("If an enemy group has spawned exactly one level ago, its weights will be multiplied by this number. This value should be lower than or equal to the max multiplier", null, Array.Empty<object>()));
            consecutiveWeightMultiplierMax = configFile.Bind("SpawnConfig - Variety+", "Consecutive level weight multiplier max", 0.8,
                new ConfigDescription("An enemy group's weights will be multiplied by this number if it has spawned X levels ago with X being equal to your 'Consecutive level count' setting. For enemies spawned between X and 1 levels ago the game will apply a multiplier between your min and max corresponding to the difference", null, Array.Empty<object>()));
        }

        internal ConfigEntry<bool> preventSpawns;
        internal ConfigEntry<bool> addMissingGroups;
        internal ConfigEntry<bool> removeUnloadedLevelWeights;
        internal ConfigEntry<double> repeatMultiplier;
        internal ConfigEntry<bool> ignoreInvalidGroups;
        internal ConfigEntry<int> groupCountMultiplier;
        internal ConfigEntry<bool> enableVarietyPlus;
        internal ConfigEntry<int> consecutiveLevelCount;
        internal ConfigEntry<double> consecutiveWeightMultiplierMin;
        internal ConfigEntry<double> consecutiveWeightMultiplierMax;
    }
}