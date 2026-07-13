using System.Collections.Generic;

namespace RoomRangeConfig.SpawnModule.ExtendedClasses
{
    // Credit: original logic by Index154 (REPO_SpawnConfig), CC BY-NC 4.0.
    public class ExtendedGroupCounts
    {
        public ExtendedGroupCounts() { }

        public ExtendedGroupCounts(int i)
        {
            level = ListManager.levelNumbers[i];
            possibleGroupCounts.Add(new GroupCountEntry(i));
        }

        public int level = 1;
        public List<GroupCountEntry> possibleGroupCounts = new List<GroupCountEntry>();
    }
}