using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RoomRangeConfig
{
    // ---------------------------------------------------------------------
    // CONFIRMED against decompiled Enemy.cs / LevelGenerator.cs source.
    //
    //   - Enemy.TeleportToPoint(minDistance, maxDistance) is the real
    //     respawn/reposition method. It tries
    //     SemiFunc.LevelPointGetPlayerDistance(...) twice (once requiring
    //     all extraction points completed, once not) and returns null if
    //     neither call finds a LevelPoint in [minDistance, maxDistance] of
    //     a player - exactly the failure mode on very small maps, where
    //     nothing is far enough away to qualify.
    //   - LevelGenerator.Instance.LevelPathPoints (public List<LevelPoint>)
    //     and LevelGenerator.Instance.LevelPathTruck (public LevelPoint)
    //     give everything needed to pick a fallback: if there's only one
    //     LevelPoint on the map, use it (it's the only room, so also the
    //     extraction room by definition); otherwise use whichever point is
    //     furthest from the truck/start point - the same "furthest from
    //     start" selection LevelGenerator.EnemySetup() already uses for
    //     initial enemy placement.
    //   - Enemy.EnemyTeleported(Vector3) (public) does the actual
    //     move-and-network-sync, so the fallback just calls that with the
    //     chosen point's position.
    //
    // No reflection or name-guessing needed here at all - every member
    // used below is public.
    // ---------------------------------------------------------------------
    public static class EnemyRespawnRoomConfig
    {
        private static ConfigEntry<bool> _enabled;

        public static void Init(ConfigFile config)
        {
            _enabled = config.Bind(
                "Enemy Respawn", "Force Respawn Room On Small Maps", true,
                "If an enemy can't find a valid teleport/respawn point (can happen on very small room counts), " +
                "fall back to the furthest room from the start - or the only room, if there's just one.");
        }

        public static bool IsEnabled() => Plugin.IsEnabled() && _enabled != null && _enabled.Value;

        public static LevelPoint PickFallbackPoint()
        {
            var points = LevelGenerator.Instance != null ? LevelGenerator.Instance.LevelPathPoints : null;
            if (points == null || points.Count == 0)
                return null;

            if (points.Count == 1)
                return points[0];

            var truck = LevelGenerator.Instance.LevelPathTruck;
            Vector3 origin = truck != null ? truck.transform.position : points[0].transform.position;

            LevelPoint furthest = null;
            float best = -1f;
            foreach (var point in points)
            {
                if (!point) continue;
                float d = Vector3.Distance(point.transform.position, origin);
                if (d > best)
                {
                    best = d;
                    furthest = point;
                }
            }
            return furthest;
        }
    }

    [HarmonyPatch(typeof(Enemy), nameof(Enemy.TeleportToPoint))]
    public static class EnemyTeleportFallbackPatch
    {
        private static void Postfix(Enemy __instance, ref LevelPoint __result)
        {
            if (__result != null) return;
            if (!EnemyRespawnRoomConfig.IsEnabled()) return;

            var fallback = EnemyRespawnRoomConfig.PickFallbackPoint();
            if (fallback == null) return;

            __instance.EnemyTeleported(fallback.transform.position);
            __result = fallback;

            Plugin.Log.LogInfo($"[RoomRangeConfig] Enemy respawn: no natural teleport point found, forced fallback to '{fallback.name}'.");
        }
    }
}
