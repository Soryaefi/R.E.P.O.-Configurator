using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RoomRangeConfig
{
    // ---------------------------------------------------------------------
    // Two behavior changes, both funneled through the single confirmed
    // choke point EnemyParent.Despawn() - decompiled source shows it fires
    // for BOTH a kill (Health.healthCurrent <= 0, checked right inside the
    // method) and a natural idle despawn (triggered when EnemyParent.Logic()'s
    // SpawnedTimer runs out and the enemy isn't playerClose), so one Harmony
    // patch covers both cases:
    //
    //   1. RELOCATE ON DESPAWN (Postfix): whenever an enemy despawns - killed
    //      or natural - it's moved to a random LevelPoint that's "mod far"
    //      (see below) from every player, via the public Enemy.EnemyTeleported().
    //      This runs after DespawnRPC has already disabled the enemy's
    //      GameObject, so the move is invisible.
    //
    //   2. GATE THE NATURAL IDLE CYCLE (Prefix): a natural despawn is only
    //      allowed to actually happen if the enemy is already mod-far from
    //      every player at that moment. If not, the despawn is cancelled via
    //      EnemyParent.SpawnedTimerReset() (public - re-arms the timer and
    //      flips CurrentState back to Roaming) and the original Despawn() is
    //      skipped entirely (Prefix returns false), so no RPC/VFX/loot logic
    //      runs for a despawn that isn't happening. Once a natural despawn
    //      IS allowed through, its hidden duration is replaced with a short
    //      configurable value instead of the vanilla random range.
    //
    // "Mod far" = vanilla's only concrete distance-from-player constant
    // visible in decompiled source (the 20f "playerClose" radius in
    // EnemyParent.PlayerCloseLogic) multiplied by a configurable factor.
    // Flagging this since 20f isn't a named constant anywhere - it's the
    // closest concrete number available.
    //
    // Kills are never gated or timer-shortened - only relocated. Detecting
    // a kill requires reading Enemy.HasHealth / Enemy.Health / EnemyHealth.
    // healthCurrent, all internal fields in a different assembly, read via
    // AccessTools.Field like the rest of this codebase's internal-field
    // reads (see ValuablesConfig.cs). CONFIRMED against decompiled
    // EnemyHealth.cs: `internal int healthCurrent;`, and it's already 0 by
    // the time Despawn() can run for a kill - Hurt() sets it synchronously
    // on the hit that kills the enemy, well before the death
    // freeze/animation and eventual despawn. The reflective read still
    // fails safe (assumes "kill", skipping gating/shortening) if the field
    // is ever renamed by a future game update.
    //
    // Distinguishing the natural idle despawn from the other two despawn
    // triggers that also route through this same method - ChaseDespawnLogic
    // (chase timeout) and StuckDespawnLogic (stuck count), both in
    // EnemyStateDespawn.cs - matters because those exist as safety valves
    // and must never be blocked (a blocked stuck-enemy despawn would soft-lock
    // it in place). The heuristic used is EnemyParent.SpawnedTimer <= 0f,
    // which is the exact condition vanilla's Logic() coroutine checks before
    // setting CurrentState = Despawn for the idle case. It isn't a perfectly
    // exclusive signal (chase/stuck despawns could coincidentally line up
    // with SpawnedTimer already being <= 0), so as a second safety net this
    // patch never cancels the same enemy's despawn twice within a few
    // seconds - it lets a borderline case through rather than risk a loop.
    //
    // A short grace period after an EnemyParent is first observed also skips
    // both the gate and the relocation, since EnemyParent.Logic() calls
    // Despawn() once immediately on setup (before its intended first-spawn
    // placement) and that call should never be touched.
    // ---------------------------------------------------------------------
    public static class EnemyRelocationConfig
    {
        // EnemyParent.PlayerCloseLogic: `float num = 20f;` is the vanilla
        // "playerClose" radius - the only concrete distance-from-player
        // constant visible in decompiled source.
        private const float VanillaFarDistance = 20f;

        private const float InitialGraceSeconds = 3f;
        private const float CancelCooldownSeconds = 3f;

        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<float> _farMultiplier;
        private static ConfigEntry<float> _naturalDespawnHiddenDuration;

        public static void Init(ConfigFile config)
        {
            _enabled = config.Bind(
                "Enemy Relocation", "Enabled", true,
                "Relocate enemies to a random LevelPoint far from every player whenever they despawn (killed or " +
                "natural), and only allow the natural idle despawn/respawn cycle to trigger once the enemy is " +
                "already far from every player.");

            _farMultiplier = config.Bind(
                "Enemy Relocation", "Mod Far Distance Multiplier", 1.75f,
                new ConfigDescription(
                    "Multiplier over vanilla's 'far' distance (20m, the only concrete distance-from-player constant " +
                    "found in decompiled source) used for both the relocation target and the natural-despawn gate. " +
                    "1.75x = 35m.",
                    new AcceptableValueRange<float>(1f, 4f)));

            _naturalDespawnHiddenDuration = config.Bind(
                "Enemy Relocation", "Natural Despawn Hidden Duration", 1.5f,
                new ConfigDescription(
                    "Seconds an enemy stays hidden after a natural idle despawn that was allowed through the gate, " +
                    "replacing the vanilla random despawn timer for that case only. Never affects despawns from being killed.",
                    new AcceptableValueRange<float>(0.1f, 10f)));
        }

        public static bool IsEnabled() => Plugin.IsEnabled() && _enabled != null && _enabled.Value;
        public static float GetModFarDistance() => VanillaFarDistance * _farMultiplier.Value;
        public static float GetNaturalDespawnHiddenDuration() => _naturalDespawnHiddenDuration.Value;

        // ------------------------------------------------------------
        // Reflection - internal fields in Assembly-CSharp, same pattern
        // as ValuablesConfig.cs's AccessTools.Field usage.
        // ------------------------------------------------------------
        private static readonly FieldInfo EnemyParentEnemyField =
            AccessTools.Field(typeof(EnemyParent), "Enemy");
        private static readonly FieldInfo EnemyHasHealthField =
            AccessTools.Field(typeof(Enemy), "HasHealth");
        private static readonly FieldInfo EnemyHealthField =
            AccessTools.Field(typeof(Enemy), "Health");

        // CONFIRMED against decompiled EnemyHealth.cs: `internal int healthCurrent;`.
        // Still resolved via reflection (it's internal, in a different assembly),
        // and still fails safe (IsKilledEnemy assumes "kill") if a future game
        // update ever renames it.
        private static readonly FieldInfo HealthCurrentField =
            AccessTools.Field(typeof(EnemyHealth), "healthCurrent");

        private static bool _loggedHealthFieldWarning;

        public static Enemy GetEnemy(EnemyParent parent)
        {
            return EnemyParentEnemyField?.GetValue(parent) as Enemy;
        }

        public static bool IsKilledEnemy(Enemy enemy)
        {
            if (enemy == null) return true;

            bool hasHealth = EnemyHasHealthField != null && (bool)EnemyHasHealthField.GetValue(enemy);
            if (!hasHealth) return false; // no health component - can't have been killed

            if (HealthCurrentField == null || EnemyHealthField == null)
            {
                if (!_loggedHealthFieldWarning)
                {
                    _loggedHealthFieldWarning = true;
                    Plugin.Log.LogWarning("[RoomRangeConfig] Could not resolve EnemyHealth.healthCurrent via reflection " +
                        "(confirmed present in the decompiled source this was built against - the game may have updated) - " +
                        "treating all despawns as kills (relocation still applies, gating/hidden-duration shortening will not).");
                }
                return true;
            }

            object healthComponent = EnemyHealthField.GetValue(enemy);
            if (healthComponent == null) return true;

            float current = Convert.ToSingle(HealthCurrentField.GetValue(healthComponent));
            return current <= 0f;
        }

        // ------------------------------------------------------------
        // Distance helpers - horizontal (XZ) distance, matching the
        // flattening EnemyParent.PlayerCloseLogic itself uses.
        // ------------------------------------------------------------
        private static float NearestPlayerDistance(Vector3 position)
        {
            float nearest = float.MaxValue;
            Vector3 flatPos = new Vector3(position.x, 0f, position.z);

            foreach (PlayerAvatar player in SemiFunc.PlayerGetList())
            {
                if (!player) continue;
                Vector3 flatPlayer = new Vector3(player.transform.position.x, 0f, player.transform.position.z);
                float d = Vector3.Distance(flatPos, flatPlayer);
                if (d < nearest) nearest = d;
            }

            return nearest; // no players in list -> stays float.MaxValue, i.e. "infinitely far"
        }

        public static bool IsFarFromAllPlayers(Vector3 position, float threshold)
        {
            return NearestPlayerDistance(position) >= threshold;
        }

        // Picks a random LevelPoint that's mod-far from every player. If none
        // qualify (small maps), falls back to whichever point is furthest from
        // its single nearest player - same "do something reasonable rather
        // than nothing" philosophy as EnemyRespawnRoomConfig's fallback.
        public static LevelPoint FindModFarRelocationPoint()
        {
            var points = LevelGenerator.Instance != null ? LevelGenerator.Instance.LevelPathPoints : null;
            if (points == null || points.Count == 0) return null;

            float modFar = GetModFarDistance();
            var farEnough = new List<LevelPoint>();

            LevelPoint bestFallback = null;
            float bestFallbackDist = -1f;

            foreach (var point in points)
            {
                if (!point) continue;

                float nearest = NearestPlayerDistance(point.transform.position);
                if (nearest >= modFar)
                    farEnough.Add(point);

                if (nearest > bestFallbackDist)
                {
                    bestFallbackDist = nearest;
                    bestFallback = point;
                }
            }

            if (farEnough.Count > 0)
                return farEnough[UnityEngine.Random.Range(0, farEnough.Count)];

            return bestFallback;
        }

        // ------------------------------------------------------------
        // Per-enemy timing state (grace period + anti-softlock cooldown),
        // stored off to the side via ConditionalWeakTable rather than
        // touching the game's own classes.
        // ------------------------------------------------------------
        private class FloatBox { public float Value; }

        private static readonly ConditionalWeakTable<EnemyParent, FloatBox> FirstSeen = new ConditionalWeakTable<EnemyParent, FloatBox>();
        private static readonly ConditionalWeakTable<EnemyParent, FloatBox> LastCancel = new ConditionalWeakTable<EnemyParent, FloatBox>();

        public static bool PastInitialGrace(EnemyParent instance)
        {
            var box = FirstSeen.GetValue(instance, _ => new FloatBox { Value = Time.time });
            return Time.time - box.Value > InitialGraceSeconds;
        }

        public static bool RecentlyCanceled(EnemyParent instance)
        {
            return LastCancel.TryGetValue(instance, out var box) && Time.time - box.Value < CancelCooldownSeconds;
        }

        public static void MarkCanceled(EnemyParent instance)
        {
            LastCancel.GetValue(instance, _ => new FloatBox()).Value = Time.time;
        }
    }

    [HarmonyPatch(typeof(EnemyParent), nameof(EnemyParent.Despawn))]
    public static class EnemyDespawnRelocatePatch
    {
        private static bool Prefix(EnemyParent __instance)
        {
            if (!EnemyRelocationConfig.IsEnabled()) return true;
            if (!EnemyRelocationConfig.PastInitialGrace(__instance)) return true;

            var enemy = EnemyRelocationConfig.GetEnemy(__instance);
            if (enemy == null) return true;

            if (EnemyRelocationConfig.IsKilledEnemy(enemy)) return true; // never gate a real kill

            // Idle-cycle heuristic - see class comment on EnemyRelocationConfig
            // for why this isn't a perfectly exclusive signal for chase/stuck
            // despawns, and why RecentlyCanceled() exists as a backstop.
            if (__instance.SpawnedTimer > 0f) return true;

            float modFar = EnemyRelocationConfig.GetModFarDistance();
            if (EnemyRelocationConfig.IsFarFromAllPlayers(enemy.transform.position, modFar))
                return true; // already far enough - let it through, Postfix handles the rest

            if (EnemyRelocationConfig.RecentlyCanceled(__instance))
                return true; // avoid repeatedly cancelling the same enemy in a tight loop

            __instance.SpawnedTimerReset();
            EnemyRelocationConfig.MarkCanceled(__instance);
            Plugin.Log.LogInfo($"[RoomRangeConfig] Natural despawn blocked for '{__instance.enemyName}' - " +
                $"not yet {modFar:0.#}m from all players.");
            return false;
        }

        private static void Postfix(EnemyParent __instance)
        {
            if (!EnemyRelocationConfig.IsEnabled()) return;
            if (!EnemyRelocationConfig.PastInitialGrace(__instance)) return;

            var enemy = EnemyRelocationConfig.GetEnemy(__instance);
            if (enemy == null) return;

            var point = EnemyRelocationConfig.FindModFarRelocationPoint();
            if (point == null) return;

            enemy.EnemyTeleported(point.transform.position);

            bool isKill = EnemyRelocationConfig.IsKilledEnemy(enemy);
            bool wasIdleDespawn = !isKill && __instance.SpawnedTimer <= 0f;

            if (wasIdleDespawn)
                __instance.DespawnedTimer = EnemyRelocationConfig.GetNaturalDespawnHiddenDuration();

            string reason = isKill ? "killed" : wasIdleDespawn ? "natural idle" : "other despawn";
            Plugin.Log.LogInfo($"[RoomRangeConfig] Enemy '{__instance.enemyName}' relocated to '{point.name}' on despawn ({reason}).");
        }
    }
}
