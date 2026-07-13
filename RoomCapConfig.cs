using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace RoomRangeConfig
{
    // Per-map "hard cap" that bypasses milestone-based room ranges entirely
    // for one specific map.
    public class MapRoomCapEntry
    {
        public ConfigEntry<bool> Enabled;
        public ConfigEntry<int> MaxRooms;
    }

    // Sections are created lazily, the first time a given map name is seen,
    // rather than pre-bound like Plugin's fixed MilestoneSlot list. REPO
    // supports custom/modded maps whose names aren't known ahead of time, so
    // a fixed slot list (like milestones use) isn't an option here - this is
    // the dynamic equivalent.
    public static class RoomCapManager
    {
        private static ConfigFile _config;
        private static readonly Dictionary<string, MapRoomCapEntry> _entries =
            new Dictionary<string, MapRoomCapEntry>(StringComparer.OrdinalIgnoreCase);

        public static void Init(ConfigFile config)
        {
            _config = config;
        }

        // Safe to call every level load - BepInEx's ConfigFile.Bind returns
        // the existing entry rather than duplicating it if the section
        // already exists, so this only actually creates new UI the first
        // time a given map name shows up.
        public static MapRoomCapEntry GetOrCreate(string mapName)
        {
            if (string.IsNullOrEmpty(mapName))
                mapName = "Unknown Map";

            if (_entries.TryGetValue(mapName, out var existing))
                return existing;

            string section = $"Room Cap - {mapName}";
            var entry = new MapRoomCapEntry
            {
                Enabled = _config.Bind(section, "Enabled", false,
                    "Enable a hard room-count cap for this specific map, ignoring milestone ranges entirely."),
                MaxRooms = _config.Bind(section, "Maximum Rooms", 10,
                    new ConfigDescription(
                        "Hard cap on room count for this map when Enabled is checked. Milestones are not used at all for this map while this is on.",
                        new AcceptableValueRange<int>(1, 100))),
            };

            _entries[mapName] = entry;
            Plugin.Log.LogInfo($"[RoomRangeConfig] Registered room cap slot for map '{mapName}'.");
            return entry;
        }

        // Returns true and sets `rooms` to the hard cap if this map has the
        // bypass enabled; otherwise returns false and the caller should fall
        // through to normal milestone logic.
        public static bool TryGetCap(string mapName, out int rooms)
        {
            var entry = GetOrCreate(mapName);
            if (entry.Enabled.Value)
            {
                rooms = entry.MaxRooms.Value;
                return true;
            }

            rooms = 0;
            return false;
        }
    }
}
