using System;
using System.Collections.Generic;
using System.Reflection;
using MonoMod.RuntimeDetour;
using SulfurMP.Networking;
using SulfurMP.Players;
using UnityEngine;

namespace SulfurMP.World
{
    /// <summary>
    /// Extends DistanceLODManager to keep rooms active near ALL players, not just the local camera.
    /// Fixes breakables/interactables being deactivated when a remote player is far from the host.
    ///
    /// Hook chain (LIFO): Unity → RoomLODSyncManager → SpectatorManager → original.
    /// - Spectating: pass through (SpectatorManager owns LOD)
    /// - No remote players: pass through (native LOD)
    /// - Remote players exist: lock → orig (Phase 1 only) → compute distances for all players → apply
    /// </summary>
    public class RoomLODSyncManager : MonoBehaviour
    {
        public static RoomLODSyncManager Instance { get; private set; }

        private Hook _lodLateUpdateHook;
        private bool _hookInstalled;
        private int _hookRetryCount;
        private const int MaxHookRetries = 300; // ~5 seconds at 60fps

        // Reflection — independent from SpectatorManager's private fields
        private static bool _reflectionInit;
        private static Type _distanceLODManagerType;
        private static MethodInfo _lodLateUpdateMethod;
        private static FieldInfo _lodLockedField;
        private static FieldInfo _lodForceRefreshField;
        private static FieldInfo _lodActiveStatesField;
        private static FieldInfo _lodDistancesSqField;
        private static PropertyInfo _lodPositionsProp;
        private static FieldInfo _lodLodsField;
        private static FieldInfo _lodGOsToActivateField;
        private static FieldInfo _lodGOsToDeactivateField;
        private static FieldInfo _lodQueueInteractorRefreshField;

        // RoomLODBase reflection
        private static PropertyInfo _roomLODActiveProperty;
        private static FieldInfo _roomLODRenderersField;
        private static FieldInfo _roomLODGameObjectsField;

        // Reusable list to avoid GC allocations
        private static readonly List<Vector3> _playerPositionsCache = new List<Vector3>(8);

        // MonoMod delegate types
        private delegate void orig_LodLateUpdate(object self);
        private delegate void hook_LodLateUpdate(orig_LodLateUpdate orig, object self);

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
            NetworkEvents.OnDisconnected += OnDisconnected;
        }

        private void OnDisable()
        {
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
            if (!_hookInstalled && _hookRetryCount < MaxHookRetries)
            {
                _hookRetryCount++;
                TryInstallHook();
            }
        }

        private void OnDisconnected(string reason)
        {
            // No cleanup needed — next LateUpdate will see no remote players and pass through
        }

        #region Hook Installation

