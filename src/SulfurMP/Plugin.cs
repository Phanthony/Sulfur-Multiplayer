using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using SulfurMP.Combat;
using SulfurMP.Config;
using SulfurMP.Entities;
using SulfurMP.Items;
using SulfurMP.Level;
using SulfurMP.Networking;
using SulfurMP.Patches;
using SulfurMP.Players;
using SulfurMP.UI;
using SulfurMP.Weapons;
using SulfurMP.World;
using UnityEngine;

namespace SulfurMP
{
    [BepInPlugin(PluginInfo.GUID, PluginInfo.NAME, PluginInfo.VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance { get; private set; }
        internal static ManualLogSource Log { get; private set; }
        private Harmony _harmony;
        internal static GameObject NetworkObject;
        internal static bool Initialized;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            MultiplayerConfig.Init(Config);

            _harmony = new Harmony(PluginInfo.GUID);

            // Bootstrap patches — hook into game Update methods to init once game loop runs
            BootstrapPatch.Apply(_harmony);

            // Apply any attribute-based patches
            _harmony.PatchAll();

            Log.LogInfo($"{PluginInfo.NAME} v{PluginInfo.VERSION} loaded!");
            Log.LogInfo("Waiting for game loop to bootstrap...");
        }

        /// <summary>
        /// Called from SteamManagerPatch once the game loop is running.
        /// Creates all SulfurMP components on a persistent GameObject.
        /// </summary>
        internal static void Bootstrap()
        {
            if (Initialized) return;
            Initialized = true;

            Log.LogInfo("SteamManager active — creating SulfurMP components...");

            NetworkObject = new GameObject("SulfurMP_Network");
            NetworkObject.hideFlags = HideFlags.HideAndDontSave;
            Object.DontDestroyOnLoad(NetworkObject);

            NetworkObject.AddComponent<NetworkManager>();
            NetworkObject.AddComponent<LobbyManager>();
            NetworkObject.AddComponent<DebugOverlay>();
            NetworkObject.AddComponent<MultiplayerPanel>();
            NetworkObject.AddComponent<PlayerReplicationManager>();
            NetworkObject.AddComponent<LocalPlayerSync>();
            NetworkObject.AddComponent<LevelSyncManager>();
            NetworkObject.AddComponent<EntitySyncManager>();
            NetworkObject.AddComponent<CombatSyncManager>();
            NetworkObject.AddComponent<EnemyAISyncManager>();
            NetworkObject.AddComponent<ItemSyncManager>();
            NetworkObject.AddComponent<SpectatorManager>();
            NetworkObject.AddComponent<WorldStateSyncManager>();
            NetworkObject.AddComponent<WeaponFireSyncManager>();
            NetworkObject.AddComponent<LevelGenDeterminismManager>();

            PauseMenuHook.Install();

            Log.LogInfo("SulfurMP fully initialized. F9 for debug overlay, Multiplayer via pause menu.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            NetworkEvents.ClearAll();

            if (NetworkObject != null)
                Object.Destroy(NetworkObject);
        }
    }

    internal static class PluginInfo
    {
        public const string GUID = "com.sulfurmp.multiplayer";
        public const string NAME = "SulfurMP";
        public const string VERSION = "0.1.0";
    }
}
