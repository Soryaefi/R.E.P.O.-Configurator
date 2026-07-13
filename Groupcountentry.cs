using System.Collections.Generic;

namespace RoomRangeConfig.SpawnModule.ExtendedClasses
{
    // Credit: original logic by Index154 (REPO_SpawnConfig), CC BY-NC 4.0.
    public class GroupCountEntry
    {
        public GroupCountEntry() { }

        public GroupCountEntry(int i)
        {
            counts = new List<int>(3)
            {
                ListManager.difficulty1Counts[i],
                ListManager.difficulty2Counts[i],
                ListManager.difficulty3Counts[i]
            };
        }

        public List<int> counts = new List<int>();
        public int weight = 1;
        public int minPlayerCount = 1;
        public int maxPlayerCount = 0;
    }
}