        private void TryInstallHook()
        {
            if (_hookInstalled) return;

            InitReflection();
            if (_lodLateUpdateMethod == null)
            {
                if (_hookRetryCount >= MaxHookRetries)
                    Plugin.Log.LogWarning("RoomLODSyncManager: Could not find DistanceLODManager.LateUpdate after retries");
                return;
            }

            try
            {
                _lodLateUpdateHook = new Hook(
                    _lodLateUpdateMethod,
                    new hook_LodLateUpdate(LodLateUpdateInterceptor));
                _hookInstalled = true;
                Plugin.Log.LogInfo("RoomLODSyncManager: Installed MonoMod hook on DistanceLODManager.LateUpdate");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"RoomLODSyncManager: Failed to hook DistanceLODManager.LateUpdate: {ex}");
                _hookRetryCount = MaxHookRetries; // Stop retrying
            }
        }

        private void DisposeHook()
        {
            _lodLateUpdateHook?.Dispose();
            _lodLateUpdateHook = null;
            _hookInstalled = false;
        }

        #endregion

        #region Hook Logic

        private static void LodLateUpdateInterceptor(orig_LodLateUpdate orig, object self)
        {
            // Spectating → pass through entirely (SpectatorManager owns LOD)
            var spectator = SpectatorManager.Instance;
            if (spectator != null && spectator.IsSpectating)
            {
                orig(self);
                return;
            }

            // Not connected or no remote players → pass through (native LOD)
            var net = NetworkManager.Instance;
            var repl = PlayerReplicationManager.Instance;
            if (net == null || !net.IsConnected || repl == null || repl.RemotePlayers.Count == 0)
            {
                orig(self);
                return;
            }

            // Missing reflection → fall back to orig
            if (_lodLockedField == null || _lodActiveStatesField == null ||
                _lodDistancesSqField == null || _lodPositionsProp == null ||
                _lodLodsField == null)
            {
                orig(self);
                return;
            }

            // Gather all player positions (local camera + remote players)
            _playerPositionsCache.Clear();

            var cam = Camera.main;
            if (cam != null)
                _playerPositionsCache.Add(cam.transform.position);

            foreach (var kvp in repl.RemotePlayers)
            {
                if (kvp.Value != null && kvp.Value.gameObject != null)
                {
                    // Skip dead remote players
                    if (spectator != null && spectator.IsRemotePlayerDead(kvp.Key))
                        continue;
                    _playerPositionsCache.Add(kvp.Value.transform.position);
                }
            }

            if (_playerPositionsCache.Count == 0)
            {
                orig(self);
                return;
            }

            // Phase 1: Lock → let orig process activation/deactivation queues only
            try
            {
                _lodLockedField.SetValue(self, true);
                orig(self);
                _lodLockedField.SetValue(self, false);
            }
            catch
            {
                try { _lodLockedField.SetValue(self, false); } catch { }
            }

            // Phase 2+3: Compute distances for all players and apply
            try
            {
                RunMultiPlayerDistanceLod(self);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"RoomLODSyncManager: LOD update error: {ex.Message}");
            }
        }

        /// <summary>
        /// Distance-based LOD using all player positions. A room is active if ANY player
        /// is within its distance threshold.
        /// </summary>
        private static void RunMultiPlayerDistanceLod(object lodManager)
        {
            var activeStates = (bool[])_lodActiveStatesField.GetValue(lodManager);
            var distancesSq = (float[])_lodDistancesSqField.GetValue(lodManager);
            var worldPositions = (Vector3[])_lodPositionsProp.GetValue(lodManager);
            var forceRefresh = (bool)_lodForceRefreshField.GetValue(lodManager);

            if (activeStates == null || distancesSq == null || worldPositions == null)
                return;

            // Phase 2: Compute _activeStates — room active if ANY player is within range
            for (int i = 0; i < worldPositions.Length; i++)
            {
                activeStates[i] = false;
                float threshold = distancesSq[i];
                Vector3 roomPos = worldPositions[i];

                for (int j = 0; j < _playerPositionsCache.Count; j++)
                {
                    Vector3 playerPos = _playerPositionsCache[j];
                    float dx = roomPos.x - playerPos.x;
                    float dy = roomPos.y - playerPos.y;
                    float dz = roomPos.z - playerPos.z;
                    if (dx * dx + dy * dy + dz * dz < threshold)
                    {
                        activeStates[i] = true;
                        break;
                    }
                }
            }

            // Phase 3: Apply changes — compare Active vs _activeStates, queue activation/deactivation
            var lods = _lodLodsField.GetValue(lodManager) as System.Collections.IList;
            if (lods == null) return;

            var gosToActivate = _lodGOsToActivateField.GetValue(lodManager) as List<GameObject>;
            var gosToDeactivate = _lodGOsToDeactivateField.GetValue(lodManager) as List<GameObject>;

            for (int i = 0; i < lods.Count; i++)
            {
                var lod = lods[i];
                if (lod == null) continue;

                bool currentActive = (bool)_roomLODActiveProperty.GetValue(lod);
                bool newActive = activeStates[i];

                if (currentActive != newActive || forceRefresh)
                {
                    _roomLODActiveProperty.SetValue(lod, newActive);

                    // Set forceRenderingOff on renderers
                    var renderers = _roomLODRenderersField.GetValue(lod) as List<Renderer>;
                    if (renderers != null)
                    {
                        renderers.RemoveAll(r => r == null);
                        for (int j = 0; j < renderers.Count; j++)
                            renderers[j].forceRenderingOff = !newActive;
                    }

                    // Queue gameObjects for activation/deactivation
                    var gameObjects = _roomLODGameObjectsField.GetValue(lod) as List<GameObject>;
                    if (gameObjects != null)
                    {
                        gameObjects.RemoveAll(g => g == null);
                        if (newActive)
                            gosToActivate?.AddRange(gameObjects);
                        else
                            gosToDeactivate?.AddRange(gameObjects);
                    }

                    // Queue interactor refresh
                    if (_lodQueueInteractorRefreshField != null)
                        _lodQueueInteractorRefreshField.SetValue(lodManager, true);
                }
            }

            // Clear forceRefresh
            _lodForceRefreshField.SetValue(lodManager, false);
        }

        #endregion

        #region Reflection

        private static void InitReflection()
        {
            if (_reflectionInit) return;
            _reflectionInit = true;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (_distanceLODManagerType == null)
                    _distanceLODManagerType = asm.GetType("PerfectRandom.Sulfur.Core.DistanceLODManager");
                if (_distanceLODManagerType != null) break;
            }

            if (_distanceLODManagerType == null)
            {
                Plugin.Log.LogWarning("RoomLODSyncManager: Could not find DistanceLODManager type");
                return;
            }

            var bf = BindingFlags.NonPublic | BindingFlags.Instance;
            var bfPub = BindingFlags.Public | BindingFlags.Instance;

            _lodLateUpdateMethod = _distanceLODManagerType.GetMethod("LateUpdate", bf);
            _lodLockedField = _distanceLODManagerType.GetField("_locked", bf);
            _lodForceRefreshField = _distanceLODManagerType.GetField("_forceRefresh", bf);
            _lodActiveStatesField = _distanceLODManagerType.GetField("_activeStates", bf);
            _lodDistancesSqField = _distanceLODManagerType.GetField("_distancesSq", bf);
            _lodPositionsProp = _distanceLODManagerType.GetProperty("worldLODPositions", bfPub);
            _lodLodsField = _distanceLODManagerType.GetField("lods", bfPub);
            _lodGOsToActivateField = _distanceLODManagerType.GetField("_GOsToActivate", bf);
            _lodGOsToDeactivateField = _distanceLODManagerType.GetField("_GOsToDeactivate", bf);
            _lodQueueInteractorRefreshField = _distanceLODManagerType.GetField("_queueInteractorRefresh", bf);

            // RoomLODBase — parent of DistanceLOD
            Type roomLODBaseType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (roomLODBaseType == null)
                    roomLODBaseType = asm.GetType("PerfectRandom.Sulfur.Core.RoomLODBase");
                if (roomLODBaseType != null) break;
            }

            if (roomLODBaseType != null)
            {
                _roomLODActiveProperty = roomLODBaseType.GetProperty("Active", bfPub);
                _roomLODRenderersField = roomLODBaseType.GetField("renderers", bfPub);
                _roomLODGameObjectsField = roomLODBaseType.GetField("gameObjects", bfPub);
            }

            Plugin.Log.LogInfo($"RoomLODSyncManager: Reflection init — " +
                $"LateUpdate={_lodLateUpdateMethod != null} locked={_lodLockedField != null} " +
                $"activeStates={_lodActiveStatesField != null} positions={_lodPositionsProp != null} " +
                $"lods={_lodLodsField != null} Active={_roomLODActiveProperty != null}");
        }

        #endregion
    }
}
