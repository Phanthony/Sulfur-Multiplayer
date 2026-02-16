using System;
using System.Collections.Generic;
using System.Reflection;
using MonoMod.RuntimeDetour;
using SulfurMP.Level;
using SulfurMP.Networking;
using SulfurMP.Networking.Messages;
using Steamworks;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SulfurMP.Players
{
    /// <summary>
    /// Manages spectator mode when a player dies in multiplayer.
    /// Creates a mouse-controlled orbit camera around the spectated player.
    /// Tracks which players are dead for all-dead detection.
    /// </summary>
    public class SpectatorManager : MonoBehaviour
    {
        public static SpectatorManager Instance { get; private set; }

        /// <summary>True if the local player is currently spectating.</summary>
        public bool IsSpectating { get; private set; }

        /// <summary>
        /// Returns true if a remote player is known to be dead.
        /// Used by EnemyAISyncManager to skip dead players in LOS/distance/melee checks.
        /// </summary>
        public bool IsRemotePlayerDead(ulong steamId) => _deadPlayers.Contains(steamId);

        // Spectator camera
        private GameObject _spectatorCamera;
        private int _spectateTargetIndex;

        // Death tracking
        private readonly HashSet<ulong> _deadPlayers = new HashSet<ulong>();
        private bool _localPlayerDead;

        // Orbit camera parameters
        private const float OrbitDistance = 6f;
        private const float OrbitLookHeight = 0.9f; // Look at chest height
        private const float OrbitSmoothSpeed = 10f;
        private const float MouseSensitivity = 0.3f;
        private const float MinPitch = -80f;
        private const float MaxPitch = 80f;

        // Orbit camera state (mouse-controlled)
        private float _orbitYaw;
        private float _orbitPitch = 25f; // Default: slightly above

        // Cameras disabled when entering spectate (restored on exit if still alive)
        private readonly List<Camera> _disabledCameras = new List<Camera>();

        // DistanceLODManager hook — fix terrain not loading during spectate
        private Hook _lodLateUpdateHook;
        private static bool _lodReflectionInit;
        private static Type _distanceLODManagerType;
        private static MethodInfo _lodLateUpdateMethod;
        private static FieldInfo _lodLockedField;          // _locked (private bool)
        private static FieldInfo _lodForceRefreshField;    // _forceRefresh (private bool)
        private static FieldInfo _lodActiveStatesField;    // _activeStates (private bool[])
        private static FieldInfo _lodDistancesSqField;     // _distancesSq (private float[])
        private static PropertyInfo _lodPositionsProp;     // worldLODPositions (public Vector3[])
        private static FieldInfo _lodLodsField;            // lods (public List<DistanceLOD>)
        private static FieldInfo _lodGOsToActivateField;   // _GOsToActivate (private List<GameObject>)
        private static FieldInfo _lodGOsToDeactivateField; // _GOsToDeactivate (private List<GameObject>)
        private static FieldInfo _lodQueueInteractorRefreshField; // _queueInteractorRefresh (private bool)
        private static MethodInfo _lodForceRefreshMethod;  // ForceRefresh() (public void)
        private static PropertyInfo _lodInstanceProp;      // StaticInstance<DistanceLODManager>.Instance

        // RoomLODBase reflection (parent of DistanceLOD)
        private static PropertyInfo _roomLODActiveProperty;   // Active (public get/set)
        private static FieldInfo _roomLODRenderersField;      // renderers (public List<Renderer>)
        private static FieldInfo _roomLODGameObjectsField;    // gameObjects (public List<GameObject>)

        // PlayerLocks reflection — block menus during spectate
        private static bool _playerLocksReflectionInit;
        private static Type _playerLocksType;
        private static MethodInfo _addLockMethod;

        // MonoMod delegate for DistanceLODManager.LateUpdate (private instance, void)
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
            NetworkEvents.OnMessageReceived += OnMessageReceived;
            NetworkEvents.OnDisconnected += OnDisconnected;
            NetworkEvents.OnPeerLeft += OnPeerLeft;
            TryInstallLodHook();
        }

        private void OnDisable()
        {
            NetworkEvents.OnMessageReceived -= OnMessageReceived;
            NetworkEvents.OnDisconnected -= OnDisconnected;
            NetworkEvents.OnPeerLeft -= OnPeerLeft;
        }

        private void OnDestroy()
        {
            ExitSpectateMode();
            _lodLateUpdateHook?.Dispose();
            _lodLateUpdateHook = null;
            if (Instance == this)
                Instance = null;
        }

        private float _lastDiagLogTime;

        private void LateUpdate()
        {
            if (!IsSpectating || _spectatorCamera == null)
                return;

            var target = GetCurrentSpectateTarget();
            if (target == null)
            {
                // Log periodically if we can't find a target
                if (Time.unscaledTime - _lastDiagLogTime > 5f)
                {
                    _lastDiagLogTime = Time.unscaledTime;
                    var repl = PlayerReplicationManager.Instance;
                    int remoteCount = repl != null ? repl.RemotePlayers.Count : -1;
                    Plugin.Log.LogWarning($"SpectatorManager: No spectate target! " +
                        $"remotePlayers={remoteCount} deadPlayers={_deadPlayers.Count}");
                }
                return;
            }

            // Orbit camera: position based on mouse-controlled yaw/pitch around the target
            var camTransform = _spectatorCamera.transform;
            Vector3 targetPos = target.transform.position;
            Vector3 lookTarget = targetPos + Vector3.up * OrbitLookHeight;

            // Compute camera offset from orbit angles
            Quaternion orbitRotation = Quaternion.Euler(_orbitPitch, _orbitYaw, 0f);
            Vector3 direction = orbitRotation * Vector3.back;
            float desiredDistance = OrbitDistance;

            // Wall collision: raycast from target to desired camera position,
            // pull camera closer if geometry is in the way
            if (Physics.Raycast(lookTarget, direction, out RaycastHit hit, OrbitDistance))
            {
                desiredDistance = hit.distance - 0.2f; // Small buffer to avoid clipping
                if (desiredDistance < 0.5f)
                    desiredDistance = 0.5f; // Minimum distance so we don't end up inside the target
            }

            Vector3 desiredPos = lookTarget + direction * desiredDistance;

            // Smooth follow on position (tracks moving target without jitter)
            camTransform.position = Vector3.Lerp(camTransform.position, desiredPos,
                Time.unscaledDeltaTime * OrbitSmoothSpeed);
            camTransform.LookAt(lookTarget);
        }

        private void Update()
        {
            if (!IsSpectating)
                return;

            // Keep the game state clean during spectating.
            // After the death sequence, the game may try to pause, show cursor,
            // toggle inventory UI, etc. — all of which cover the spectator view.
            if (Cursor.lockState != CursorLockMode.Locked)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            if (Time.timeScale != 1f)
                Time.timeScale = 1f;

            // Mouse input: orbit around the target
            if (Mouse.current != null)
            {
                Vector2 mouseDelta = Mouse.current.delta.ReadValue();
                _orbitYaw += mouseDelta.x * MouseSensitivity;
                _orbitPitch -= mouseDelta.y * MouseSensitivity;
                _orbitPitch = Mathf.Clamp(_orbitPitch, MinPitch, MaxPitch);
            }

            // Tab key: cycle to next alive remote player
            if (Keyboard.current != null && Keyboard.current.tabKey.wasPressedThisFrame)
            {
                CycleSpectateTarget();
            }
        }

        private void OnGUI()
        {
            if (!IsSpectating)
                return;

            var target = GetCurrentSpectateTarget();
            string targetName = target != null ? target.PlayerName : "---";

            // Top banner: SPECTATING [name]
            var bannerStyle = new GUIStyle(GUI.skin.box)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal =
                {
                    textColor = Color.white,
                    background = MakeTex(1, 1, new Color(0f, 0f, 0f, 0.7f)),
                },
            };

            float bannerWidth = 400f;
            float bannerHeight = 36f;
            GUI.Box(
                new Rect((Screen.width - bannerWidth) / 2f, 10f, bannerWidth, bannerHeight),
                $"SPECTATING: {targetName}", bannerStyle);

            // Hint: Press TAB to switch, Mouse to orbit
            var hintStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(1f, 1f, 1f, 0.7f) },
            };
            GUI.Label(
                new Rect((Screen.width - bannerWidth) / 2f, 48f, bannerWidth, 24f),
                "Mouse to orbit | TAB to switch players", hintStyle);

            // Player count
            var repl = PlayerReplicationManager.Instance;
            int totalPlayers = (repl != null ? repl.RemotePlayers.Count : 0) + 1;
            int aliveCount = totalPlayers - _deadPlayers.Count - (_localPlayerDead ? 1 : 0);
            GUI.Label(
                new Rect((Screen.width - bannerWidth) / 2f, 72f, bannerWidth, 24f),
                $"Alive: {aliveCount}/{totalPlayers}", hintStyle);
        }

        #region Spectate Mode

        /// <summary>
        /// Enter spectator mode: create a camera and start following an alive remote player.
        /// </summary>
        public void EnterSpectateMode()
        {
            if (IsSpectating)
                return;

            _localPlayerDead = true;
            IsSpectating = true;
            _spectateTargetIndex = 0;

            // Disable all existing cameras so they don't compete with the spectator camera
            _disabledCameras.Clear();
            foreach (var existingCam in UnityEngine.Object.FindObjectsOfType<Camera>())
            {
                if (existingCam != null && existingCam.enabled)
                {
                    existingCam.enabled = false;
                    _disabledCameras.Add(existingCam);
                    Plugin.Log.LogInfo($"SpectatorManager: Disabled camera '{existingCam.name}' (tag={existingCam.tag})");
                }
            }

            // Hide the dead player's body so it doesn't block the spectator view
            HideLocalPlayerVisuals();

            // Create spectator camera
            _spectatorCamera = new GameObject("SpectatorCamera");
            UnityEngine.Object.DontDestroyOnLoad(_spectatorCamera);

            var cam = _spectatorCamera.AddComponent<Camera>();
            cam.tag = "MainCamera";
            cam.fieldOfView = 60f;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 1000f;

            // Try to add URP camera data for proper rendering — search all assemblies
            try
            {
                Type urpCamDataType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    urpCamDataType = asm.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData");
                    if (urpCamDataType != null) break;
                }
                if (urpCamDataType != null)
                {
                    _spectatorCamera.AddComponent(urpCamDataType);
                    Plugin.Log.LogInfo("SpectatorManager: Added URP camera data component");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"SpectatorManager: Could not add URP camera data: {ex.Message}");
            }

            // Block inventory, interaction, weapon switching during spectate
            SetPlayerLocks(true);

            // Resume game state so the world keeps running
            ResumeGameState();

            // Initialize orbit angles from target's facing direction (start behind them)
            var target = GetCurrentSpectateTarget();
            if (target != null)
            {
                _orbitYaw = target.transform.eulerAngles.y + 180f; // Behind the target
                _orbitPitch = 25f; // Slightly above

                // Snap camera to initial position
                var camTransform = _spectatorCamera.transform;
                Vector3 lookTarget = target.transform.position + Vector3.up * OrbitLookHeight;
                Quaternion orbitRotation = Quaternion.Euler(_orbitPitch, _orbitYaw, 0f);
                Vector3 offset = orbitRotation * (Vector3.back * OrbitDistance);
                camTransform.position = lookTarget + offset;
                camTransform.LookAt(lookTarget);

                Plugin.Log.LogInfo($"SpectatorManager: Initial camera pos={camTransform.position} target={target.PlayerName}");
            }
            else
            {
                _orbitYaw = 0f;
                _orbitPitch = 25f;
                Plugin.Log.LogWarning("SpectatorManager: No spectate target found! Camera may show black.");
            }

            Plugin.Log.LogInfo("SpectatorManager: Entered spectate mode (orbit camera)");
        }

        /// <summary>
        /// Exit spectator mode: destroy camera, reset all death tracking state.
        /// Always clears death tracking even if not currently spectating —
        /// level transitions must reset tracked deaths so stale entries
        /// don't cause false "all players dead" on the next death.
        /// </summary>
        public void ExitSpectateMode()
        {
            bool wasSpectating = IsSpectating;

            // Always reset death tracking (even if host wasn't spectating)
            _localPlayerDead = false;
            _deadPlayers.Clear();
            _spectateTargetIndex = 0;
            IsSpectating = false;

            if (_spectatorCamera != null)
            {
                UnityEngine.Object.Destroy(_spectatorCamera);
                _spectatorCamera = null;
            }

            // Re-enable cameras that were disabled (if they still exist — scene unload destroys them)
            foreach (var cam in _disabledCameras)
            {
                if (cam != null)
                    cam.enabled = true;
            }
            _disabledCameras.Clear();

            if (wasSpectating)
            {
                // Unblock menus
                SetPlayerLocks(false);

                // Force LOD refresh so normal room-based LOD resumes correctly
                ForceRefreshLod();

                Plugin.Log.LogInfo("SpectatorManager: Exited spectate mode");
            }
            else if (_deadPlayers.Count > 0)
                Plugin.Log.LogInfo("SpectatorManager: Cleared death tracking (level transition)");
        }

        /// <summary>
        /// Returns true if ALL players (local + all remote) are dead.
        /// </summary>
        public bool AllPlayersDead()
        {
            if (!_localPlayerDead)
                return false;

            var repl = PlayerReplicationManager.Instance;
            if (repl == null)
                return true;

            // Every remote player must be in _deadPlayers
            foreach (var steamId in repl.RemotePlayers.Keys)
            {
                if (!_deadPlayers.Contains(steamId))
                    return false;
            }

            return true;
        }

        #endregion

        #region Target Cycling

        private RemotePlayer GetCurrentSpectateTarget()
        {
            var replicationMgr = PlayerReplicationManager.Instance;
            if (replicationMgr == null)
                return null;

            var aliveTargets = GetAliveRemotePlayers();
            if (aliveTargets.Count == 0)
                return null;

            if (_spectateTargetIndex >= aliveTargets.Count)
                _spectateTargetIndex = 0;

            return aliveTargets[_spectateTargetIndex];
        }

        private void CycleSpectateTarget()
        {
            var aliveTargets = GetAliveRemotePlayers();
            if (aliveTargets.Count <= 1)
                return;

            _spectateTargetIndex = (_spectateTargetIndex + 1) % aliveTargets.Count;
            Plugin.Log.LogInfo($"SpectatorManager: Switched to target {_spectateTargetIndex}: {aliveTargets[_spectateTargetIndex].PlayerName}");
        }

        private List<RemotePlayer> GetAliveRemotePlayers()
        {
            var result = new List<RemotePlayer>();
            var replicationMgr = PlayerReplicationManager.Instance;
            if (replicationMgr == null)
                return result;

            foreach (var kvp in replicationMgr.RemotePlayers)
            {
                if (kvp.Value != null && kvp.Value.gameObject != null &&
                    !_deadPlayers.Contains(kvp.Key))
                {
                    result.Add(kvp.Value);
                }
            }
            return result;
        }

        #endregion

        #region Message Handling

        private void OnMessageReceived(CSteamID sender, NetworkMessage msg)
        {
            if (msg.Type != MessageType.PlayerDeath)
                return;

            var deathMsg = (PlayerDeathMessage)msg;
            HandlePlayerDeath(sender, deathMsg);
        }

        private void HandlePlayerDeath(CSteamID sender, PlayerDeathMessage msg)
        {
            if (msg.IsDead)
            {
                _deadPlayers.Add(msg.SteamId);
                Plugin.Log.LogInfo($"SpectatorManager: Player {msg.SteamId} died");
            }
            else
            {
                _deadPlayers.Remove(msg.SteamId);
                Plugin.Log.LogInfo($"SpectatorManager: Player {msg.SteamId} respawned");
            }

            // Hide/show the remote player capsule
            SetRemotePlayerVisible(msg.SteamId, !msg.IsDead);

            // Host relays death messages to other clients
            var net = NetworkManager.Instance;
            if (net != null && net.IsHost)
            {
                net.SendToAllExcept(sender, msg);

                // Check if all players are dead → transition to ChurchHub
                LevelSyncManager.CheckAllPlayersDead();
            }
        }

        #endregion

        #region Events

        private void OnDisconnected(string reason)
        {
            ExitSpectateMode();
        }

        private void OnPeerLeft(CSteamID peerId)
        {
            _deadPlayers.Remove(peerId.m_SteamID);
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Resume game state: unpause, restore timeScale, lock cursor.
        /// Ensures the game world keeps running while spectating.
        /// </summary>
        private static void ResumeGameState()
        {
            Time.timeScale = 1f;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;

            // Try to call GameManager.ResumeGame()
            try
            {
                Type gmType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    gmType = asm.GetType("PerfectRandom.Sulfur.Core.GameManager");
                    if (gmType != null) break;
                }
                if (gmType == null) return;

                var instanceProp = gmType.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                if (instanceProp == null) return;

                var gm = instanceProp.GetValue(null);
                if (gm == null || (gm is UnityEngine.Object uObj && uObj == null)) return;

                var resumeMethod = gmType.GetMethod("ResumeGame",
                    BindingFlags.Public | BindingFlags.Instance);
                if (resumeMethod != null)
                    resumeMethod.Invoke(gm, null);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"SpectatorManager: ResumeGameState failed: {ex.Message}");
            }
        }

        private static void HideLocalPlayerVisuals()
        {
            try
            {
                Type gmType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    gmType = asm.GetType("PerfectRandom.Sulfur.Core.GameManager");
                    if (gmType != null) break;
                }
                if (gmType == null) return;

                var instanceProp = gmType.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                var gm = instanceProp?.GetValue(null);
                if (gm == null || (gm is UnityEngine.Object uObj && uObj == null)) return;

                var playerObjProp = gmType.GetProperty("PlayerObject",
                    BindingFlags.Public | BindingFlags.Instance);
                var playerObj = playerObjProp?.GetValue(gm) as GameObject;
                if (playerObj == null) return;

                var renderers = playerObj.GetComponentsInChildren<Renderer>(true);
                int count = 0;
                foreach (var r in renderers)
                {
                    if (r != null && r.enabled)
                    {
                        r.enabled = false;
                        count++;
                    }
                }
                Plugin.Log.LogInfo($"SpectatorManager: Hid {count} player renderers");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"SpectatorManager: Failed to hide player visuals: {ex.Message}");
            }
        }

        /// <summary>
        /// Show or hide a remote player's capsule (renderers + name label).
        /// Called when a death/respawn message is received.
        /// </summary>
        private static void SetRemotePlayerVisible(ulong steamId, bool visible)
        {
            var repl = PlayerReplicationManager.Instance;
            if (repl == null) return;
            if (!repl.RemotePlayers.TryGetValue(steamId, out var remote)) return;
            if (remote == null || remote.gameObject == null) return;

            foreach (var r in remote.GetComponentsInChildren<Renderer>(true))
            {
                if (r != null) r.enabled = visible;
            }
            foreach (var tm in remote.GetComponentsInChildren<TextMesh>(true))
            {
                if (tm != null) tm.gameObject.SetActive(visible);
            }

            Plugin.Log.LogInfo($"SpectatorManager: Set remote player {steamId} visible={visible}");
        }

        private static Texture2D MakeTex(int width, int height, Color color)
        {
            var pixels = new Color[width * height];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = color;
            var tex = new Texture2D(width, height);
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        #endregion

        #region DistanceLODManager Hook

        private void TryInstallLodHook()
        {
            if (_lodLateUpdateHook != null) return;

            InitLodReflection();
            if (_lodLateUpdateMethod == null)
            {
                Plugin.Log.LogWarning("SpectatorManager: Could not find DistanceLODManager.LateUpdate");
                return;
            }

            try
            {
                _lodLateUpdateHook = new Hook(
                    _lodLateUpdateMethod,
                    new hook_LodLateUpdate(LodLateUpdateInterceptor));
                Plugin.Log.LogInfo("SpectatorManager: Installed MonoMod hook on DistanceLODManager.LateUpdate");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"SpectatorManager: Failed to hook DistanceLODManager.LateUpdate: {ex}");
            }
        }

        /// <summary>
        /// Hook on DistanceLODManager.LateUpdate. During spectate mode, bypasses the room
        /// connectivity LOD logic (which NREs on null PlayerUnit after death) and runs
        /// the game's own distance-only LOD fallback instead.
        /// </summary>
        private static void LodLateUpdateInterceptor(orig_LodLateUpdate orig, object self)
        {
            var instance = Instance;
            if (instance == null || !instance.IsSpectating)
            {
                // Normal mode — let the game handle LOD normally
                orig(self);
                return;
            }

            // Spectate mode: we need to let the activation/deactivation queues process
            // (lines 95-130 of original) but skip the room connectivity code (line 141+)
            // that NREs on null PlayerUnit.
            //
            // Strategy: set _locked = true before calling orig. Orig will:
            //   1. Process activation/deactivation queues (lines 95-130)
            //   2. Queue interactor refresh if needed (lines 131-135)
            //   3. Return early at the _locked check (line 136-138)
            // Then we run distance-only LOD ourselves.

            if (_lodLockedField == null || _lodActiveStatesField == null ||
                _lodDistancesSqField == null || _lodPositionsProp == null ||
                _lodLodsField == null)
            {
                // Missing reflection — fall back to orig (may NRE, but better than no LOD)
                try { orig(self); } catch { }
                return;
            }

            try
            {
                // Set locked to let orig process queues but skip room connectivity
                _lodLockedField.SetValue(self, true);
                orig(self);
                _lodLockedField.SetValue(self, false);
            }
            catch
            {
                // Ensure locked is reset even if orig throws
                try { _lodLockedField.SetValue(self, false); } catch { }
            }

            // Now run distance-only LOD using spectator camera position
            try
            {
                RunSpectatorDistanceLod(self);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"SpectatorManager: LOD update error: {ex.Message}");
            }
        }

        /// <summary>
        /// Distance-only LOD check — same as the game's fallback path (lines 176-179)
        /// when rooms have no connectivity graph. Uses spectator camera position instead
        /// of the (destroyed) player camera.
        /// </summary>
        private static void RunSpectatorDistanceLod(object lodManager)
        {
            var activeStates = (bool[])_lodActiveStatesField.GetValue(lodManager);
            var distancesSq = (float[])_lodDistancesSqField.GetValue(lodManager);
            var worldPositions = (Vector3[])_lodPositionsProp.GetValue(lodManager);
            var forceRefresh = (bool)_lodForceRefreshField.GetValue(lodManager);

            if (activeStates == null || distancesSq == null || worldPositions == null)
                return;

            // Get spectator camera position
            var cam = Camera.main;
            if (cam == null) return;
            Vector3 camPos = cam.transform.position;

            // Distance-only check for each LOD
            for (int i = 0; i < worldPositions.Length; i++)
            {
                float dx = worldPositions[i].x - camPos.x;
                float dy = worldPositions[i].y - camPos.y;
                float dz = worldPositions[i].z - camPos.z;
                activeStates[i] = (dx * dx + dy * dy + dz * dz) < distancesSq[i];
            }

            // Apply changes — same as game's lines 188-210
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

        private static void ForceRefreshLod()
        {
            if (_lodInstanceProp == null || _lodForceRefreshMethod == null) return;

            try
            {
                var lodMgr = _lodInstanceProp.GetValue(null);
                if (lodMgr != null && !(lodMgr is UnityEngine.Object uObj && uObj == null))
                    _lodForceRefreshMethod.Invoke(lodMgr, null);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"SpectatorManager: ForceRefresh failed: {ex.Message}");
            }
        }

        private static void InitLodReflection()
        {
            if (_lodReflectionInit) return;
            _lodReflectionInit = true;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (_distanceLODManagerType == null)
                    _distanceLODManagerType = asm.GetType("PerfectRandom.Sulfur.Core.DistanceLODManager");
                if (_distanceLODManagerType != null) break;
            }

            if (_distanceLODManagerType == null)
            {
                Plugin.Log.LogWarning("SpectatorManager: Could not find DistanceLODManager type");
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
            _lodForceRefreshMethod = _distanceLODManagerType.GetMethod("ForceRefresh", bfPub);

            // StaticInstance<DistanceLODManager>.Instance
            var baseType = _distanceLODManagerType.BaseType; // StaticInstance<DistanceLODManager>
            if (baseType != null)
            {
                _lodInstanceProp = baseType.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            }

            // RoomLODBase — parent of DistanceLOD
            Type roomLODBaseType = null;
            Type distanceLODType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (roomLODBaseType == null)
                    roomLODBaseType = asm.GetType("PerfectRandom.Sulfur.Core.RoomLODBase");
                if (distanceLODType == null)
                    distanceLODType = asm.GetType("PerfectRandom.Sulfur.Core.DistanceLOD");
                if (roomLODBaseType != null && distanceLODType != null) break;
            }

            if (roomLODBaseType != null)
            {
                _roomLODActiveProperty = roomLODBaseType.GetProperty("Active", bfPub);
                _roomLODRenderersField = roomLODBaseType.GetField("renderers", bfPub);
                _roomLODGameObjectsField = roomLODBaseType.GetField("gameObjects", bfPub);
            }

            Plugin.Log.LogInfo($"SpectatorManager: LOD reflection init — " +
                $"LateUpdate={_lodLateUpdateMethod != null} locked={_lodLockedField != null} " +
                $"activeStates={_lodActiveStatesField != null} positions={_lodPositionsProp != null} " +
                $"lods={_lodLodsField != null} Active={_roomLODActiveProperty != null}");
        }

        #endregion

        #region PlayerLocks

        /// <summary>
        /// Add or remove player locks (Inventory|Interaction|Weapon = 22) via
        /// GameManager.AddLock. Uses the game's built-in input blocking system.
        /// </summary>
        private static void SetPlayerLocks(bool addLock)
        {
            InitPlayerLocksReflection();
            if (_playerLocksType == null || _addLockMethod == null) return;

            try
            {
                Type gmType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    gmType = asm.GetType("PerfectRandom.Sulfur.Core.GameManager");
                    if (gmType != null) break;
                }
                if (gmType == null) return;

                var instanceProp = gmType.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                var gm = instanceProp?.GetValue(null);
                if (gm == null || (gm is UnityEngine.Object uObj && uObj == null)) return;

                // Inventory(16) | Interaction(2) | Weapon(4) = 22
                var lockValue = Enum.ToObject(_playerLocksType, 22);
                _addLockMethod.Invoke(gm, new object[] { lockValue, addLock });
                Plugin.Log.LogInfo($"SpectatorManager: PlayerLocks {(addLock ? "added" : "removed")} (Inventory|Interaction|Weapon)");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"SpectatorManager: SetPlayerLocks failed: {ex.Message}");
            }
        }

        private static void InitPlayerLocksReflection()
        {
            if (_playerLocksReflectionInit) return;
            _playerLocksReflectionInit = true;

            Type gmType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                gmType = asm.GetType("PerfectRandom.Sulfur.Core.GameManager");
                if (gmType != null) break;
            }
            if (gmType == null) return;

            // PlayerLocks is nested in GameManager
            _playerLocksType = gmType.GetNestedType("PlayerLocks", BindingFlags.Public);
            if (_playerLocksType == null)
            {
                // Might be top-level
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    _playerLocksType = asm.GetType("PerfectRandom.Sulfur.Core.GameManager+PlayerLocks");
                    if (_playerLocksType != null) break;
                    _playerLocksType = asm.GetType("PerfectRandom.Sulfur.Core.PlayerLocks");
                    if (_playerLocksType != null) break;
                }
            }

            if (_playerLocksType != null)
            {
                _addLockMethod = gmType.GetMethod("AddLock",
                    BindingFlags.Public | BindingFlags.Instance);
                Plugin.Log.LogInfo($"SpectatorManager: PlayerLocks reflection — " +
                    $"type={_playerLocksType != null} AddLock={_addLockMethod != null}");
            }
            else
            {
                Plugin.Log.LogWarning("SpectatorManager: Could not find PlayerLocks type");
            }
        }

        #endregion
    }
}
