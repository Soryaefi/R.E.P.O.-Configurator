using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using RoomRangeConfig.SpawnModule.ExtendedClasses;

namespace RoomRangeConfig.SpawnModule
{
    // Credit: original logic by Index154 (REPO_SpawnConfig), CC BY-NC 4.0.
    public class SpawnJsonManager
    {
        public static List<ExtendedGroupCounts> GetGroupCountsFromJSON(string path)
        {
            List<ExtendedGroupCounts> temp = new List<ExtendedGroupCounts>();
            if (File.Exists(path))
            {
                string readFile = File.ReadAllText(path);
                if (!string.IsNullOrEmpty(readFile))
                {
                    temp = JsonConvert.DeserializeObject<List<ExtendedGroupCounts>>(readFile);
                }
            }
            return temp;
        }

        public static List<ExtendedEnemySetup> GetSpawnGroupsFromJSON(string path)
        {
            List<ExtendedEnemySetup> temp = new List<ExtendedEnemySetup>();
            if (File.Exists(path))
            {
                string readFile = File.ReadAllText(path);
                if (!string.IsNullOrEmpty(readFile))
                {
                    temp = JsonConvert.DeserializeObject<List<ExtendedEnemySetup>>(readFile);
                    if (!readFile.Contains("allowDuplicates"))
                    {
                        SpawnModuleState.missingProperties = true;
                    }
                }
            }
            return temp;
        }

        public static string SpawnGroupsToJSON(List<ExtendedEnemySetup> eesList)
        {
            StringBuilder json = new StringBuilder();
            StringWriter sw = new StringWriter(json);
            using (JsonWriter writer = new JsonTextWriter(sw))
            {
                writer.Formatting = Formatting.Indented;
                writer.WriteStartArray();
                foreach (ExtendedEnemySetup ees in eesList)
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("name");
                    writer.WriteValue(ees.name);
                    writer.WritePropertyName("levelRangeCondition");
                    writer.WriteValue(ees.levelRangeCondition);
                    writer.WritePropertyName("minLevel");
                    writer.WriteValue(ees.minLevel);
                    writer.WritePropertyName("maxLevel");
                    writer.WriteValue(ees.maxLevel);
                    writer.WritePropertyName("runsPlayed");
                    writer.WriteValue(ees.runsPlayed);
                    writer.WritePropertyName("spawnObjects");
                    writer.WriteStartArray();
                    foreach (string s in ees.spawnObjects)
                    {
                        writer.WriteValue(s);
                    }
                    writer.WriteEndArray();
                    writer.WritePropertyName("difficulty1Weight");
                    writer.WriteValue(ees.difficulty1Weight);
                    writer.WritePropertyName("difficulty2Weight");
                    writer.WriteValue(ees.difficulty2Weight);
                    writer.WritePropertyName("difficulty3Weight");
                    writer.WriteValue(ees.difficulty3Weight);
                    writer.WritePropertyName("levelWeightMultipliers");
                    writer.WriteStartObject();
                    writer.Formatting = Formatting.None;
                    int x = 0;
                    foreach (KeyValuePair<string, float> kvp in ees.levelWeightMultipliers)
                    {
                        if (x > 0) writer.WriteRaw(", ");
                        writer.WriteRaw("\"" + kvp.Key + "\": ");
                        writer.WriteRaw(kvp.Value.ToString("0.0", CultureInfo.InvariantCulture));
                        x++;
                    }
                    writer.WriteEndObject();
                    writer.Formatting = Formatting.Indented;
                    writer.WritePropertyName("soloGroup");
                    writer.WriteValue(ees.soloGroup);
                    writer.WritePropertyName("allowDuplicates");
                    writer.WriteValue(ees.allowDuplicates);
                    writer.WritePropertyName("alterAmountChance");
                    writer.WriteValue(ees.alterAmountChance);
                    writer.WritePropertyName("alterAmountMin");
                    writer.WriteValue(ees.alterAmountMin);
                    writer.WritePropertyName("alterAmountMax");
                    writer.WriteValue(ees.alterAmountMax);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            return json.ToString();
        }

        public static string GroupCountsToJSON(List<ExtendedGroupCounts> gcList)
        {
            StringBuilder json = new StringBuilder();
            StringWriter sw = new StringWriter(json);
            using (JsonWriter writer = new JsonTextWriter(sw))
            {
                writer.Formatting = Formatting.Indented;
                writer.WriteStartArray();
                foreach (ExtendedGroupCounts groupCounts in gcList)
                {
                    writer.Formatting = Formatting.Indented;
                    writer.WriteStartObject();
                    writer.WritePropertyName("level");
                    writer.WriteValue(groupCounts.level);
                    writer.WritePropertyName("possibleGroupCounts");
                    writer.WriteStartArray();
                    foreach (GroupCountEntry groupCountEntry in groupCounts.possibleGroupCounts)
                    {
                        writer.WriteStartObject();
                        writer.Formatting = Formatting.None;
                        writer.WriteRaw("\"counts\": ");
                        writer.WriteRaw("[");
                        writer.WriteRaw(groupCountEntry.counts[0] + ",");
                        writer.WriteRaw(groupCountEntry.counts[1] + ",");
                        writer.WriteRaw((groupCountEntry.counts[2]).ToString() ?? "");
                        writer.WriteRaw("], \"weight\": ");
                        writer.WriteRaw(groupCountEntry.weight.ToString());
                        writer.WriteRaw(", \"minPlayerCount\": ");
                        writer.WriteRaw(groupCountEntry.minPlayerCount.ToString());
                        writer.WriteRaw(", \"maxPlayerCount\": ");
                        writer.WriteRaw(groupCountEntry.maxPlayerCount.ToString());
                        writer.WriteEndObject();
                        writer.Formatting = Formatting.Indented;
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            return json.ToString();
        }
    }
}