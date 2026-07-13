# RoomRangeConfig

Configure room count and extraction point count per level using milestones, editable in-game via [REPOConfig](https://thunderstore.io/c/repo/p/nickklmao/REPOConfig/).

## Status

**Confirmed and working:**

- `LevelGenerator.ModuleAmount` / `LevelGenerator.ExtractionAmount` — the fields
  that control room and extraction counts (`HarmonyTranspiler` on `TileGeneration`).
- `RunManager.instance.levelsCompleted` — current level number is `+ 1` of this.
- Per-map room cap bypass. Minimum room count floor is **1** everywhere it's
  configurable: milestone slots and per-map room caps. The map name used for
  each map's section comes from `LevelGenerator.Instance.Level.name` (a public
  field, confirmed against decompiled `LevelGenerator.cs`/`Level.cs`) rather
  than a guessed field.
- Extraction Points slider runs **1-10** and stores the actual extraction
  count directly — a slider set to 4 means 4 extraction points, no
  conversion needed. (An earlier version stored `value - 1` on a `-1..9`
  slider to work around what looked like a display quirk; that offset has
  been removed since the `-1..9` range itself was the confusing part, not a
  real save/display mismatch.)
- **Valuables (Max Amount / Max Value mode, per milestone, with real
  Minimum Amount / Minimum Value floors)** — confirmed against decompiled
  `ValuableObject.cs`/`ValuableDirector.cs`. `DollarValueSetLogic()` is
  patched directly (public method) to override
  `dollarValueOriginal`/`dollarValueCurrent` (both `internal`, read via
  reflection like `ModuleAmount`); this runs before the game adds the item's
  value into the level's haul goal, so it also shifts total map value, just
  not to an exact target. `ValuableDirector.SpawnValuable()` (the level's
  loot-generation method, called before a valuable's GameObject exists) is
  the count-cap hook — over-cap valuables are never spawned at all, rather
  than being spawned and destroyed afterward, so the level's cash goal never
  ends up counting loot that isn't actually there to extract.
  **Minimum Amount** (Max Amount mode) and **Minimum Total Value** (Max
  Value mode) are enforced with a top-up pass: the vanilla spawn loop runs
  untouched, then, right before `VolumesAndSwitchSetup()` (called once, near
  the end of `SetupHost`, after both the valuables loop and the cosmetic
  loot loop have finished), the mod checks its own tally of what actually
  spawned and force-spawns extra valuables into whichever `ValuableVolume`s
  are still genuinely free until the minimum is met. "Free" accounts for
  cosmetic props too, since they draw from the same per-type volume lists.
  This is hard-capped by the level's actual spawn points — if a level
  doesn't have enough of them, the minimum can't be fully reached and the
  mod logs that and stops, rather than inventing spawn points that don't
  exist.
- **Enemy respawn fallback on small maps** — confirmed against decompiled
  `Enemy.cs`. `Enemy.TeleportToPoint()` is patched: when the game's own
  point-search returns nothing (the small-map failure case), this picks the
  furthest `LevelPoint` from the truck/start point — or the only room, if
  there's just one — using `LevelGenerator.Instance.LevelPathPoints`/
  `LevelPathTruck` (both public), then calls the game's own
  `Enemy.EnemyTeleported()` to move it.
- **Lobby size (2-50 players)** — confirmed against decompiled `GameManager.cs`.
  `GameManager.maxPlayersPhoton` and `GameManager.SetMaxPlayers(int)` are both
  public; a postfix on `GameManager.Awake` applies your configured max on
  startup, and it's safe to change live too.
- **Enemy relocation on despawn + gated natural despawn cycle** — confirmed
  against decompiled `EnemyParent.cs`/`Enemy.cs`/`EnemyStateDespawn.cs`.
  `EnemyParent.Despawn()` is the single method that fires for both a kill and
  a natural idle despawn, so a Prefix/Postfix pair on it covers both:
  - **Postfix (always, on any despawn):** relocates the enemy to a random
    `LevelPoint` that's "mod far" — vanilla's only concrete distance-from-player
    constant (20m, from `EnemyParent.PlayerCloseLogic`) times a configurable
    multiplier — from every player, via the public `Enemy.EnemyTeleported()`.
    Runs after the enemy is already invisible, so the move isn't seen. Falls
    back to whichever `LevelPoint` is furthest from its nearest player if
    nothing qualifies as mod-far (small maps), same philosophy as the
    small-map respawn fallback below.
  - **Prefix (natural despawns only):** a natural idle despawn is only
    allowed to actually happen if the enemy is already mod-far from every
    player; otherwise the despawn is cancelled via the public
    `EnemyParent.SpawnedTimerReset()` and skipped entirely. Once allowed
    through, its hidden duration is replaced with a short configurable value
    instead of the vanilla random range. Kills are never gated or
    timer-shortened, only relocated.
  - Distinguishing the idle cycle from the game's two *other* despawn
    triggers that share this same method (chase-timeout and stuck-count
    despawns, both in `EnemyStateDespawn.cs`) uses `SpawnedTimer <= 0f` as a
    heuristic — the exact condition vanilla checks to trigger the idle case.
    It isn't perfectly exclusive, so the mod also never cancels the same
    enemy's despawn twice within a few seconds, to avoid any risk of
    soft-locking a stuck enemy in place.
  - Kill detection reads `Enemy.Health.healthCurrent` — confirmed against
    decompiled `EnemyHealth.cs` as `internal int healthCurrent;`, read via
    reflection since it's internal in a different assembly. It's already 0
    by the time a kill's `Despawn()` can run, since `Hurt()` sets it
    synchronously on the killing blow, well before the death
    freeze/animation. Fails safe (treats the despawn as a kill, skipping
    gating/shortening) if a future game update ever renames the field.

**Valuables note:** "total value spawned on the map" (an exact target sum)
and "per-item value range" are different mechanics. There's still no direct
"set the map's total to exactly X" hook, but Minimum Total Value gets you a
real floor — the total will be at least that much (or as close as the
level's spawn points allow), it just isn't pinned to an exact number. Max
Amount mode only caps/floors the level's own loot-generation pass
(`ValuableDirector.SpawnValuable`), not valuables dropped by dying enemies,
which spawn through a separate path.

**Not currently included:** enemy spawn-group milestones have been removed
for now — see the git history if you want to revisit that work later.

## A note on fragility

Transpilers work by pattern-matching IL bytecode (opcode sequences), not by
name — that's inherently more brittle than a prefix/postfix. If a future REPO
update changes `TileGeneration`'s internals even slightly, the pattern match
can fail. The plugin handles this gracefully: `TileGenerationTranspiler` logs
an error and returns the method unpatched if it can't find the expected
pattern, rather than crashing — so a game update will just silently disable
the mod's effect (with a log message) instead of breaking your game. The same
defensive philosophy is used for the newer reflection-based hooks above.

If that happens, re-check the pattern in dnSpy against the new
`Assembly-CSharp.dll` and adjust the opcode sequence in
`TileGenerationTranspiler` accordingly. The R.E.P.O. Modding Discord /
repomods.com wiki is the fastest place to check whether someone's already
found the updated pattern.

## Building

1. Open `RoomRangeConfig.csproj` and set `<ModManagerProfile>` to your actual
   profile name in Thunderstore Mod Manager (shown in its profile switcher —
   default is often "Default" or "Solo"). You don't need to touch anything
   involving your Windows username; `$(APPDATA)` resolves that automatically.
   - Using r2modman instead? Swap the `RepoBepInExDir` line to
     `$(APPDATA)\r2modmanPlus-local\REPO\profiles\$(ModManagerProfile)`.
   - Installed BepInEx directly into the Steam folder (no mod manager)?
     Set `<UseSteamInstallInstead>true</UseSteamInstallInstead>`.
2. Set `<SteamGameDir>` to your actual R.E.P.O. install path (Steam → right-click
   R.E.P.O. → Manage → Browse Local Files) — this is always needed regardless
   of the above, since `UnityEngine.dll`/`Assembly-CSharp.dll` only exist in
   the real game folder, never inside a mod manager profile.
3. `dotnet build -c Release`
   - If the paths are wrong, the build fails immediately with a clear error
     telling you which DLL it couldn't find, rather than a cryptic compiler error.
4. Copy `bin/Release/net472/RoomRangeConfig.dll` into your **profile's**
   `BepInEx/plugins/` folder (not the Steam folder, if you're using a mod
   manager) — e.g.
   `%APPDATA%\Thunderstore Mod Manager\DataFolder\REPO\profiles\<YourProfile>\BepInEx\plugins\`.

## Config format

Edit via REPOConfig in-game, or directly in
`BepInEx/config/yourname.roomrangeconfig.cfg` after first launch:

```json
[
  { "level": 1, "minRooms": 5, "maxRooms": 7, "extractions": 1 },
  { "level": 4, "minRooms": 7, "maxRooms": 9, "extractions": 2 },
  { "level": 8, "minRooms": 9, "maxRooms": 12, "extractions": 3 }
]
```

Each milestone applies from its `level` onward until the next milestone's level
is reached — no interpolation, it's a flat step function as requested. Levels
below the lowest milestone's level fall back to the lowest milestone.

## Per-map room cap bypass

Under a `Room Cap - <map name>` section (one appears automatically the first
time you visit each map, including custom/modded maps — there's no fixed
list). Check `Enabled` and set `Maximum Rooms` to hard-cap that specific map's
room count, completely ignoring the milestone system for it. Other maps are
unaffected.

## Valuables (Max Amount / Max Value mode, per milestone)

Under `Valuables`, set the global `Mode` dropdown, then configure
`Valuables Milestone 1`, `2`, etc. — same slot pattern as the room milestones
(`Enabled` checkbox adds/removes a slot, `Milestone` field applies from that
level onward, stored as `actual level - 1`).

Three modes:

- **Disabled (default):** no effect, vanilla valuables behavior.
- **Max Amount:** controls total item count.
  - `Maximum Count` caps how many valuables the level generates — over-cap
    valuables are never spawned in the first place.
  - `Minimum Amount` forces extra valuables to spawn (into whatever spawn
    points the level has left over after its normal generation pass) until
    at least this many total valuables exist. Automatically kept at or below
    `Maximum Count`. 0 = no minimum.
- **Max Value:** controls cash value.
  - `Minimum Value` / `Maximum Value` reroll every spawned valuable's
    individual cash value.
  - `Minimum Total Value` forces extra valuables to spawn (each rolled
    within Minimum/Maximum Value) until the level's total cash value reaches
    this. Typed as plain digits (up to 5,000,000). 0 = no minimum.

Both minimums are hard-capped by however many valuable spawn points actually
exist in the level — if a level doesn't have enough, the mod logs that it
stopped short rather than inventing spawn points. See Status above for the
mechanism.

## Enemy respawn fallback (small maps)

Under `Enemy Respawn`, `Force Respawn Room On Small Maps` is on by default.
When an enemy can't find a valid teleport/respawn point on its own (common on
very small room counts), this forces it to the furthest room from the start —
or the only room, if there's just one. Confirmed hook — see Status above.

## Enemy relocation on despawn

Under `Enemy Relocation`, `Enabled` is on by default. `Mod Far Distance
Multiplier` (1x-4x, default 1.75x) sets how much larger than vanilla's 20m
"far" distance the mod's own far-from-player threshold is, used both for
picking a relocation point and for gating the natural idle despawn cycle.
`Natural Despawn Hidden Duration` (default 1.5s) is how briefly an enemy
stays hidden after a natural despawn that was allowed through the gate —
this never affects despawns from being killed. See Status above for the
full mechanism and its known heuristic limitations.

## Lobby size

Under `Lobby Size`: check `Enabled` and set `Max Players` (2-50). Applies on
startup and live if changed mid-session. Confirmed hook — see Status above.

## Requirements

- [BepInEx 5.4.2304 pack](https://thunderstore.io/c/repo/p/BepInEx/BepInExPack/) for REPO
- [REPOConfig](https://thunderstore.io/c/repo/p/nickklmao/REPOConfig/) (optional, but this is what gives you the in-game menu — without it, edit the `.cfg` file directly)
