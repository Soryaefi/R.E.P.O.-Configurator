using BepInEx.Configuration;
using HarmonyLib;

namespace RoomRangeConfig
{
    // ---------------------------------------------------------------------
    // CONFIRMED against decompiled GameManager.cs source. No guessing here:
    //   - GameManager.maxPlayersPhoton (public int, default 20) is the hard
    //     ceiling SetMaxPlayers clamps against.
    //   - GameManager.SetMaxPlayers(int) (public) does
    //     Math.Clamp(target, 1, maxPlayersPhoton) and applies it to
    //     PhotonNetwork/Steam/Discord as needed - all handled internally.
    //     So to raise the lobby size past the default ceiling, bump
    //     maxPlayersPhoton BEFORE calling SetMaxPlayers.
    // ---------------------------------------------------------------------
    public static class LobbySizeConfig
    {
        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<int> _maxPlayers;

        public static void Init(ConfigFile config)
        {
            _enabled = config.Bind(
                "Lobby Size", "Enabled", false,
                "Override the maximum number of players allowed in a lobby.");

            _maxPlayers = config.Bind(
                "Lobby Size", "Max Players", 6,
                new ConfigDescription("Maximum players per lobby when Enabled is checked.",
                    new AcceptableValueRange<int>(2, 50)));

            _enabled.SettingChanged += (_, _) => Apply();
            _maxPlayers.SettingChanged += (_, _) => Apply();
        }

        // Safe to call anytime GameManager.instance exists, including
        // mid-lobby - SetMaxPlayers already handles updating the live
        // Photon room (and Steam/Discord party size) if one exists.
        public static void Apply()
        {
            if (GameManager.instance == null || _enabled == null || !_enabled.Value)
                return;

            int target = _maxPlayers.Value;
            if (GameManager.instance.maxPlayersPhoton < target)
                GameManager.instance.maxPlayersPhoton = target;

            GameManager.instance.SetMaxPlayers(target);
            Plugin.Log.LogInfo($"[RoomRangeConfig] Lobby size override applied: {target} max players.");
        }
    }

    [HarmonyPatch(typeof(GameManager), "Awake")]
    public static class GameManagerAwakePatch
    {
        // GameManager is a DontDestroyOnLoad singleton set up very early -
        // this plugin's Harmony patches are already active by then (BepInEx
        // plugins load before the first scene), so this postfix reliably
        // fires right after GameManager.instance is assigned.
        private static void Postfix()
        {
            LobbySizeConfig.Apply();
        }
    }
}
