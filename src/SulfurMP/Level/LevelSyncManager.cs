using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MonoMod.RuntimeDetour;
using SulfurMP.Entities;
using SulfurMP.Networking;
using SulfurMP.Networking.Messages;
using SulfurMP.Players;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SulfurMP.Level
{
    /// <summary>
    /// Synchronizes level generation between host and clients.
    /// Host-authoritative: host decides when to change levels and broadcasts the seed.
    /// Clients block local level triggers and only load when instructed by host.
    ///
    /// Uses MonoMod Hook on GameManager.GoToLevel to intercept level transitions:
    /// - Host: pre-roll seed, set GlobalSettings.ForceLevelSeed, broadcast, call original
    /// - Client: block game-initiated calls, only load via received LevelSeedMessage
    /// </summary>
    public class LevelSyncManager : MonoBehaviour
    {
        public static LevelSyncManager Instance { get; private set; }

        // Current level state (host tracks for late-joiners)
        private byte _currentEnvironmentId;
        private int _currentLevelIndex;
        private long _currentSeed;
        public long CurrentSeed => _currentSeed;

        // Hook state
        private bool _hookAttempted;
        private Hook _goToLevelHook;
        private Hook _switchLevelRoutineHook;
        private Hook _completeLevelHook;
        private Hook _saveStashHook;
        private Hook _saveInventoryHook;
        private Hook _saveBackupHook;
        private Hook _clearSaveDataHook;
        private Hook _saveCheckpointsHook;
        private Hook _pauseGameHook;
        private Hook _setStateHook;
        private Hook _startNewLifeHook;
        private Hook _setTimeScaleHook;
        private Hook _playerDiedHook;
        private Hook _loadStashHook;
        private bool _isLoadingLevel; // Re-entrant guard
        public bool IsLoadingLevel => _isLoadingLevel;

        // Guard: prevents double-broadcast when GoToLevel calls SwitchLevelRoutine
        private static bool _switchLevelAlreadyHandled;

        // Pending level sync: non-null means hijack the next game-initiated GoToLevel
        private LevelSeedMessage _pendingLevelSync;

        // Clients that have finished loading (host-side tracking)
        private readonly HashSet<ulong> _clientsReady = new HashSet<ulong>();

        // Reflection cache
        private static Type _gameManagerType;
        private static Type _globalSettingsType;
        private static Type _worldEnvIdsType;
        private static Type _loadingModeType;
        private static PropertyInfo _gmInstanceProp;
        private static MethodInfo _goToLevelMethod;
        private static FieldInfo _forceLevelSeedField;
        private static MethodInfo _resumeGameMethod;
        private static PropertyInfo _playerObjectProp;
        private static MethodInfo _saveStashDataMethod;
        private static MethodInfo _saveInventoryDataMethod;
        private static Type _sulfurSavePCType;
        private static MethodInfo _saveBackupMethod;
        private static MethodInfo _clearSaveDataMethod;
        private static Type _playerProgressType;
        private static MethodInfo _saveCheckpointsMethod;
        private static MethodInfo _switchLevelRoutineMethod;
        private static MethodInfo _completeLevelMethod;
        private static MethodInfo _pauseGameMethod;
        private static MethodInfo _startNewLifeMethod;
        private static MethodInfo _playerDiedMethod;
        private static MethodInfo _setTimeScaleMethod;
        private static MethodInfo _loadStashDataMethod;
        private static PropertyInfo _currentGameStateProp;
        private static FieldInfo _currentGameStateField;
        private static object _gameStateLoading; // GameState.Loading enum value
        private static object _gameStateCinematic; // GameState.Cinematic enum value
        private static object _gameStatePaused; // GameState.Paused enum value
        private static MethodInfo _setStateMethod;

        // Player death detection for timeScale filtering
        private static PropertyInfo _playerUnitProp; // GameManager.PlayerUnit
        private static Type _unitType;
        private static FieldInfo _unitStateField; // Unit.unitState (public field)
        private static object _unitStateAlive; // UnitState.Alive enum value (=2)

        private static bool _reflectionInit;

        // LootManager reflection cache (for death side effects)
        private static Type _lootManagerType;
        private static bool _lootManagerTypeResolved;

        // MonoMod Hook delegates — use int for enum params (same underlying type at IL level)
        private delegate void orig_GoToLevel(object self, int envId, int levelIndex, int loadingMode, string spawnId);
        private delegate void hook_GoToLevel(orig_GoToLevel orig, object self, int envId, int levelIndex, int loadingMode, string spawnId);

        // Save method hook delegates — skip saves on multiplayer clients
        private delegate void orig_SaveStashData(object self, string stashIdentifier);
        private delegate void hook_SaveStashData(orig_SaveStashData orig, object self, string stashIdentifier);
        private delegate void orig_SaveInventoryData(object self);
        private delegate void hook_SaveInventoryData(orig_SaveInventoryData orig, object self);
        private delegate void orig_SaveBackup(object self);
        private delegate void hook_SaveBackup(orig_SaveBackup orig, object self);
        private delegate void orig_ClearSaveData(object self);
        private delegate void hook_ClearSaveData(orig_ClearSaveData orig, object self);

        // SwitchLevelRoutine hook delegates — private IEnumerator coroutine
        private delegate IEnumerator orig_SwitchLevelRoutine(object self, int envId, int levelIndex, int loadingMode, string spawnId);
        private delegate IEnumerator hook_SwitchLevelRoutine(orig_SwitchLevelRoutine orig, object self, int envId, int levelIndex, int loadingMode, string spawnId);

        // CompleteLevel hook delegates
        private delegate void orig_CompleteLevel(object self);
        private delegate void hook_CompleteLevel(orig_CompleteLevel orig, object self);

        // SaveCheckpoints hook delegates — static method: NO self param
        private delegate void orig_SaveCheckpoints(bool flushToDiskOnConsole);
        private delegate void hook_SaveCheckpoints(orig_SaveCheckpoints orig, bool flushToDiskOnConsole);

        // PauseGame hook delegates
        private delegate void orig_PauseGame(object self, bool showMenu);
        private delegate void hook_PauseGame(orig_PauseGame orig, object self, bool showMenu);

        // SetState hook delegates
        private delegate void orig_SetState(object self, int state);
        private delegate void hook_SetState(orig_SetState orig, object self, int state);

        // StartNewLife hook delegates
        private delegate void orig_StartNewLife(object self);
        private delegate void hook_StartNewLife(orig_StartNewLife orig, object self);

        // PlayerDied hook delegates
        private delegate void orig_PlayerDied(object self);
        private delegate void hook_PlayerDied(orig_PlayerDied orig, object self);

        // SetTimeScale hook delegates
        private delegate void orig_SetTimeScale(object self, float scale, float lerpDuration);
        private delegate void hook_SetTimeScale(orig_SetTimeScale orig, object self, float scale, float lerpDuration);

        // LoadStashData hook delegates — same signature as SaveStashData
        private delegate void orig_LoadStashData(object self, string stashIdentifier);
        private delegate void hook_LoadStashData(orig_LoadStashData orig, object self, string stashIdentifier);

        // Stored trampoline for calling the original GoToLevel
        private static orig_GoToLevel _origGoToLevel;

        private void Awake()
        {
            if (Instance != null)
            {
                Destroy(this);
                return;
            }
            Instance = this;
        }

        private void OnEnable()
        {
            NetworkEvents.OnMessageReceived += OnMessageReceived;
            NetworkEvents.OnPeerJoined += OnPeerJoined;
            NetworkEvents.OnDisconnected += OnDisconnected;
        }

        private void OnDisable()
        {
            NetworkEvents.OnMessageReceived -= OnMessageReceived;
            NetworkEvents.OnPeerJoined -= OnPeerJoined;
            NetworkEvents.OnDisconnected -= OnDisconnected;
        }

        private void OnDestroy()
        {
            DisposeHook();
            if (Instance == this)
                Instance = null;
        }

        private void Update()
        {
            if (!_hookAttempted)
                TryInstallHook();
        }

        #region Hook Installation

        private void TryInstallHook()
        {
            InitReflection();

            // Can't hook until we find the method
            if (_goToLevelMethod == null)
                return;

            _hookAttempted = true;

            try
            {
                _goToLevelHook = new Hook(
                    _goToLevelMethod,
                    new hook_GoToLevel(GoToLevelInterceptor));
                Plugin.Log.LogInfo("LevelSync: Installed MonoMod hook on GameManager.GoToLevel");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"LevelSync: Failed to hook GoToLevel: {ex}");
            }

            // Hook SwitchLevelRoutine — catch ALL level transitions (GoToLevel + CompleteLevel)
            if (_switchLevelRoutineMethod != null)
            {
                try
                {
                    _switchLevelRoutineHook = new Hook(
                        _switchLevelRoutineMethod,
                        new hook_SwitchLevelRoutine(SwitchLevelRoutineInterceptor));
                    Plugin.Log.LogInfo("LevelSync: Installed MonoMod hook on GameManager.SwitchLevelRoutine");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"LevelSync: Failed to hook SwitchLevelRoutine: {ex}");
                }
            }

            // Hook CompleteLevel — client blocks + sends request, host passes through
            if (_completeLevelMethod != null)
            {
                try
                {
                    _completeLevelHook = new Hook(
                        _completeLevelMethod,
                        new hook_CompleteLevel(CompleteLevelInterceptor));
                    Plugin.Log.LogInfo("LevelSync: Installed MonoMod hook on GameManager.CompleteLevel");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"LevelSync: Failed to hook CompleteLevel: {ex}");
                }
            }

            // Hook SaveStashData — skip/protect saves on multiplayer clients
            if (_saveStashDataMethod != null)
            {
                try
                {
                    _saveStashHook = new Hook(
                        _saveStashDataMethod,
                        new hook_SaveStashData(SaveStashDataInterceptor));
                    Plugin.Log.LogInfo("LevelSync: Installed MonoMod hook on GameManager.SaveStashData");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"LevelSync: Failed to hook SaveStashData: {ex}");
                }
            }

            // Hook SaveInventoryData — skip/protect saves on multiplayer clients
            if (_saveInventoryDataMethod != null)
            {
                try
                {
                    _saveInventoryHook = new Hook(
                        _saveInventoryDataMethod,
                        new hook_SaveInventoryData(SaveInventoryDataInterceptor));
                    Plugin.Log.LogInfo("LevelSync: Installed MonoMod hook on GameManager.SaveInventoryData");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"LevelSync: Failed to hook SaveInventoryData: {ex}");
                }
            }

            // Hook SaveBackup — skip on client, IOException catch on host
            if (_saveBackupMethod != null)
            {
                try
                {
                    _saveBackupHook = new Hook(
                        _saveBackupMethod,
                        new hook_SaveBackup(SaveBackupInterceptor));
                    Plugin.Log.LogInfo("LevelSync: Installed MonoMod hook on SulfurSave_PC.SaveBackup");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"LevelSync: Failed to hook SaveBackup: {ex}");
                }
            }

            // Hook PlayerProgress.SaveCheckpoints — skip on client, IOException catch on host (static method)
            if (_saveCheckpointsMethod != null)
            {
                try
                {
                    _saveCheckpointsHook = new Hook(
                        _saveCheckpointsMethod,
                        new hook_SaveCheckpoints(SaveCheckpointsInterceptor));
                    Plugin.Log.LogInfo("LevelSync: Installed MonoMod hook on PlayerProgress.SaveCheckpoints");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"LevelSync: Failed to hook SaveCheckpoints: {ex}");
                }
            }

            // Hook PauseGame — block in multiplayer
            if (_pauseGameMethod != null)
            {
                try
                {
                    _pauseGameHook = new Hook(
                        _pauseGameMethod,
                        new hook_PauseGame(PauseGameInterceptor));
                    Plugin.Log.LogInfo("LevelSync: Installed MonoMod hook on GameManager.PauseGame");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"LevelSync: Failed to hook PauseGame: {ex}");
                }
            }

            // Hook SetState — block GameState.Paused in multiplayer (single interception point
            // for ALL pause sources: ESC menu, inventory, dialog, cinematics, dev tools, etc.)
            if (_setStateMethod != null)
            {
                try
                {
                    _setStateHook = new Hook(
                        _setStateMethod,
                        new hook_SetState(SetStateInterceptor));
                    Plugin.Log.LogInfo("LevelSync: Installed MonoMod hook on GameManager.SetState");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"LevelSync: Failed to hook SetState: {ex}");
                }
            }

            // Hook StartNewLife — spectate on death in multiplayer
            if (_startNewLifeMethod != null)
            {
                try
                {
                    _startNewLifeHook = new Hook(
                        _startNewLifeMethod,
                        new hook_StartNewLife(StartNewLifeInterceptor));
                    Plugin.Log.LogInfo("LevelSync: Installed MonoMod hook on GameManager.StartNewLife");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"LevelSync: Failed to hook StartNewLife: {ex}");
                }
            }

            // Hook PlayerDied — skip death coroutine in multiplayer (prevents Destroy(PlayerObject))
            if (_playerDiedMethod != null)
            {
                try
                {
                    _playerDiedHook = new Hook(
                        _playerDiedMethod,
                        new hook_PlayerDied(PlayerDiedInterceptor));
                    Plugin.Log.LogInfo("LevelSync: Installed MonoMod hook on GameManager.PlayerDied");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"LevelSync: Failed to hook PlayerDied: {ex}");
                }
            }

            // Hook SetTimeScale — block slow-mo effects in multiplayer
            if (_setTimeScaleMethod != null)
            {
                try
                {
                    _setTimeScaleHook = new Hook(
                        _setTimeScaleMethod,
                        new hook_SetTimeScale(SetTimeScaleInterceptor));
                    Plugin.Log.LogInfo("LevelSync: Installed MonoMod hook on GameManager.SetTimeScale");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"LevelSync: Failed to hook SetTimeScale: {ex}");
                }
            }

            // Hook LoadStashData — catch IOException during level load (file contention)
            if (_loadStashDataMethod != null)
            {
                try
                {
                    _loadStashHook = new Hook(
                        _loadStashDataMethod,
                        new hook_LoadStashData(LoadStashDataInterceptor));
                    Plugin.Log.LogInfo("LevelSync: Installed MonoMod hook on GameManager.LoadStashData");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"LevelSync: Failed to hook LoadStashData: {ex}");
                }
            }

            // Hook ClearSaveData — block on client during multiplayer
            if (_clearSaveDataMethod != null)
            {
                try
                {
                    _clearSaveDataHook = new Hook(
                        _clearSaveDataMethod,
                        new hook_ClearSaveData(ClearSaveDataInterceptor));
                    Plugin.Log.LogInfo("LevelSync: Installed MonoMod hook on SulfurSave_PC.ClearSaveData");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"LevelSync: Failed to hook ClearSaveData: {ex}");
                }
            }
        }

        private void DisposeHook()
        {
            _goToLevelHook?.Dispose();
            _goToLevelHook = null;
            _switchLevelRoutineHook?.Dispose();
            _switchLevelRoutineHook = null;
            _completeLevelHook?.Dispose();
            _completeLevelHook = null;
            _saveStashHook?.Dispose();
            _saveStashHook = null;
            _saveInventoryHook?.Dispose();
            _saveInventoryHook = null;
            _saveBackupHook?.Dispose();
            _saveBackupHook = null;
            _clearSaveDataHook?.Dispose();
            _clearSaveDataHook = null;
            _saveCheckpointsHook?.Dispose();
            _saveCheckpointsHook = null;
            _pauseGameHook?.Dispose();
            _pauseGameHook = null;
            _setStateHook?.Dispose();
            _setStateHook = null;
            _startNewLifeHook?.Dispose();
            _startNewLifeHook = null;
            _playerDiedHook?.Dispose();
            _playerDiedHook = null;
            _setTimeScaleHook?.Dispose();
            _setTimeScaleHook = null;
            _loadStashHook?.Dispose();
            _loadStashHook = null;
            _hookAttempted = false;
        }

        #endregion

        #region GoToLevel Hook

        private static void GoToLevelInterceptor(
            orig_GoToLevel orig, object self,
            int envId, int levelIndex, int loadingMode, string spawnId)
        {
            // Always store the trampoline so we can invoke it later
            _origGoToLevel = orig;

            var instance = Instance;

            // Re-entrant call from our own code — pass through
            if (instance != null && instance._isLoadingLevel)
            {
                Plugin.Log.LogInfo($"LevelSync: Pass-through (isLoadingLevel) env={envId} level={levelIndex}");
                orig(self, envId, levelIndex, loadingMode, spawnId);
                return;
            }

            var net = NetworkManager.Instance;
            if (net == null || !net.IsConnected)
            {
                // Not in multiplayer — pass through to normal game behavior
                orig(self, envId, levelIndex, loadingMode, spawnId);
                return;
            }

            if (net.IsHost)
            {
                HandleHostGoToLevel(orig, self, envId, levelIndex, loadingMode, spawnId);
            }
            else
            {
                // Client path: check for pending hijack
                if (instance != null && instance._pendingLevelSync != null)
                {
                    // Hijack: game is doing its normal startup GoToLevel — redirect to host's level
                    var pending = instance._pendingLevelSync;
                    instance._pendingLevelSync = null;

                    // Capture old player before async scene load
                    var gmForOldPlayer = GetGameManager();
                    var oldPlayer = gmForOldPlayer != null ? GetPlayerObject(gmForOldPlayer) : null;

                    SetForceLevelSeed(pending.Seed);
                    Plugin.Log.LogInfo($"LevelSync [Client]: Hijacking GoToLevel: " +
                        $"env={envId}→{pending.EnvironmentId} level={levelIndex}→{pending.LevelIndex} " +
                        $"seed={pending.Seed}");

                    // Keep _isLoadingLevel true for entire transition — coroutine clears it
                    instance._isLoadingLevel = true;
                    try
                    {
                        orig(self, pending.EnvironmentId, pending.LevelIndex, loadingMode, pending.SpawnIdentifier);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogError($"LevelSync [Client]: Hijack GoToLevel failed: {ex}");
                        instance._isLoadingLevel = false;
                        return;
                    }
                    // NOTE: _isLoadingLevel stays true — ClientPostLoadCoroutine clears it

                    instance.StartCoroutine(instance.ClientPostLoadCoroutine(oldPlayer));
                    return;
                }

                // No pending hijack — decide based on LoadingMode
                // 0 = Normal (portal/stairs), 1 = Amulet, 2 = Death, 3 = Menu
                if (loadingMode == 0)
                {
                    // Portal/stairs: send request to host, who will initiate for everyone
                    Plugin.Log.LogInfo($"LevelSync [Client]: Sending level transition request to host " +
                        $"env={envId} level={levelIndex} spawn='{spawnId}'");
                    var request = new LevelTransitionRequestMessage
                    {
                        EnvironmentId = (byte)envId,
                        LevelIndex = levelIndex,
                        SpawnIdentifier = spawnId ?? "",
                    };
                    var netMgr = NetworkManager.Instance;
                    if (netMgr != null)
                        netMgr.SendToAll(request);
                }
                else
                {
                    // Death/Amulet/Menu: block silently (future phase)
                    Plugin.Log.LogInfo($"LevelSync [Client]: Blocked GoToLevel " +
                        $"env={envId} level={levelIndex} loadingMode={loadingMode}");
                }
            }
        }

        private static void HandleHostGoToLevel(
            orig_GoToLevel orig, object self,
            int envId, int levelIndex, int loadingMode, string spawnId)
        {
            var instance = Instance;

            // Exit spectate mode if active (level transition resets death state)
            SpectatorManager.Instance?.ExitSpectateMode();

            // Pre-roll a seed for deterministic level generation
            long seed = PreRollSeed();
            SetForceLevelSeed(seed);

            // Track current level state
            if (instance != null)
            {
                instance._currentEnvironmentId = (byte)envId;
                instance._currentLevelIndex = levelIndex;
                instance._currentSeed = seed;
                instance._clientsReady.Clear();
            }

            // Mark as handled so SwitchLevelRoutine hook doesn't double-broadcast
            _switchLevelAlreadyHandled = true;

            // Broadcast to all clients BEFORE generating
            var net = NetworkManager.Instance;
            var msg = new LevelSeedMessage
            {
                EnvironmentId = (byte)envId,
                LevelIndex = levelIndex,
                Seed = seed,
                SpawnIdentifier = spawnId ?? "",
            };
            net.SendToAll(msg);

            Plugin.Log.LogInfo($"LevelSync [Host]: GoToLevel env={envId} level={levelIndex} " +
                $"seed={seed} spawn='{spawnId}'");

            // Notify entity sync: clear registry, suppress individual broadcasts,
            // and start coroutine to wait for NPCs before sending batch
            EntitySyncManager.Instance?.OnLevelLoadStart();

            // Call original with the forced seed set
            if (instance != null) instance._isLoadingLevel = true;
            try
            {
                orig(self, envId, levelIndex, loadingMode, spawnId);
            }
            finally
            {
                if (instance != null) instance._isLoadingLevel = false;
            }
        }

        /// <summary>
        /// Intercepts SwitchLevelRoutine — the common path for ALL level transitions.
        /// For GoToLevel-initiated transitions, HandleHostGoToLevel already broadcast (flag set).
        /// For CompleteLevel-initiated transitions, this is where we broadcast.
        /// </summary>
        private static IEnumerator SwitchLevelRoutineInterceptor(
            orig_SwitchLevelRoutine orig, object self,
            int envId, int levelIndex, int loadingMode, string spawnId)
        {
            // Exit spectate mode on any level transition (idempotent — safe if not spectating)
            SpectatorManager.Instance?.ExitSpectateMode();

            var net = NetworkManager.Instance;

            if (net != null && net.IsConnected && net.IsHost && !_switchLevelAlreadyHandled)
            {
                // CompleteLevel path — HandleHostGoToLevel didn't run, so we broadcast here
                var instance = Instance;

                long seed = PreRollSeed();
                SetForceLevelSeed(seed);

                if (instance != null)
                {
                    instance._currentEnvironmentId = (byte)envId;
                    instance._currentLevelIndex = levelIndex;
                    instance._currentSeed = seed;
                    instance._clientsReady.Clear();
                }

                var msg = new LevelSeedMessage
                {
                    EnvironmentId = (byte)envId,
                    LevelIndex = levelIndex,
                    Seed = seed,
                    SpawnIdentifier = spawnId ?? "",
                };
                net.SendToAll(msg);

                Plugin.Log.LogInfo($"LevelSync [Host]: SwitchLevelRoutine (CompleteLevel path) " +
                    $"env={envId} level={levelIndex} seed={seed}");

                EntitySyncManager.Instance?.OnLevelLoadStart();
            }

            // Clear flag for next transition
            _switchLevelAlreadyHandled = false;

            return orig(self, envId, levelIndex, loadingMode, spawnId);
        }

        /// <summary>
        /// Intercepts CompleteLevel — called when player reaches a "complete level" portal
        /// (e.g., Caves 1 → Caves 2). Client blocks and sends request; host passes through.
        /// </summary>
        private static void CompleteLevelInterceptor(orig_CompleteLevel orig, object self)
        {
            var net = NetworkManager.Instance;
            if (net == null || !net.IsConnected)
            {
                // Not in multiplayer — pass through
                orig(self);
                return;
            }

            if (net.IsHost)
            {
                // Host: just run CompleteLevel normally
                // SwitchLevelRoutine hook will handle broadcast
                Plugin.Log.LogInfo("LevelSync [Host]: CompleteLevel called, passing through");
                orig(self);
            }
            else
            {
                // Client: block and send request to host
                Plugin.Log.LogInfo("LevelSync [Client]: CompleteLevel blocked, sending request to host");
                net.SendToAll(new CompleteLevelRequestMessage());
            }
        }

        #endregion

        #region Save Hooks

        private static void SaveStashDataInterceptor(orig_SaveStashData orig, object self, string stashIdentifier)
        {
            
            try
            {
                orig(self, stashIdentifier);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"LevelSync: SaveStashData exception (non-fatal): {ex.Message}");
            }
        }

        private static void SaveInventoryDataInterceptor(orig_SaveInventoryData orig, object self)
        {
            
            try
            {
                orig(self);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"LevelSync: SaveInventoryData exception (non-fatal): {ex.Message}");
            }
        }

        private static void SaveBackupInterceptor(orig_SaveBackup orig, object self)
        {
            
            try
            {
                orig(self);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"LevelSync: SaveBackup exception (non-fatal): {ex.Message}");
            }
        }

        private static void SaveCheckpointsInterceptor(orig_SaveCheckpoints orig, bool flushToDiskOnConsole)
        {
            // All players save — client progress (level completions, recipes) persists.
            try
            {
                orig(flushToDiskOnConsole);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"LevelSync: SaveCheckpoints exception (non-fatal): {ex.Message}");
            }
        }

        private static void LoadStashDataInterceptor(orig_LoadStashData orig, object self, string stashIdentifier)
        {
            
            try
            {
                orig(self, stashIdentifier);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"LevelSync: LoadStashData exception (non-fatal): {ex.Message}");
            }
        }

        private static void ClearSaveDataInterceptor(orig_ClearSaveData orig, object self)
        {
            var net = NetworkManager.Instance;
            if (net != null && net.IsConnected && !net.IsHost)
            {
                Plugin.Log.LogInfo("LevelSync [Client]: Blocking ClearSaveData (multiplayer client)");
                return;
            }

            orig(self);
        }

        #endregion

        #region Pause / Death Hooks

        // Guard: prevents re-broadcast when applying a received TimeScaleMessage
        private static bool _isRemoteTimeScaleChange;

        private static void SetTimeScaleInterceptor(orig_SetTimeScale orig, object self, float scale, float lerpDuration)
        {
            var net = NetworkManager.Instance;
            if (net == null || !net.IsConnected)
            {
                // Not in multiplayer — passthrough
                orig(self, scale, lerpDuration);
                return;
            }

            // During loading, each instance manages its own timeScale — no sync
            if (IsGameStateLoading(self))
            {
                orig(self, scale, lerpDuration);
                return;
            }

            // Remote synced changes always apply
            if (_isRemoteTimeScaleChange)
            {
                orig(self, scale, lerpDuration);
                return;
            }

            // If spectating, block all local effects
            var spectator = SpectatorManager.Instance;
            if (spectator != null && spectator.IsSpectating)
            {
                orig(self, 1f, 0f);
                return;
            }

            // Block sub-1.0 time scales (death slow-mo, punish triggers) — don't broadcast
            if (scale < 1f)
            {
                orig(self, 1f, 0f);
                return;
            }

            // Apply locally + broadcast
            orig(self, scale, lerpDuration);
            var msg = new TimeScaleMessage
            {
                Scale = scale,
                LerpDuration = lerpDuration,
            };
            net.SendToAll(msg);
        }

        /// <summary>
        /// Check if the game is in Loading state via reflection on GameManager.
        /// </summary>
        private static bool IsGameStateLoading(object gm)
        {
            if (_gameStateLoading == null)
                return false;
            try
            {
                object state = GetCurrentGameState(gm);
                return state != null && state.Equals(_gameStateLoading);
            }
            catch { return false; }
        }

        /// <summary>
        /// Check if the game is in Cinematic state (death screen, cutscenes).
        /// </summary>
        private static bool IsGameStateCinematic(object gm)
        {
            if (_gameStateCinematic == null)
                return false;
            try
            {
                object state = GetCurrentGameState(gm);
                return state != null && state.Equals(_gameStateCinematic);
            }
            catch { return false; }
        }

        private static object GetCurrentGameState(object gm)
        {
            if (_currentGameStateProp != null)
                return _currentGameStateProp.GetValue(gm);
            if (_currentGameStateField != null)
                return _currentGameStateField.GetValue(gm);
            return null;
        }

        /// <summary>
        /// Check if the local player unit is dead (unitState != Alive).
        /// Die() sets unitState=Dead BEFORE DoPlayerDeathEffects calls SetTimeScale(0.5, 2).
        /// </summary>
        private static bool IsLocalPlayerDead(object gm)
        {
            if (_playerUnitProp == null || _unitStateField == null || _unitStateAlive == null)
                return false;
            try
            {
                var playerUnit = _playerUnitProp.GetValue(gm);
                if (playerUnit == null || (playerUnit is UnityEngine.Object u && u == null))
                    return true; // No player unit = dead/destroyed
                var state = _unitStateField.GetValue(playerUnit);
                return state != null && !state.Equals(_unitStateAlive);
            }
            catch { return false; }
        }

        private static void PauseGameInterceptor(orig_PauseGame orig, object self, bool showMenu)
        {
            var net = NetworkManager.Instance;
            if (net != null && net.IsConnected)
            {
                // If spectating, block pause entirely — the pause menu covers the spectator
                // view and unlocks the cursor, making the spectator camera unusable
                var spectator = SpectatorManager.Instance;
                if (spectator != null && spectator.IsSpectating)
                    return;
            }

            // SetStateInterceptor handles blocking GameState.Paused → timeScale stays 1
            orig(self, showMenu);
        }

        /// <summary>
        /// Intercepts GameManager.SetState(GameState) — the single bottleneck where gameState
        /// gets assigned. In MP, blocks GameState.Paused from ever being set, which prevents
        /// Update() from setting timeScale=0. All menu UIs still open normally (they're shown
        /// by their own methods before ModifyGamePauseState calls SetState).
        /// </summary>
        private static void SetStateInterceptor(orig_SetState orig, object self, int state)
        {
            var net = NetworkManager.Instance;
            if (net != null && net.IsConnected &&
                _gameStatePaused != null && state == (int)_gameStatePaused)
                return; // Block Paused state in MP — keeps timeScale = 1

            orig(self, state);
        }

        /// <summary>
        /// Intercepts GameManager.PlayerDied() — the entry point for the death sequence.
        /// In multiplayer: skip entirely to prevent PlayerDiedRoutine from starting,
        /// which would Destroy(PlayerObject) and cause cascading NREs across 100+ systems.
        /// PlayerObject stays alive with unitState=Dead — NPCs naturally ignore dead units.
        /// </summary>
        private static void PlayerDiedInterceptor(orig_PlayerDied orig, object self)
        {
            var net = NetworkManager.Instance;
            if (net == null || !net.IsConnected)
            {
                // Not in multiplayer — normal single-player death flow
                orig(self);
                return;
            }

            // In MP: skip orig entirely — prevents PlayerDiedRoutine → Destroy(PlayerObject)
            // Unit.Die() already ran before this: unitState=Dead, death animation triggered,
            // SetTimeScale(0.5,2) caught by our hook and overridden to (1,0)
            Plugin.Log.LogInfo("LevelSync: PlayerDied intercepted — skipping death coroutine in MP");

            // Replicate per-player death side effects (matches single-player PlayerDiedRoutine behavior)
            // Order matters: save insured items FIRST, then clear inventory
            try
            {
                // 1. Save insured items + death-gold to church stash
                //    LootManager : StaticInstance<LootManager>, AddToChurchCollection() is public
                var lootManagerType = FindLootManagerType();
                if (lootManagerType != null)
                {
                    var lmInstanceProp = lootManagerType.GetProperty("Instance",
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                    var lmInstance = lmInstanceProp?.GetValue(null);
                    if (lmInstance != null && !(lmInstance is UnityEngine.Object uo && uo == null))
                    {
                        var addMethod = lootManagerType.GetMethod("AddToChurchCollection",
                            BindingFlags.Public | BindingFlags.Instance);
                        addMethod?.Invoke(lmInstance, null);
                        Plugin.Log.LogInfo("LevelSync: Church collection saved on death");
                    }
                }

                // 2. Clear inventory, resources, cached stats/buffs
                //    GameManager.ClearPlayerInventoryAndResources() is private
                var clearMethod = self.GetType().GetMethod("ClearPlayerInventoryAndResources",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (clearMethod != null)
                {
                    clearMethod.Invoke(self, null);
                    Plugin.Log.LogInfo("LevelSync: Inventory cleared on death");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"LevelSync: Failed to replicate death effects: {ex}");
            }

            // Broadcast death to all peers
            var deathMsg = new PlayerDeathMessage
            {
                SteamId = SteamUser.GetSteamID().m_SteamID,
                IsDead = true,
            };
            net.SendToAll(deathMsg);

            // Enter spectate mode
            var spectator = SpectatorManager.Instance;
            if (spectator != null)
                spectator.EnterSpectateMode();

            // Check if all players are dead (host only)
            CheckAllPlayersDead();
        }

        /// <summary>
        /// Safety net: StartNewLife should never be called in MP since we skip PlayerDied.
        /// If it somehow fires, block it to prevent DoStartup → GoToChurchHub.
        /// </summary>
        private static void StartNewLifeInterceptor(orig_StartNewLife orig, object self)
        {
            var net = NetworkManager.Instance;
            if (net == null || !net.IsConnected)
            {
                // Not in multiplayer — normal behavior
                orig(self);
                return;
            }

            // Safety net: PlayerDied is skipped in MP, so StartNewLife should never fire.
            // Block it to prevent unintended DoStartup → GoToChurchHub.
            Plugin.Log.LogWarning("LevelSync: StartNewLife unexpectedly called in MP — blocking");
        }

        /// <summary>
        /// Hide the death overlay UI via reflection:
        /// StaticInstance&lt;UIManager&gt;.Instance.deathOverlay.SetState(UIState.Hidden)
        /// </summary>
        private static void HideDeathOverlay()
        {
            try
            {
                Type uiManagerType = null;
                Type uiStateType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (uiManagerType == null)
                        uiManagerType = asm.GetType("PerfectRandom.Sulfur.Core.UI.UIManager");
                    if (uiStateType == null)
                        uiStateType = asm.GetType("PerfectRandom.Sulfur.Core.UI.UIState");
                    if (uiManagerType != null && uiStateType != null)
                        break;
                }

                if (uiManagerType == null || uiStateType == null)
                {
                    Plugin.Log.LogWarning($"LevelSync: Could not find UIManager ({uiManagerType != null}) or UIState ({uiStateType != null}) types");
                    return;
                }

                var instanceProp = uiManagerType.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                if (instanceProp == null)
                {
                    Plugin.Log.LogWarning("LevelSync: UIManager.Instance property not found");
                    return;
                }

                var uiMgr = instanceProp.GetValue(null);
                if (uiMgr == null || (uiMgr is UnityEngine.Object uObj && uObj == null))
                {
                    Plugin.Log.LogWarning("LevelSync: UIManager.Instance is null");
                    return;
                }

                var hiddenEnum = Enum.Parse(uiStateType, "Hidden");

                // 1. Hide death overlay (the "YOU DIED" text)
                var deathOverlayField = uiManagerType.GetField("deathOverlay",
                    BindingFlags.Public | BindingFlags.Instance);
                if (deathOverlayField != null)
                {
                    var deathOverlay = deathOverlayField.GetValue(uiMgr);
                    if (deathOverlay != null && !(deathOverlay is UnityEngine.Object dObj && dObj == null))
                    {
                        var setState = deathOverlay.GetType().GetMethod("SetState",
                            BindingFlags.Public | BindingFlags.Instance);
                        if (setState != null)
                        {
                            setState.Invoke(deathOverlay, new[] { hiddenEnum });
                            Plugin.Log.LogInfo("LevelSync: Hid death overlay");
                        }
                    }
                }

                // 2. Dismiss the loading fade (black screen that covers view during death transition)
                // PlayerDiedRoutine calls LoadingFade(true, Death) before StartNewLife
                var loadingFadeMethod = uiManagerType.GetMethod("LoadingFade",
                    BindingFlags.Public | BindingFlags.Instance);
                if (loadingFadeMethod != null)
                {
                    // LoadingFade(false) calls fadeEffect.FadeIn() to dismiss the black overlay
                    var fadeParams = loadingFadeMethod.GetParameters();
                    if (fadeParams.Length == 2)
                    {
                        // Second param is LoadingMode enum — convert int 0 (Normal) to the proper enum
                        var loadingModeParamType = fadeParams[1].ParameterType;
                        object loadingModeNormal = Enum.ToObject(loadingModeParamType, 0);
                        loadingFadeMethod.Invoke(uiMgr, new object[] { false, loadingModeNormal });
                    }
                    else if (fadeParams.Length == 1)
                    {
                        loadingFadeMethod.Invoke(uiMgr, new object[] { false });
                    }
                    Plugin.Log.LogInfo("LevelSync: Dismissed loading fade");
                }

                // 3. Also hide the loading overlay (loading screen UI with hints/progress)
                var loadingOverlayField = uiManagerType.GetField("loadingOverlay",
                    BindingFlags.Public | BindingFlags.Instance);
                if (loadingOverlayField != null)
                {
                    var loadingOverlay = loadingOverlayField.GetValue(uiMgr);
                    if (loadingOverlay != null && !(loadingOverlay is UnityEngine.Object lObj && lObj == null))
                    {
                        var setState = loadingOverlay.GetType().GetMethod("SetState",
                            BindingFlags.Public | BindingFlags.Instance);
                        if (setState != null)
                        {
                            setState.Invoke(loadingOverlay, new[] { hiddenEnum });
                            Plugin.Log.LogInfo("LevelSync: Hid loading overlay");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"LevelSync: Failed to hide death UI: {ex.Message}");
            }
        }

        /// <summary>
        /// Host-only: check if all players (local + remote) are dead.
        /// If so, transition everyone to ChurchHub.
        /// </summary>
        internal static void CheckAllPlayersDead()
        {
            var net = NetworkManager.Instance;
            if (net == null || !net.IsHost)
                return;

            var spectator = SpectatorManager.Instance;
            if (spectator == null || !spectator.AllPlayersDead())
                return;

            Plugin.Log.LogInfo("LevelSync: All players dead — transitioning to ChurchHub");

            spectator.ExitSpectateMode();

            var gm = GetGameManager();
            if (gm == null)
            {
                Plugin.Log.LogError("LevelSync: Cannot transition to ChurchHub — no GameManager");
                return;
            }

            // Clear run-tracking HashSets for fresh dungeon generation on next run
            try
            {
                var gmType = gm.GetType();
                foreach (var fieldName in new[] { "usedChunksThisRun", "usedUniqueEventThisRun", "usedUniqueEventThisEnvironment" })
                {
                    var field = gmType.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
                    if (field != null)
                    {
                        var hashSet = field.GetValue(gm);
                        hashSet?.GetType().GetMethod("Clear")?.Invoke(hashSet, null);
                    }
                }
                Plugin.Log.LogInfo("LevelSync: Run tracking cleared for next dungeon generation");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"LevelSync: Failed to clear run tracking: {ex}");
            }

            // ChurchHub = WorldEnvironmentIds value 1, LoadingMode.Death = 2
            InvokeGoToLevel(gm, 1, 0, 2, "Respawn");
        }

        #endregion

        #region Message Handling

        private void OnMessageReceived(CSteamID sender, NetworkMessage msg)
        {
            switch (msg.Type)
            {
                case MessageType.LevelSeed:
                    HandleLevelSeed(sender, (LevelSeedMessage)msg);
                    break;
                case MessageType.SceneReady:
                    HandleSceneReady(sender, (SceneReadyMessage)msg);
                    break;
                case MessageType.SceneChange:
                    HandleLevelTransitionRequest(sender, (LevelTransitionRequestMessage)msg);
                    break;
                case MessageType.LevelCompleteRequest:
                    HandleCompleteLevelRequest(sender);
                    break;
                case MessageType.PauseState:
                    HandleTimeScaleSync(sender, (TimeScaleMessage)msg);
                    break;
            }
        }

        private void HandleLevelSeed(CSteamID sender, LevelSeedMessage msg)
        {
            var net = NetworkManager.Instance;
            if (net == null || net.IsHost)
                return; // Only clients process this

            Plugin.Log.LogInfo($"LevelSync [Client]: Received LevelSeedMessage " +
                $"env={msg.EnvironmentId} level={msg.LevelIndex} seed={msg.Seed} " +
                $"spawn='{msg.SpawnIdentifier}'");

            _currentEnvironmentId = msg.EnvironmentId;
            _currentLevelIndex = msg.LevelIndex;
            _currentSeed = msg.Seed;

            // Set forced seed so the game generates the same level
            SetForceLevelSeed(msg.Seed);

            var gm = GetGameManager();

            if (gm != null)
            {
                // Path A: GameManager exists — redirect directly (works with or without PlayerObject)
                Plugin.Log.LogInfo("LevelSync [Client]: GameManager exists, redirecting to host's level");
                _pendingLevelSync = null;
                StartCoroutine(ClientRedirectCoroutine(msg));
            }
            else
            {
                // Path B: No GameManager yet (early startup) — hijack next GoToLevel + fallback
                Plugin.Log.LogInfo("LevelSync [Client]: No GameManager, will hijack next GoToLevel (with fallback)");
                _pendingLevelSync = msg;
                StartCoroutine(ClientFallbackLoadCoroutine(msg));
            }
        }

        /// <summary>
        /// Path A: GameManager exists — directly trigger GoToLevel to redirect to host's level.
        /// </summary>
        private IEnumerator ClientRedirectCoroutine(LevelSeedMessage msg)
        {
            Plugin.Log.LogInfo("LevelSync [Client]: Starting redirect coroutine (Path A — already in game)");

            // Exit spectate mode if active (level transition resets death state)
            SpectatorManager.Instance?.ExitSpectateMode();

            // One frame for state to settle
            yield return null;

            // Pre-transition cleanup
            var gm = GetGameManager();
            if (gm != null)
            {
                Plugin.Log.LogInfo("LevelSync [Client]: Clearing game pause state (pre-redirect)");
                ResumeGameViaReflection(gm);
            }

            // Re-apply forced seed (may have been cleared)
            SetForceLevelSeed(msg.Seed);

            // Re-fetch GM fresh for GoToLevel call
            gm = GetGameManager();
            if (gm == null)
            {
                Plugin.Log.LogError("LevelSync [Client]: GameManager null before redirect GoToLevel, aborting");
                yield break;
            }

            // Capture old player BEFORE GoToLevel — scene load is async, old player persists briefly
            var oldPlayer = GetPlayerObject(gm) as GameObject;

            // Diagnostic: check GameManager state
            var gmMono = gm as MonoBehaviour;
            var gmActive = gmMono != null && gmMono.gameObject.activeInHierarchy;
            var gmEnabled = gmMono != null && gmMono.enabled;
            var sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            Plugin.Log.LogInfo($"LevelSync [Client]: Redirect GoToLevel env={msg.EnvironmentId} " +
                $"level={msg.LevelIndex} seed={msg.Seed} " +
                $"(oldPlayer={(oldPlayer != null ? "exists" : "null")}, " +
                $"gmActive={gmActive}, gmEnabled={gmEnabled}, " +
                $"trampoline={((_origGoToLevel != null) ? "yes" : "NULL")}, scene={sceneName})");

            // Keep _isLoadingLevel true for the ENTIRE transition — game's async loading
            // pipeline may call GoToLevel again internally, and our hook must pass it through.
            // IMPORTANT: Use MethodInfo.Invoke (NOT the stored trampoline) — the trampoline
            // delegate captured from orig doesn't reliably call the real GoToLevel body.
            // MethodInfo.Invoke goes through our hook → _isLoadingLevel guard → orig → real method.
            _isLoadingLevel = true;
            try
            {
                Plugin.Log.LogInfo("LevelSync [Client]: Calling GoToLevel via reflection (through hook pass-through)");
                InvokeGoToLevel(gm, msg.EnvironmentId, msg.LevelIndex,
                    0 /* LoadingMode.Normal */, msg.SpawnIdentifier);
                Plugin.Log.LogInfo("LevelSync [Client]: GoToLevel call returned successfully");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"LevelSync [Client]: Failed to invoke GoToLevel: {ex}");
                _isLoadingLevel = false;
                yield break;
            }
            // NOTE: _isLoadingLevel stays true — cleared after transition completes

            // Wait for scene transition: old player destroyed → new player spawned
            yield return StartCoroutine(WaitForPlayerSpawnCoroutine(oldPlayer));

            // Post-load cleanup
            yield return null;
            _isLoadingLevel = false;
            ApplyPostLoadCleanup();
        }

        /// <summary>
        /// Path B post-load: GoToLevel was already called by the hijack in GoToLevelInterceptor.
        /// This coroutine just waits for player spawn and applies cleanup.
        /// </summary>
        private IEnumerator ClientPostLoadCoroutine(GameObject oldPlayer = null)
        {
            Plugin.Log.LogInfo("LevelSync [Client]: Starting post-load coroutine (Path B — hijacked GoToLevel)");

            // Wait for scene transition: old player destroyed → new player spawned
            yield return StartCoroutine(WaitForPlayerSpawnCoroutine(oldPlayer));

            // Post-load cleanup
            yield return null;
            _isLoadingLevel = false;
            ApplyPostLoadCleanup();
        }

        /// <summary>
        /// Path B fallback: waits for GameManager, then calls GoToLevel if the hijack hasn't fired.
        /// Runs in parallel with the hijack — whichever fires first wins via _pendingLevelSync null-check.
        /// </summary>
        private IEnumerator ClientFallbackLoadCoroutine(LevelSeedMessage msg)
        {
            Plugin.Log.LogInfo("LevelSync [Client]: Starting fallback coroutine (Path B — waiting for GameManager)");

            // Wait for GameManager
            float waited = 0f;
            const float maxWait = 30f;
            const float checkInterval = 0.5f;
            object gm = null;

            while (waited < maxWait)
            {
                // Hijack already handled it
                if (_pendingLevelSync == null)
                {
                    Plugin.Log.LogInfo("LevelSync [Client]: Fallback: hijack already handled level load, exiting");
                    yield break;
                }

                gm = GetGameManager();
                if (gm != null)
                    break;

                yield return new WaitForSecondsRealtime(checkInterval);
                waited += checkInterval;
            }

            if (gm == null)
            {
                Plugin.Log.LogError($"LevelSync [Client]: Fallback: GameManager not found after {maxWait}s, aborting");
                _pendingLevelSync = null;
                yield break;
            }

            Plugin.Log.LogInfo($"LevelSync [Client]: Fallback: GameManager found after {waited:F1}s");

            // Give the game a short window to call GoToLevel naturally (hijack path)
            yield return new WaitForSecondsRealtime(2f);

            // Check again — hijack may have fired during the wait
            if (_pendingLevelSync == null)
            {
                Plugin.Log.LogInfo("LevelSync [Client]: Fallback: hijack fired during wait, exiting");
                yield break;
            }

            // Hijack didn't fire — call GoToLevel ourselves
            _pendingLevelSync = null;
            Plugin.Log.LogInfo("LevelSync [Client]: Fallback: game didn't call GoToLevel, forcing redirect");

            // Re-fetch GM and do the redirect (same as Path A)
            gm = GetGameManager();
            if (gm == null)
            {
                Plugin.Log.LogError("LevelSync [Client]: Fallback: GameManager disappeared, aborting");
                yield break;
            }

            ResumeGameViaReflection(gm);
            SetForceLevelSeed(msg.Seed);

            gm = GetGameManager();
            if (gm == null) yield break;

            // Capture old player before async scene load
            var oldPlayer = GetPlayerObject(gm);

            Plugin.Log.LogInfo($"LevelSync [Client]: Fallback: GoToLevel env={msg.EnvironmentId} " +
                $"level={msg.LevelIndex} seed={msg.Seed}");

            _isLoadingLevel = true;
            try
            {
                Plugin.Log.LogInfo("LevelSync [Client]: Fallback: Calling GoToLevel via reflection");
                InvokeGoToLevel(gm, msg.EnvironmentId, msg.LevelIndex,
                    0 /* LoadingMode.Normal */, msg.SpawnIdentifier);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"LevelSync [Client]: Fallback: Failed to invoke GoToLevel: {ex}");
                _isLoadingLevel = false;
                yield break;
            }
            // NOTE: _isLoadingLevel stays true — cleared after transition completes

            yield return StartCoroutine(WaitForPlayerSpawnCoroutine(oldPlayer));
            yield return null;
            _isLoadingLevel = false;
            ApplyPostLoadCleanup();
        }

        /// <summary>
        /// Shared: Wait for scene transition to complete after GoToLevel (async/Addressables).
        /// Uses scene name change detection, then waits for PlayerObject to appear.
        /// </summary>
        private IEnumerator WaitForPlayerSpawnCoroutine(GameObject oldPlayer = null)
        {
            float waited = 0f;
            const float maxWait = 30f;
            const float checkInterval = 0.5f;

            // Phase 1: Wait for scene to actually change
            // KNOWN ISSUE: "GameScene" reloads into "GameScene" — scene name never changes.
            // Skip Phase 1 entirely for GameScene-to-GameScene transitions (go straight to player spawn wait).
            var startScene = SceneManager.GetActiveScene().name;
            Plugin.Log.LogInfo($"LevelSync [Client]: Scene detection (current: {startScene})...");

            if (startScene == "GameScene")
            {
                // GameScene → GameScene: scene name won't change. Instead wait for old player destruction.
                if (oldPlayer == null)
                {
                    // Player already destroyed (e.g., death sequence destroyed it before we got here)
                    Plugin.Log.LogInfo("LevelSync [Client]: GameScene detected, oldPlayer already null — skipping destruction wait");
                }
                else
                {
                    Plugin.Log.LogInfo("LevelSync [Client]: GameScene detected, waiting for old player destruction");
                    float destroyWait = 0f;
                    const float destroyMaxWait = 10f;
                    while (destroyWait < destroyMaxWait)
                    {
                        yield return new WaitForSecondsRealtime(checkInterval);
                        destroyWait += checkInterval;
                        waited += checkInterval;

                        // Old player destroyed = scene unloading started
                        if (oldPlayer != null && oldPlayer == null)
                        {
                            Plugin.Log.LogInfo($"LevelSync [Client]: Old player destroyed after {destroyWait:F1}s");
                            break;
                        }
                    }
                }
            }
            else
            {
                while (waited < maxWait)
                {
                    yield return new WaitForSecondsRealtime(checkInterval);
                    waited += checkInterval;

                    var currentScene = SceneManager.GetActiveScene().name;
                    if (currentScene != startScene)
                    {
                        Plugin.Log.LogInfo($"LevelSync [Client]: Scene changed: {startScene} → {currentScene} after {waited:F1}s");
                        break;
                    }

                    // Log progress every 5s
                    if (waited % 5f < checkInterval)
                    {
                        Plugin.Log.LogInfo($"LevelSync [Client]: Still waiting for scene change... ({waited:F1}s, " +
                            $"scene={currentScene}, oldPlayer={(oldPlayer != null ? "alive" : "gone")})");
                    }
                }

                if (SceneManager.GetActiveScene().name == startScene)
                {
                    Plugin.Log.LogWarning($"LevelSync [Client]: Scene did not change after {maxWait}s " +
                        $"(still: {startScene}), proceeding anyway");
                }
            }

            // Phase 2: Wait for new PlayerObject to appear
            Plugin.Log.LogInfo("LevelSync [Client]: Waiting for new player spawn...");
            float spawnWaited = 0f;
            bool playerSpawned = false;

            while (spawnWaited < maxWait)
            {
                yield return new WaitForSecondsRealtime(checkInterval);
                spawnWaited += checkInterval;

                var currentGm = GetGameManager();
                if (currentGm != null && GetPlayerObject(currentGm) != null)
                {
                    playerSpawned = true;
                    break;
                }
            }

            if (!playerSpawned)
            {
                Plugin.Log.LogWarning($"LevelSync [Client]: Player did not spawn after {spawnWaited:F1}s, " +
                    "proceeding with cleanup anyway");
            }
            else
            {
                Plugin.Log.LogInfo($"LevelSync [Client]: Player spawned after {spawnWaited:F1}s " +
                    $"(total wait: {waited + spawnWaited:F1}s)");
            }
        }

        /// <summary>
        /// Shared: Apply post-load cleanup — resume game, fix timeScale/cursor, send SceneReady.
        /// </summary>
        private void ApplyPostLoadCleanup()
        {
            Plugin.Log.LogInfo("LevelSync [Client]: Applying post-load cleanup");
            var gm = GetGameManager();
            if (gm != null)
            {
                ResumeGameViaReflection(gm);
            }
            Time.timeScale = 1f;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;

            // Notify entity sync: client ready for batch from host
            EntitySyncManager.Instance?.OnClientLevelLoadComplete();

            SendSceneReady();
            Plugin.Log.LogInfo("LevelSync [Client]: Client level transition complete");
        }

        private void HandleSceneReady(CSteamID sender, SceneReadyMessage msg)
        {
            var net = NetworkManager.Instance;
            if (net == null || !net.IsHost)
                return;

            _clientsReady.Add(msg.SteamId);
            Plugin.Log.LogInfo($"LevelSync [Host]: Client {msg.SteamId} scene-ready " +
                $"({_clientsReady.Count}/{net.ConnectedPeers.Count} clients)");
        }

        private void HandleLevelTransitionRequest(CSteamID sender, LevelTransitionRequestMessage msg)
        {
            var net = NetworkManager.Instance;
            if (net == null || !net.IsHost)
                return;

            if (_isLoadingLevel)
            {
                Plugin.Log.LogInfo($"LevelSync [Host]: Ignoring transition request from {sender} " +
                    $"(already loading)");
                return;
            }

            Plugin.Log.LogInfo($"LevelSync [Host]: Client {sender} requested level transition " +
                $"env={msg.EnvironmentId} level={msg.LevelIndex} spawn='{msg.SpawnIdentifier}'");

            var gm = GetGameManager();
            if (gm == null)
            {
                Plugin.Log.LogWarning("LevelSync [Host]: Cannot process transition request — no GameManager");
                return;
            }

            // Invoke GoToLevel on the host — this triggers HandleHostGoToLevel via our hook,
            // which pre-rolls seed, broadcasts LevelSeedMessage, and calls orig for everyone
            InvokeGoToLevel(gm, msg.EnvironmentId, msg.LevelIndex, 0 /* Normal */, msg.SpawnIdentifier);
        }

        private void HandleCompleteLevelRequest(CSteamID sender)
        {
            var net = NetworkManager.Instance;
            if (net == null || !net.IsHost)
                return;

            if (_isLoadingLevel)
            {
                Plugin.Log.LogInfo($"LevelSync [Host]: Ignoring complete-level request from {sender} " +
                    $"(already loading)");
                return;
            }

            Plugin.Log.LogInfo($"LevelSync [Host]: Client {sender} requested level completion");

            var gm = GetGameManager();
            if (gm == null)
            {
                Plugin.Log.LogWarning("LevelSync [Host]: Cannot process complete-level request — no GameManager");
                return;
            }

            // Invoke CompleteLevel on the host's GameManager
            // This goes through our CompleteLevel hook → orig → OnCompleteLevelRoutine → SwitchLevelRoutine hook → broadcast
            InvokeCompleteLevel(gm);
        }

        private void HandleTimeScaleSync(CSteamID sender, TimeScaleMessage msg)
        {
            var net = NetworkManager.Instance;
            if (net == null) return;

            // Host relays to other clients
            if (net.IsHost)
                net.SendToAllExcept(sender, msg);

            // Apply locally via SetTimeScale reflection (goes through our hook with guard)
            var gm = GetGameManager();
            if (gm == null) return;

            _isRemoteTimeScaleChange = true;
            try
            {
                if (_setTimeScaleMethod != null)
                    _setTimeScaleMethod.Invoke(gm, new object[] { msg.Scale, msg.LerpDuration });
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"LevelSync: Failed to apply remote timeScale: {ex.Message}");
            }
            finally
            {
                _isRemoteTimeScaleChange = false;
            }
        }

        #endregion

        #region Peer Events

        private void OnPeerJoined(CSteamID peerId)
        {
            var net = NetworkManager.Instance;
            if (net == null || !net.IsHost)
                return;

            // Late-joiner: send current level info if we're in a level
            if (_currentSeed != 0)
            {
                var msg = new LevelSeedMessage
                {
                    EnvironmentId = _currentEnvironmentId,
                    LevelIndex = _currentLevelIndex,
                    Seed = _currentSeed,
                    SpawnIdentifier = "",
                };
                net.SendMessage(peerId, msg);
                Plugin.Log.LogInfo($"LevelSync [Host]: Sent current level to late-joiner {peerId} " +
                    $"(env={_currentEnvironmentId} level={_currentLevelIndex} seed={_currentSeed})");
            }
        }

        private void OnDisconnected(string reason)
        {
            Plugin.Log.LogInfo($"LevelSync: Disconnected ({reason}), clearing state");
            _currentEnvironmentId = 0;
            _currentLevelIndex = 0;
            _currentSeed = 0;
            _clientsReady.Clear();
            _isLoadingLevel = false;
            _pendingLevelSync = null;
            _switchLevelAlreadyHandled = false;

            // Reset forced seed so single-player generates random levels again
            SetForceLevelSeed(0);

            // Return to ChurchHub after a short delay (let other OnDisconnected handlers finish)
            StartCoroutine(ReturnToChurchHubAfterDisconnect());
        }

        private IEnumerator ReturnToChurchHubAfterDisconnect()
        {
            // Wait 2 frames for all OnDisconnected handlers to complete
            yield return null;
            yield return null;

            // Skip if no GameManager or in MainMenu scene
            var gm = GetGameManager();
            if (gm == null)
                yield break;

            var sceneName = SceneManager.GetActiveScene().name;
            if (sceneName == "MainMenu" || sceneName == "StartupScene")
                yield break;

            Plugin.Log.LogInfo("LevelSync: Returning to ChurchHub after disconnect");

            // Resume game state (clear any pause/cinematic locks)
            ResumeGameViaReflection(gm);
            Time.timeScale = 1f;

            // Re-fetch GM after resume
            gm = GetGameManager();
            if (gm == null)
                yield break;

            // ChurchHub = WorldEnvironmentIds value 1, LoadingMode.Normal = 0
            InvokeGoToLevel(gm, 1, 0, 0, "Respawn");
        }

        #endregion

        #region Helpers

        private static long PreRollSeed()
        {
            var rng = new System.Random();
            return (long)rng.Next(int.MinValue, int.MaxValue);
        }

        private void SendSceneReady()
        {
            var net = NetworkManager.Instance;
            if (net == null || !net.IsConnected)
                return;

            var msg = new SceneReadyMessage
            {
                SteamId = SteamUser.GetSteamID().m_SteamID,
            };
            net.SendToAll(msg);
        }

        #endregion

        #region Reflection

        private static void InitReflection()
        {
            if (_reflectionInit) return;
            _reflectionInit = true;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (_gameManagerType == null)
                    _gameManagerType = asm.GetType("PerfectRandom.Sulfur.Core.GameManager");
                if (_globalSettingsType == null)
                    _globalSettingsType = asm.GetType("GlobalSettings")
                        ?? asm.GetType("PerfectRandom.Sulfur.Core.GlobalSettings");
                if (_worldEnvIdsType == null)
                    _worldEnvIdsType = asm.GetType("WorldEnvironmentIds")
                        ?? asm.GetType("PerfectRandom.Sulfur.Core.WorldEnvironmentIds");
                if (_loadingModeType == null)
                    _loadingModeType = asm.GetType("PerfectRandom.Sulfur.Core.LoadingMode")
                        ?? asm.GetType("LoadingMode");

                if (_gameManagerType != null && _globalSettingsType != null
                    && _worldEnvIdsType != null && _loadingModeType != null)
                    break;
            }

            // GameManager
            if (_gameManagerType != null)
            {
                _gmInstanceProp = _gameManagerType.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

                // Find GoToLevel (4 params) — handle potential overloads
                try
                {
                    _goToLevelMethod = _gameManagerType.GetMethod("GoToLevel",
                        BindingFlags.Public | BindingFlags.Instance);
                }
                catch (AmbiguousMatchException)
                {
                    // Multiple overloads — find the one with 4 parameters
                    _goToLevelMethod = _gameManagerType
                        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "GoToLevel" && m.GetParameters().Length == 4);
                }

                if (_goToLevelMethod != null)
                    Plugin.Log.LogInfo($"LevelSync: Found GoToLevel: {_goToLevelMethod}");
                else
                    Plugin.Log.LogWarning("LevelSync: Could not find GameManager.GoToLevel method");

                // ResumeGame — clears pause locks, controller locks, cursor state
                _resumeGameMethod = _gameManagerType.GetMethod("ResumeGame",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_resumeGameMethod != null)
                    Plugin.Log.LogInfo("LevelSync: Found GameManager.ResumeGame");
                else
                    Plugin.Log.LogWarning("LevelSync: Could not find GameManager.ResumeGame method");

                // PlayerObject — used to detect when the player has spawned after level load
                _playerObjectProp = _gameManagerType.GetProperty("PlayerObject",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_playerObjectProp != null)
                    Plugin.Log.LogInfo("LevelSync: Found GameManager.PlayerObject");
                else
                    Plugin.Log.LogWarning("LevelSync: Could not find GameManager.PlayerObject property");

                // SaveStashData — hooked to skip/protect saves on multiplayer clients
                _saveStashDataMethod = _gameManagerType.GetMethod("SaveStashData",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_saveStashDataMethod != null)
                    Plugin.Log.LogInfo("LevelSync: Found GameManager.SaveStashData");
                else
                    Plugin.Log.LogWarning("LevelSync: Could not find GameManager.SaveStashData method");

                // SaveInventoryData — hooked to skip/protect saves on multiplayer clients
                _saveInventoryDataMethod = _gameManagerType.GetMethod("SaveInventoryData",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_saveInventoryDataMethod != null)
                    Plugin.Log.LogInfo("LevelSync: Found GameManager.SaveInventoryData");
                else
                    Plugin.Log.LogWarning("LevelSync: Could not find GameManager.SaveInventoryData method");

                // LoadStashData — hooked to catch IOException from file contention
                _loadStashDataMethod = _gameManagerType.GetMethod("LoadStashData",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_loadStashDataMethod != null)
                    Plugin.Log.LogInfo("LevelSync: Found GameManager.LoadStashData");
                else
                    Plugin.Log.LogWarning("LevelSync: Could not find GameManager.LoadStashData method");

                // SwitchLevelRoutine — private coroutine, common path for ALL level transitions
                _switchLevelRoutineMethod = _gameManagerType.GetMethod("SwitchLevelRoutine",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (_switchLevelRoutineMethod != null)
                    Plugin.Log.LogInfo($"LevelSync: Found GameManager.SwitchLevelRoutine: {_switchLevelRoutineMethod}");
                else
                    Plugin.Log.LogWarning("LevelSync: Could not find GameManager.SwitchLevelRoutine method");

                // CompleteLevel — called when player reaches a level-end portal
                _completeLevelMethod = _gameManagerType.GetMethod("CompleteLevel",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_completeLevelMethod != null)
                    Plugin.Log.LogInfo("LevelSync: Found GameManager.CompleteLevel");
                else
                    Plugin.Log.LogWarning("LevelSync: Could not find GameManager.CompleteLevel method");

                // PauseGame — block in multiplayer to prevent timeScale=0
                _pauseGameMethod = _gameManagerType.GetMethod("PauseGame",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_pauseGameMethod != null)
                    Plugin.Log.LogInfo("LevelSync: Found GameManager.PauseGame");
                else
                    Plugin.Log.LogWarning("LevelSync: Could not find GameManager.PauseGame method");

                // SetState — single interception point for ALL pause sources
                _setStateMethod = _gameManagerType.GetMethod("SetState",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_setStateMethod != null)
                    Plugin.Log.LogInfo("LevelSync: Found GameManager.SetState");
                else
                    Plugin.Log.LogWarning("LevelSync: Could not find GameManager.SetState method");

                // StartNewLife — intercept death to enter spectate mode
                _startNewLifeMethod = _gameManagerType.GetMethod("StartNewLife",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_startNewLifeMethod != null)
                    Plugin.Log.LogInfo("LevelSync: Found GameManager.StartNewLife");
                else
                    Plugin.Log.LogWarning("LevelSync: Could not find GameManager.StartNewLife method");

                // PlayerDied — skip death coroutine in MP to prevent Destroy(PlayerObject)
                _playerDiedMethod = _gameManagerType.GetMethod("PlayerDied",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_playerDiedMethod != null)
                    Plugin.Log.LogInfo("LevelSync: Found GameManager.PlayerDied");
                else
                    Plugin.Log.LogWarning("LevelSync: Could not find GameManager.PlayerDied method");

                // SetTimeScale — block slow-mo effects in multiplayer
                _setTimeScaleMethod = _gameManagerType.GetMethod("SetTimeScale",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_setTimeScaleMethod != null)
                    Plugin.Log.LogInfo("LevelSync: Found GameManager.SetTimeScale");
                else
                    Plugin.Log.LogWarning("LevelSync: Could not find GameManager.SetTimeScale method");

                // CurrentGameState — used to detect Loading state for timeScale filtering
                _currentGameStateProp = _gameManagerType.GetProperty("CurrentGameState",
                    BindingFlags.Public | BindingFlags.Instance)
                    ?? _gameManagerType.GetProperty("currentGameState",
                        BindingFlags.Public | BindingFlags.Instance);
                if (_currentGameStateProp == null)
                {
                    // Try as field
                    var stateField = _gameManagerType.GetField("currentGameState",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                    if (stateField != null)
                    {
                        // Wrap field access in a PropertyInfo-like pattern isn't possible,
                        // so store the field and handle in IsGameStateLoading
                        _currentGameStateField = stateField;
                        Plugin.Log.LogInfo("LevelSync: Found GameManager.currentGameState (field)");
                    }
                }
                else
                {
                    Plugin.Log.LogInfo($"LevelSync: Found GameManager.CurrentGameState (property)");
                }

                // Resolve GameState.Loading enum value
                Type gameStateType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    gameStateType = asm.GetType("PerfectRandom.Sulfur.Core.GameState")
                        ?? asm.GetType("GameState");
                    if (gameStateType != null) break;
                }
                if (gameStateType != null)
                {
                    try
                    {
                        _gameStateLoading = Enum.Parse(gameStateType, "Loading");
                        Plugin.Log.LogInfo("LevelSync: Resolved GameState.Loading");
                    }
                    catch
                    {
                        Plugin.Log.LogWarning("LevelSync: Could not resolve GameState.Loading");
                    }
                    try
                    {
                        _gameStateCinematic = Enum.Parse(gameStateType, "Cinematic");
                        Plugin.Log.LogInfo("LevelSync: Resolved GameState.Cinematic");
                    }
                    catch
                    {
                        Plugin.Log.LogWarning("LevelSync: Could not resolve GameState.Cinematic");
                    }
                    try
                    {
                        _gameStatePaused = Enum.Parse(gameStateType, "Paused");
                        Plugin.Log.LogInfo("LevelSync: Resolved GameState.Paused");
                    }
                    catch
                    {
                        Plugin.Log.LogWarning("LevelSync: Could not resolve GameState.Paused");
                    }
                }
            }
            else
            {
                Plugin.Log.LogWarning("LevelSync: Could not find GameManager type");
            }

            // PlayerUnit + unitState — used to detect local player death for timeScale filtering
            if (_gameManagerType != null)
            {
                _playerUnitProp = _gameManagerType.GetProperty("PlayerUnit",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_playerUnitProp != null)
                    Plugin.Log.LogInfo("LevelSync: Found GameManager.PlayerUnit");
            }

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (_unitType == null)
                    _unitType = asm.GetType("PerfectRandom.Sulfur.Core.Units.Unit");
                if (_unitType != null) break;
            }

            if (_unitType != null)
            {
                _unitStateField = _unitType.GetField("unitState",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_unitStateField != null)
                    Plugin.Log.LogInfo("LevelSync: Found Unit.unitState");
            }

            Type unitStateType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                unitStateType = asm.GetType("PerfectRandom.Sulfur.Core.Units.UnitState");
                if (unitStateType != null) break;
            }
            if (unitStateType != null)
            {
                try
                {
                    _unitStateAlive = Enum.Parse(unitStateType, "Alive");
                    Plugin.Log.LogInfo("LevelSync: Resolved UnitState.Alive");
                }
                catch
                {
                    Plugin.Log.LogWarning("LevelSync: Could not resolve UnitState.Alive");
                }
            }

            // PlayerProgress.SaveCheckpoints — static method, causes IOException during level load
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                _playerProgressType = asm.GetType("PerfectRandom.Sulfur.Core.DataStorage.PlayerProgress");
                if (_playerProgressType != null)
                    break;
            }

            if (_playerProgressType != null)
            {
                _saveCheckpointsMethod = _playerProgressType.GetMethod("SaveCheckpoints",
                    BindingFlags.Public | BindingFlags.Static);
                if (_saveCheckpointsMethod != null)
                    Plugin.Log.LogInfo("LevelSync: Found PlayerProgress.SaveCheckpoints");
                else
                    Plugin.Log.LogWarning("LevelSync: Could not find PlayerProgress.SaveCheckpoints method");
            }
            else
            {
                Plugin.Log.LogWarning("LevelSync: Could not find PlayerProgress type");
            }

            // SulfurSave_PC — save system hooks (SaveBackup, ClearSaveData)
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                _sulfurSavePCType = asm.GetType("PerfectRandom.Sulfur.Core.SulfurSave_PC")
                    ?? asm.GetType("SulfurSave_PC");
                if (_sulfurSavePCType != null)
                    break;
            }

            if (_sulfurSavePCType != null)
            {
                _saveBackupMethod = _sulfurSavePCType.GetMethod("SaveBackup",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_saveBackupMethod != null)
                    Plugin.Log.LogInfo("LevelSync: Found SulfurSave_PC.SaveBackup");
                else
                    Plugin.Log.LogWarning("LevelSync: Could not find SulfurSave_PC.SaveBackup method");

                _clearSaveDataMethod = _sulfurSavePCType.GetMethod("ClearSaveData",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_clearSaveDataMethod != null)
                    Plugin.Log.LogInfo("LevelSync: Found SulfurSave_PC.ClearSaveData");
                else
                    Plugin.Log.LogWarning("LevelSync: Could not find SulfurSave_PC.ClearSaveData method");
            }
            else
            {
                Plugin.Log.LogWarning("LevelSync: Could not find SulfurSave_PC type");
            }

            // GlobalSettings.ForceLevelSeed
            if (_globalSettingsType != null)
            {
                _forceLevelSeedField = _globalSettingsType.GetField("ForceLevelSeed",
                    BindingFlags.Public | BindingFlags.Static);
                if (_forceLevelSeedField != null)
                    Plugin.Log.LogInfo("LevelSync: Found GlobalSettings.ForceLevelSeed");
                else
                    Plugin.Log.LogWarning("LevelSync: Could not find GlobalSettings.ForceLevelSeed field");
            }
            else
            {
                Plugin.Log.LogWarning("LevelSync: Could not find GlobalSettings type");
            }
        }

        private static object GetGameManager()
        {
            InitReflection();
            if (_gmInstanceProp == null) return null;
            try
            {
                var instance = _gmInstanceProp.GetValue(null);
                if (instance is UnityEngine.Object unityObj && unityObj == null)
                    return null;
                return instance;
            }
            catch { return null; }
        }

        private static Type FindLootManagerType()
        {
            if (_lootManagerTypeResolved) return _lootManagerType;
            _lootManagerTypeResolved = true;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    _lootManagerType = asm.GetType("PerfectRandom.Sulfur.Core.Items.LootManager");
                    if (_lootManagerType != null) return _lootManagerType;
                }
                catch { }
            }
            return null;
        }

        private static void SetForceLevelSeed(long seed)
        {
            InitReflection();
            if (_forceLevelSeedField == null)
            {
                Plugin.Log.LogError("LevelSync: Cannot set ForceLevelSeed — field not found");
                return;
            }
            try
            {
                _forceLevelSeedField.SetValue(null, seed);
                Plugin.Log.LogInfo($"LevelSync: Set GlobalSettings.ForceLevelSeed = {seed}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"LevelSync: Failed to set ForceLevelSeed: {ex}");
            }
        }

        private static void ResumeGameViaReflection(object gm)
        {
            InitReflection();
            if (_resumeGameMethod == null)
            {
                Plugin.Log.LogWarning("LevelSync: ResumeGame method not found, skipping");
                return;
            }
            try
            {
                _resumeGameMethod.Invoke(gm, null);
                Plugin.Log.LogInfo("LevelSync: Called GameManager.ResumeGame()");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"LevelSync: ResumeGame failed (may be fine if not paused): {ex.Message}");
            }
        }

        private static GameObject GetPlayerObject(object gm)
        {
            if (gm == null || _playerObjectProp == null) return null;
            try
            {
                var obj = _playerObjectProp.GetValue(gm) as GameObject;
                return obj != null ? obj : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Fallback: invoke GoToLevel via reflection when the orig trampoline isn't available.
        /// Converts int values to proper enum types for MethodInfo.Invoke.
        /// </summary>
        private static void InvokeGoToLevel(object gm, int envId, int levelIndex,
            int loadingMode, string spawnId)
        {
            if (_goToLevelMethod == null || _worldEnvIdsType == null || _loadingModeType == null)
            {
                Plugin.Log.LogError("LevelSync: Cannot invoke GoToLevel — missing reflection targets");
                return;
            }

            // Convert int values to the proper enum types
            object envIdEnum = Enum.ToObject(_worldEnvIdsType, envId);
            object loadingModeEnum = Enum.ToObject(_loadingModeType, loadingMode);

            _goToLevelMethod.Invoke(gm, new object[] { envIdEnum, levelIndex, loadingModeEnum, spawnId });
        }

        /// <summary>
        /// Invoke CompleteLevel on the GameManager via reflection.
        /// Goes through our CompleteLevel hook → orig → OnCompleteLevelRoutine → SwitchLevelRoutine.
        /// </summary>
        private static void InvokeCompleteLevel(object gm)
        {
            if (_completeLevelMethod == null)
            {
                Plugin.Log.LogError("LevelSync: Cannot invoke CompleteLevel — method not found");
                return;
            }

            _completeLevelMethod.Invoke(gm, null);
        }

        #endregion
    }
}
