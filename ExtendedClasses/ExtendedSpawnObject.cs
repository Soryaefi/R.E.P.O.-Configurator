using Newtonsoft.Json;

namespace RoomRangeConfig.SpawnModule.ExtendedClasses
{
    public class ExtendedSpawnObject
    {
        public ExtendedSpawnObject() { }

        public ExtendedSpawnObject(PrefabRef spawnObject)
        {
            name = spawnObject != null ? spawnObject.PrefabName : "Nameless";
        }

        public PrefabRef GetSpawnObject()
        {
            return ListManager.spawnObjectsDict[name];
        }

        public string name = "Nameless";
        public bool disabled = false;
        public int biggerGroupChance = 0;
        public int groupIncreaseAmount = 0;

        [JsonIgnore]
        public bool alteredGroupSize = false;
    }
}
