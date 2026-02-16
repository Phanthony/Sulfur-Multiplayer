using System;
using System.Collections.Generic;
using System.Reflection;
using MonoMod.RuntimeDetour;
using SulfurMP.Networking;
using SulfurMP.Networking.Messages;
using SulfurMP.Players;
using UnityEngine;

namespace SulfurMP.Entities
{
    /// <summary>
    /// Host-only manager that supplements native AI for remote player awareness:
    /// 1. LOS Injection: hooks BatchedNPCRaycasts.LateUpdate as a postfix. After the native
    ///    Burst job populates hostilesInLOS (for host player only), does Physics.Linecast
    ///    from each NPC to each remote player using EnemyOcclusionLayers. If unobstructed,
    ///    injects the remote player's Unit into hostilesInLOS so AiAgent.GetTarget() sees it.
    /// 2. DistanceToPlayer Override: NpcUpdateManager only computes distance from host player.
    ///    This postfix overrides with min(hostDist, remoteDist) for NPC activation/BT thresholds.
    /// 3. NPC Activation: activates inactive NPCs near remote players (NpcUpdateManager only
    ///    activates near the host).
    /// </summary>
    public class EnemyAISyncManager : MonoBehaviour
    {
        public static EnemyAISyncManager Instance { get; private set; }

        // Hooks
        private bool _hookAttempted;
        private int _hookRetryCount;
        private const int MaxHookRetries = 60;
        private Hook _npcUpdateManagerHook;
        private Hook _batchedNpcRaycastsHook;
        private Hook _getTargetHook;
        private Hook _setShootingHook;

        // MonoMod delegates for NpcUpdateManager.LateUpdate (private instance)
        private delegate void orig_NpcUpdateLateUpdate(object self);
        private delegate void hook_NpcUpdateLateUpdate(orig_NpcUpdateLateUpdate orig, object self);

        // MonoMod delegates for BatchedNPCRaycasts.LateUpdate (private instance)
        private delegate void orig_BatchedNpcLateUpdate(object self);
        private delegate void hook_BatchedNpcLateUpdate(orig_BatchedNpcLateUpdate orig, object self);

        // MonoMod delegates for AiAgent.GetTarget (public instance, returns Unit)
        private delegate object orig_GetTarget(object self);
        private delegate object hook_GetTarget(orig_GetTarget orig, object self);

        // MonoMod delegates for Npc.SetShooting (public instance, void)
        private delegate void orig_SetShooting(object self, bool state);
        private delegate void hook_SetShooting(orig_SetShooting orig, object self, bool state);

        // Reflection cache
        private static bool _reflectionInit;

        // Types
        private static Type _npcType;
        private static Type _aiAgentType;
        private static Type _gameManagerType;
        private static Type _npcUpdateManagerType;
        private static Type _batchedNpcRaycastsType;

        // NpcUpdateManager.LateUpdate — hook target
        private static MethodInfo _npcUpdateLateUpdateMethod;

        // BatchedNPCRaycasts.LateUpdate — hook target for LOS injection
        private static MethodInfo _batchedNpcLateUpdateMethod;

        // AiAgent — LOS injection for remote player visibility
        private static PropertyInfo _hostilesInLOSProp;     // AiAgent.hostilesInLOS (public List<Unit>)
        private static FieldInfo _useLineOfSightField;      // AiAgent.useLineOfSight (public bool field)
        private static MethodInfo _getTargetMethod;         // AiAgent.GetTarget() (public instance, returns Unit)

        // Unit state check for GetTarget fallback
        private static FieldInfo _unitStateField;           // Unit.unitState (public field)
        private static object _unitStateDead;               // UnitState.Dead enum value

        // GameManager
        private static PropertyInfo _gmInstanceProp;
        private static FieldInfo _aliveNpcsField;
        private static PropertyInfo _playerObjectProp;
        private static PropertyInfo _playerUnitProp;             // GameManager.PlayerUnit
        private static PropertyInfo _enemyOcclusionLayersProp;  // GameManager.EnemyOcclusionLayers (LayerMask)

        // Cached occlusion mask (read once per frame in LOS injection)
        private static int _cachedOcclusionMask;
        private static bool _occlusionMaskCached;

        // Npc fields/properties
        private static PropertyInfo _npcAiAgentProp;        // Npc.AiAgent (property)

        // For auto-registering unregistered NPCs
        private static FieldInfo _unitSOField;               // Unit.unitSO (field)
        private static FieldInfo _unitSOIdField;             // UnitSO.id (field, UnitId)
        private static FieldInfo _unitIdValueField;          // UnitId.value (field, ushort)
        private static MethodInfo _getCurrentHealthMethod;   // Unit.GetCurrentHealth()

        // NPC state checks — filter non-hostile/dead NPCs from combat
        private static PropertyInfo _isAliveProp;            // Unit.IsAlive (bool)
        private static PropertyInfo _isCivilianProp;         // Unit.IsCivilian (bool)
        private static PropertyInfo _isCoweringProp;         // Unit.IsCowering (bool)

        // Npc.SetShooting — hook target to block animation-event trigger hijack on client
        private static MethodInfo _setShootingMethod;

        // NPC activation near remote players
        private static PropertyInfo _gmNpcsProp;             // GameManager.npcs (all NPCs, including inactive)
        private static FieldInfo _excludeFromNpcLODField;    // Npc.excludeFromNpcLOD (bool)
        private static MethodInfo _activateBehaviourMethod;  // Npc.ActivateBehaviour()
        private static PropertyInfo _distanceToPlayerProp;   // AiAgent.DistanceToPlayer (float)
        private const float NpcActivationDistance = 200f;
        private const float NpcActivationDistanceSqr = NpcActivationDistance * NpcActivationDistance;

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
            DisposeHooks();
            if (Instance == this)
                Instance = null;
        }

        private void Update()
        {
            var net = NetworkManager.Instance;
            if (net == null || !net.IsConnected)
                return;

            if (!_hookAttempted)
                TryInstallHooks();
        }

        #region Hook Installation

        private void TryInstallHooks()
        {
            InitReflection();

            if (_npcUpdateLateUpdateMethod == null || _batchedNpcLateUpdateMethod == null ||
                _getTargetMethod == null)
            {
                _hookRetryCount++;
                if (_hookRetryCount >= MaxHookRetries)
                {
                    _hookAttempted = true;
                    if (_npcUpdateLateUpdateMethod == null)
                        Plugin.Log.LogWarning("EnemyAISync: Could not find NpcUpdateManager.LateUpdate after max retries");
                    if (_batchedNpcLateUpdateMethod == null)
                        Plugin.Log.LogWarning("EnemyAISync: Could not find BatchedNPCRaycasts.LateUpdate after max retries");
                    if (_getTargetMethod == null)
                        Plugin.Log.LogWarning("EnemyAISync: Could not find AiAgent.GetTarget after max retries");
                }
                return;
            }

            _hookAttempted = true;

            // Hook NpcUpdateManager.LateUpdate to override DistanceToPlayer and activate NPCs
            try
            {
                _npcUpdateManagerHook = new Hook(
                    _npcUpdateLateUpdateMethod,
                    new hook_NpcUpdateLateUpdate(NpcUpdateManagerLateUpdatePostfix));
                Plugin.Log.LogInfo("EnemyAISync: Installed MonoMod hook on NpcUpdateManager.LateUpdate");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"EnemyAISync: Failed to hook NpcUpdateManager.LateUpdate: {ex}");
            }

            // Hook BatchedNPCRaycasts.LateUpdate for LOS injection of remote players
            try
            {
                _batchedNpcRaycastsHook = new Hook(
                    _batchedNpcLateUpdateMethod,
                    new hook_BatchedNpcLateUpdate(BatchedNpcRaycastsLateUpdatePostfix));
                Plugin.Log.LogInfo("EnemyAISync: Installed MonoMod hook on BatchedNPCRaycasts.LateUpdate");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"EnemyAISync: Failed to hook BatchedNPCRaycasts.LateUpdate: {ex}");
            }

            // Hook AiAgent.GetTarget to handle null PlayerUnit when host dies
            try
            {
                _getTargetHook = new Hook(
                    _getTargetMethod,
                    new hook_GetTarget(GetTargetWrapper));
                Plugin.Log.LogInfo("EnemyAISync: Installed MonoMod hook on AiAgent.GetTarget");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"EnemyAISync: Failed to hook AiAgent.GetTarget: {ex}");
            }

            // Hook Npc.SetShooting to no-op on client — blocks ShootTriggerRoutine from
            // re-enabling weapon trigger via animation events
            if (_setShootingMethod != null)
            {
                try
                {
                    _setShootingHook = new Hook(_setShootingMethod,
                        new hook_SetShooting(SetShootingInterceptor));
                    Plugin.Log.LogInfo("EnemyAISync: Installed MonoMod hook on Npc.SetShooting");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"EnemyAISync: Failed to hook Npc.SetShooting: {ex}");
                }
            }
        }

        private void DisposeHooks()
        {
            _npcUpdateManagerHook?.Dispose();
            _npcUpdateManagerHook = null;
            _batchedNpcRaycastsHook?.Dispose();
            _batchedNpcRaycastsHook = null;
            _getTargetHook?.Dispose();
            _getTargetHook = null;
            _setShootingHook?.Dispose();
            _setShootingHook = null;
            _hookAttempted = false;
        }

        #endregion

        #region SetShooting Hook — Block Client Arrow Spam

        /// <summary>
        /// No-ops Npc.SetShooting on client to prevent ShootTriggerRoutine (started by
        /// animation events calling TriggerShoot) from re-enabling the weapon trigger.
        /// NpcMotionSmoother controls bIsTriggerActive directly via reflection on client.
        /// </summary>
        private static void SetShootingInterceptor(orig_SetShooting orig, object self, bool state)
        {
            var net = NetworkManager.Instance;
            if (net != null && net.IsConnected && !net.IsHost)
                return; // Client: no-op — NpcMotionSmoother controls trigger state

            orig(self, state);
        }

        #endregion

        #region BatchedNPCRaycasts Hook — LOS Injection

        /// <summary>
        /// Runs AFTER BatchedNPCRaycasts.LateUpdate completes.
        /// The native Burst job populates hostilesInLOS for the host player only.
        /// This postfix does Physics.Linecast from each NPC to each remote player
        /// using EnemyOcclusionLayers. If the linecast is unobstructed (clear LOS),
        /// the remote player's Unit is injected into that NPC's hostilesInLOS list
        /// so AiAgent.GetTarget() picks it up naturally in the next BT update.
        ///
        /// When the host player is dead, orig() may crash (PlayerObject destroyed).
        /// In that case, we clear hostilesInLOS for all NPCs to remove stale/destroyed
        /// player references, then inject only remote player Units based on LOS checks.
        /// </summary>
        private static void BatchedNpcRaycastsLateUpdatePostfix(
            orig_BatchedNpcLateUpdate orig, object self)
        {
            // Let the original run first — Burst job populates hostilesInLOS for host player
            bool origThrew = false;
            try { orig(self); }
            catch { origThrew = true; }

            var instance = Instance;
            var net = NetworkManager.Instance;
            if (instance == null || net == null || !net.IsHost || !net.IsConnected)
                return;

            // Skip during level load
            var entitySync = EntitySyncManager.Instance;
            if (entitySync != null && entitySync.IsBatchPending)
                return;

            var replicationMgr = PlayerReplicationManager.Instance;
            if (replicationMgr == null) return;

            var remotePlayers = replicationMgr.RemotePlayers;
            if (remotePlayers.Count == 0) return;

            // Check if host player is dead — hostilesInLOS may contain the dead PlayerUnit
            // which causes GetTarget's onlyTargetPlayer fast-path to return it.
            // Clear it so NPCs fall through to our injected remote player Units.
            bool hostDead = IsHostPlayerDead();

            // Collect remote player head positions + Unit components
            // RemotePlayer.CameraRoot is at y=1.6 (head height for LOS target)
            var remoteHeadPositions = new List<Vector3>();
            var remoteUnits = new List<object>();
            foreach (var kvp in remotePlayers)
            {
                var remote = kvp.Value;
                if (remote == null || remote.gameObject == null) continue;

                // Skip dead remote players — don't inject into LOS
                if (SpectatorManager.Instance != null && SpectatorManager.Instance.IsRemotePlayerDead(kvp.Key))
                    continue;

                // Get the Unit component from the RemotePlayerMarker
                var marker = remote.GetComponent<RemotePlayerMarker>();
                if (marker?.UnitComponent == null) continue;

                // Use CameraRoot position (head height) for LOS target
                Vector3 headPos;
                if (remote.CameraRoot != null)
                    headPos = remote.CameraRoot.position;
                else
                    headPos = remote.transform.position + new Vector3(0f, 1.6f, 0f);

                remoteHeadPositions.Add(headPos);
                remoteUnits.Add(marker.UnitComponent);
            }
            if (remoteHeadPositions.Count == 0) return;

            // Get the occlusion layer mask (cache once)
            int occlusionMask = GetOcclusionMask();
            if (occlusionMask == 0) return; // No mask = can't do LOS checks

            // Iterate alive NPCs and inject remote player Units into hostilesInLOS
            var npcs = GetAliveNpcs();
            if (npcs == null) return;

            foreach (var npcObj in npcs)
            {
                if (npcObj == null) continue;
                if (!(npcObj is Component npcComp) || npcComp == null) continue;

                // Get the AiAgent
                if (_npcAiAgentProp == null) continue;
                object aiAgent;
                try { aiAgent = _npcAiAgentProp.GetValue(npcObj); }
                catch { continue; }
                if (aiAgent == null) continue;

                // Skip NPCs with useLineOfSight=false — BatchedNPCRaycasts doesn't manage
                // their hostilesInLOS (it's never cleared), so appending would accumulate entries
                if (_useLineOfSightField != null)
                {
                    try
                    {
                        bool useLOS = (bool)_useLineOfSightField.GetValue(aiAgent);
                        if (!useLOS) continue;
                    }
                    catch { continue; }
                }

                // Get hostilesInLOS list
                System.Collections.IList hostilesList;
                try { hostilesList = _hostilesInLOSProp.GetValue(aiAgent) as System.Collections.IList; }
                catch { continue; }
                if (hostilesList == null) continue;

                // When orig() threw (host dead/destroyed) or host is dead (unitState=Dead),
                // hostilesInLOS may contain stale/dead player references. Clear it so GetTarget's
                // onlyTargetPlayer fast-path doesn't return the dead host PlayerUnit.
                if (origThrew || hostDead)
                    hostilesList.Clear();

                var npcPos = npcComp.transform.position;
                // Raycast from NPC eye height (approximate)
                var npcEyePos = npcPos + new Vector3(0f, 1.5f, 0f);

                for (int i = 0; i < remoteHeadPositions.Count; i++)
                {
                    // Skip if already in the list
                    if (hostilesList.Contains(remoteUnits[i]))
                        continue;

                    // Physics.Linecast: returns true if something BLOCKS the line
                    // If NOT blocked → clear LOS → inject into hostilesInLOS
                    if (!Physics.Linecast(npcEyePos, remoteHeadPositions[i], occlusionMask,
                        QueryTriggerInteraction.Ignore))
                    {
                        hostilesList.Add(remoteUnits[i]);
                    }
                }
            }
        }

        /// <summary>
        /// Get the EnemyOcclusionLayers mask from GameManager (cached after first read).
        /// </summary>
        private static int GetOcclusionMask()
        {
            if (_occlusionMaskCached)
                return _cachedOcclusionMask;

            if (_gmInstanceProp == null || _enemyOcclusionLayersProp == null)
                return 0;

            try
            {
                var gm = _gmInstanceProp.GetValue(null);
                if (gm == null || (gm is UnityEngine.Object uObj && uObj == null))
                    return 0;

                var layerMaskObj = _enemyOcclusionLayersProp.GetValue(gm);
                if (layerMaskObj is LayerMask mask)
                {
                    _cachedOcclusionMask = mask.value;
                    _occlusionMaskCached = true;
                    Plugin.Log.LogInfo($"EnemyAISync: Cached EnemyOcclusionLayers mask: {_cachedOcclusionMask}");
                    return _cachedOcclusionMask;
                }
                // LayerMask might be boxed as int
                _cachedOcclusionMask = (int)layerMaskObj;
                _occlusionMaskCached = true;
                Plugin.Log.LogInfo($"EnemyAISync: Cached EnemyOcclusionLayers mask (int): {_cachedOcclusionMask}");
                return _cachedOcclusionMask;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"EnemyAISync: Failed to get EnemyOcclusionLayers: {ex.Message}");
                return 0;
            }
        }

        #endregion

        #region AiAgent.GetTarget Hook — Host Death Resilience

        /// <summary>
        /// Wraps AiAgent.GetTarget() to handle null PlayerUnit when host player dies.
        ///
        /// Game's GetTarget() directly accesses GameManager.PlayerUnit on 4 lines without
        /// null checks. When the host dies, Destroy(PlayerObject) makes PlayerUnit Unity-null,
        /// causing NREs in every NPC's GetTarget() call → all NPCs lose their target → AI freezes.
        ///
        /// Fix: when PlayerObject is destroyed (host dead), bypass orig() entirely and manually
        /// iterate hostilesInLOS for the last non-dead hostile — matching the game's fallback
        /// logic (hostilesInLOS.LastOrDefault(unit => unit.UnitState != Dead)).
        /// This lets NPCs target remote player Units injected by our LOS injection hook.
        /// </summary>
        private static object GetTargetWrapper(orig_GetTarget orig, object self)
        {
            // Fast path: if host player exists AND is alive, just call orig (zero overhead)
            if (GetHostPlayerObject() != null && !IsHostPlayerDead())
                return orig(self);

            // Host player is dead (or destroyed) — orig() would NRE or return dead PlayerUnit.
            // Manually iterate hostilesInLOS for valid targets (our injected remote player Units).
            if (_hostilesInLOSProp == null)
                return null;

            try
            {
                var hostilesList = _hostilesInLOSProp.GetValue(self) as System.Collections.IList;
                if (hostilesList == null || hostilesList.Count == 0)
                    return null;

                // Iterate backwards — matches game's LastOrDefault behavior (prefers last entry)
                for (int i = hostilesList.Count - 1; i >= 0; i--)
                {
                    var hostile = hostilesList[i];
                    if (hostile == null) continue;

                    // Check for destroyed Unity object (host's dead PlayerUnit still in list)
                    if (hostile is UnityEngine.Object uObj && uObj == null) continue;

                    // Check UnitState != Dead (matches game's filter lambda)
                    if (IsUnitDead(hostile)) continue;

                    return hostile;
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Check if a Unit's unitState == Dead. Used by GetTarget fallback to filter
        /// dead units from hostilesInLOS, matching the game's original filter.
        /// </summary>
        private static bool IsUnitDead(object unit)
        {
            if (_unitStateField == null || _unitStateDead == null) return false;
            try
            {
                var state = _unitStateField.GetValue(unit);
                return Equals(state, _unitStateDead);
            }
            catch { return false; }
        }

        #endregion

        #region NpcUpdateManager Hook

        /// <summary>
        /// Runs AFTER NpcUpdateManager.LateUpdate completes.
        /// Overrides DistanceToPlayer with the nearest-player distance (host OR remote)
        /// and activates inactive NPCs near remote players.
        /// </summary>
        // Throttle timer for client NPC freeze scan
        private static float _clientFreezeTimer;
        private const float ClientFreezeInterval = 0.5f;

        private static void NpcUpdateManagerLateUpdatePostfix(
            orig_NpcUpdateLateUpdate orig, object self)
        {
            // Let the original run first — sets DistanceToPlayer to host distance
            // try/catch: host player dead → NpcUpdateManager may NRE on null PlayerObject
            try { orig(self); }
            catch { /* Swallow NRE when host player is dead */ }

            var instance = Instance;
            var net = NetworkManager.Instance;
            if (instance == null || net == null || !net.IsConnected)
                return;

            // Client path: freeze newly activated NPCs that NpcUpdateManager activated
            // (these bypass SpawnUnit entirely — pre-placed NPCs going active→inactive→active)
            if (!net.IsHost)
            {
                ClientFreezeNewlyActivatedNpcs();
                return;
            }

            // Skip during level load
            var entitySync = EntitySyncManager.Instance;
            if (entitySync != null && entitySync.IsBatchPending)
                return;

            var replicationMgr = PlayerReplicationManager.Instance;
            if (replicationMgr == null) return;

            var remotePlayers = replicationMgr.RemotePlayers;
            if (remotePlayers.Count == 0) return;

            // Check if host player is dead/destroyed — if so, NPCs must rely entirely
            // on remote players for distance/targeting (orig may have NRE'd, DistanceToPlayer is stale)
            bool hostPlayerDead = GetHostPlayerObject() == null || IsHostPlayerDead();

            // Collect remote player positions
            var remotePositions = new List<Vector3>();
            foreach (var kvp in remotePlayers)
            {
                if (kvp.Value != null && kvp.Value.gameObject != null)
                {
                    // Skip dead remote players — don't use for distance/activation
                    if (SpectatorManager.Instance != null && SpectatorManager.Instance.IsRemotePlayerDead(kvp.Key))
                        continue;
                    remotePositions.Add(kvp.Value.transform.position);
                }
            }
            if (remotePositions.Count == 0) return;

            // Get ALL npcs (including inactive) for activation check
            var allNpcs = GetAllNpcs();
            if (allNpcs == null || allNpcs.Count == 0) return;

            int activated = 0;

            foreach (var npcObj in allNpcs)
            {
                if (npcObj == null) continue;
                if (!(npcObj is Component npcComp) || npcComp == null) continue;

                if (IsExcludedFromLOD(npcObj)) continue;

                var npcPos = npcComp.transform.position;
                var npcGo = npcComp.gameObject;

                // Find squared distance to nearest remote player
                float nearestSqr = float.MaxValue;
                for (int i = 0; i < remotePositions.Count; i++)
                {
                    float sqr = (npcPos - remotePositions[i]).sqrMagnitude;
                    if (sqr < nearestSqr)
                        nearestSqr = sqr;
                }

                float remoteDist = Mathf.Sqrt(nearestSqr);

                if (!npcGo.activeSelf)
                {
                    // Activate inactive NPCs near remote players
                    if (nearestSqr < NpcActivationDistanceSqr)
                    {
                        npcGo.SetActive(true);
                        InvokeActivateBehaviour(npcObj);
                        activated++;
                    }
                }
                else
                {
                    // Skip non-hostile NPCs (civilians, cowering, dead)
                    if (ShouldSkipCombat(npcObj)) continue;

                    // Active NPC — override DistanceToPlayer with nearest-player distance.
                    // NpcUpdateManager only computes host distance; we fix it to min(host, remote).
                    // When host is dead, orig NRE'd and DistanceToPlayer is stale — always override.
                    if (_npcAiAgentProp != null && _distanceToPlayerProp != null)
                    {
                        try
                        {
                            var aiAgent = _npcAiAgentProp.GetValue(npcObj);
                            if (aiAgent != null)
                            {
                                bool shouldOverride = hostPlayerDead;
                                if (!shouldOverride)
                                {
                                    float current = (float)_distanceToPlayerProp.GetValue(aiAgent);
                                    shouldOverride = remoteDist < current;
                                }

                                if (shouldOverride)
                                    _distanceToPlayerProp.SetValue(aiAgent, remoteDist);
                            }
                        }
                        catch { }
                    }
                }
            }

            if (activated > 0)
                Plugin.Log.LogInfo($"EnemyAISync: Activated {activated} NPCs near remote players (post-hook)");
        }

        /// <summary>
        /// Client-only: scan alive NPCs for any that NpcUpdateManager activated but aren't
        /// registered in EntityRegistry. Freeze them (DisableClientAI) and notify the host
        /// so it can register/force-spawn them.
        /// These are pre-placed inactive NPCs that NpcUpdateManager activates based on player
        /// proximity — they bypass SpawnUnit entirely.
        /// </summary>
        private static void ClientFreezeNewlyActivatedNpcs()
        {
            _clientFreezeTimer += Time.deltaTime;
            if (_clientFreezeTimer < ClientFreezeInterval)
                return;
            _clientFreezeTimer = 0f;

            var entitySync = EntitySyncManager.Instance;
            var net = NetworkManager.Instance;
            if (entitySync == null || net == null) return;

            var npcs = GetAliveNpcs();
            if (npcs == null) return;

            foreach (var npcObj in npcs)
            {
                if (npcObj == null) continue;
                if (!(npcObj is Component npcComp) || npcComp == null) continue;

                var go = npcComp.gameObject;
                if (go == null || !go.activeSelf) continue;

                // Already registered — skip
                if (entitySync.Registry.TryGetId(go, out _))
                    continue;

                // Already processed — excludeFromNpcLOD is set by DisableClientAI
                if (IsExcludedFromLOD(npcObj))
                    continue;

                // Freeze and notify host
                EntitySyncManager.DisableClientAI(go);
                ushort unitIdValue = GetUnitIdValueFromNpc(npcObj);
                var pos = go.transform.position;

                var notifyMsg = new Networking.Messages.ClientNpcSpawnNotifyMessage
                {
                    UnitSOId = unitIdValue,
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                };
                net.SendToAll(notifyMsg);
                Plugin.Log.LogInfo($"EnemyAISync [Client]: Froze NpcUpdateManager-activated NPC UnitId={unitIdValue} at {pos}, notified host");
            }
        }

        #endregion

        #region Helpers — NPC State & Auto-Registration

        /// <summary>
        /// Returns true if this NPC should be excluded from combat logic
        /// (dead, civilian, or cowering).
        /// </summary>
        private static bool ShouldSkipCombat(object npc)
        {
            // Dead check
            if (_isAliveProp != null)
            {
                try { if (!(bool)_isAliveProp.GetValue(npc)) return true; }
                catch { }
            }
            // Civilian check
            if (_isCivilianProp != null)
            {
                try { if ((bool)_isCivilianProp.GetValue(npc)) return true; }
                catch { }
            }
            // Cowering check
            if (_isCoweringProp != null)
            {
                try { if ((bool)_isCoweringProp.GetValue(npc)) return true; }
                catch { }
            }
            return false;
        }

        /// <summary>
        /// Get UnitId value from an NPC Unit for auto-registration.
        /// </summary>
        private static ushort GetUnitIdValueFromNpc(object npc)
        {
            if (npc == null || _unitSOField == null || _unitSOIdField == null || _unitIdValueField == null)
                return 0;
            try
            {
                var unitSo = _unitSOField.GetValue(npc);
                if (unitSo == null) return 0;
                var unitId = _unitSOIdField.GetValue(unitSo);
                return (ushort)_unitIdValueField.GetValue(unitId);
            }
            catch { return 0; }
        }

        /// <summary>
        /// Get health from an NPC Unit for auto-registration.
        /// </summary>
        private static float GetHealthFromNpc(object npc)
        {
            if (npc == null || _getCurrentHealthMethod == null) return 0f;
            try { return (float)_getCurrentHealthMethod.Invoke(npc, null); }
            catch { return 0f; }
        }

        #endregion

        #region NPC Activation Helpers

        private static bool IsExcludedFromLOD(object npc)
        {
            if (_excludeFromNpcLODField == null) return false;
            try { return (bool)_excludeFromNpcLODField.GetValue(npc); }
            catch { return false; }
        }

        private static void InvokeActivateBehaviour(object npc)
        {
            if (_activateBehaviourMethod == null) return;
            try { _activateBehaviourMethod.Invoke(npc, null); }
            catch (Exception ex) { Plugin.Log.LogWarning($"EnemyAISync: ActivateBehaviour failed: {ex.Message}"); }
        }

        /// <summary>
        /// Public accessor for EntitySyncManager to call ActivateBehaviour on force-spawned NPCs.
        /// </summary>
        internal static void InvokeActivateBehaviourStatic(object npc)
        {
            InitReflection();
            InvokeActivateBehaviour(npc);
        }

        #endregion

        #region Helpers

        private static GameObject GetHostPlayerObject()
        {
            if (_gmInstanceProp == null || _playerObjectProp == null)
                return null;
            try
            {
                var gm = _gmInstanceProp.GetValue(null);
                if (gm == null || (gm is UnityEngine.Object uObj && uObj == null))
                    return null;
                var playerObj = _playerObjectProp.GetValue(gm);
                if (playerObj == null || (playerObj is UnityEngine.Object uPlayer && uPlayer == null))
                    return null;
                if (playerObj is Component comp) return comp.gameObject;
                if (playerObj is GameObject go) return go;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Check if the host's PlayerUnit has unitState == Dead.
        /// PlayerObject may still exist (we no longer Destroy it on death),
        /// so GetHostPlayerObject() != null does NOT mean the host is alive.
        /// </summary>
        private static bool IsHostPlayerDead()
        {
            if (_gmInstanceProp == null || _playerUnitProp == null ||
                _unitStateField == null || _unitStateDead == null)
                return false;
            try
            {
                var gm = _gmInstanceProp.GetValue(null);
                if (gm == null || (gm is UnityEngine.Object uObj && uObj == null))
                    return true; // No GM = assume dead
                var playerUnit = _playerUnitProp.GetValue(gm);
                if (playerUnit == null || (playerUnit is UnityEngine.Object uPU && uPU == null))
                    return true; // No PlayerUnit = dead/destroyed
                var state = _unitStateField.GetValue(playerUnit);
                return Equals(state, _unitStateDead);
            }
            catch { return false; }
        }

        #endregion

        #region Events

        private void OnDisconnected(string reason)
        {
            // Reset occlusion mask cache (may change between levels/sessions)
            _occlusionMaskCached = false;
            _cachedOcclusionMask = 0;
        }

        #endregion

        #region Reflection

        private static void InitReflection()
        {
            if (_reflectionInit) return;
            _reflectionInit = true;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (_npcType == null)
                    _npcType = asm.GetType("PerfectRandom.Sulfur.Core.Units.Npc");
                if (_aiAgentType == null)
                    _aiAgentType = asm.GetType("PerfectRandom.Sulfur.Core.Units.AI.AiAgent");
                if (_gameManagerType == null)
                    _gameManagerType = asm.GetType("PerfectRandom.Sulfur.Core.GameManager");
                if (_npcUpdateManagerType == null)
                    _npcUpdateManagerType = asm.GetType("PerfectRandom.Sulfur.Core.NpcUpdateManager");
                if (_batchedNpcRaycastsType == null)
                    _batchedNpcRaycastsType = asm.GetType("PerfectRandom.Sulfur.Core.BatchedNPCRaycasts");

                if (_npcType != null && _aiAgentType != null && _gameManagerType != null &&
                    _npcUpdateManagerType != null && _batchedNpcRaycastsType != null)
                    break;
            }

            // AiAgent
            if (_aiAgentType != null)
            {
                // DistanceToPlayer — used by AI for decisions, we override with min(host, remote)
                _distanceToPlayerProp = _aiAgentType.GetProperty("DistanceToPlayer",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_distanceToPlayerProp != null)
                    Plugin.Log.LogInfo("EnemyAISync: Found AiAgent.DistanceToPlayer");

                // hostilesInLOS — public List<Unit> property, we append remote player Units after Burst job
                _hostilesInLOSProp = _aiAgentType.GetProperty("hostilesInLOS",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_hostilesInLOSProp != null)
                    Plugin.Log.LogInfo("EnemyAISync: Found AiAgent.hostilesInLOS");
                else
                    Plugin.Log.LogWarning("EnemyAISync: Could not find AiAgent.hostilesInLOS");

                // useLineOfSight — public bool field, skip NPCs where false (BatchedNPCRaycasts doesn't manage them)
                _useLineOfSightField = _aiAgentType.GetField("useLineOfSight",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_useLineOfSightField != null)
                    Plugin.Log.LogInfo("EnemyAISync: Found AiAgent.useLineOfSight");
                else
                    Plugin.Log.LogWarning("EnemyAISync: Could not find AiAgent.useLineOfSight");

                // GetTarget — hook target for host-death resilience
                _getTargetMethod = _aiAgentType.GetMethod("GetTarget",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (_getTargetMethod != null)
                    Plugin.Log.LogInfo($"EnemyAISync: Found AiAgent.GetTarget: {_getTargetMethod}");
                else
                    Plugin.Log.LogWarning("EnemyAISync: Could not find AiAgent.GetTarget");
            }

            // Npc fields/properties
            if (_npcType != null)
            {
                _npcAiAgentProp = _npcType.GetProperty("AiAgent",
                    BindingFlags.Public | BindingFlags.Instance);

                if (_npcAiAgentProp != null)
                    Plugin.Log.LogInfo("EnemyAISync: Found Npc.AiAgent");

                // Unit fields for auto-registration (Npc inherits from Unit)
                _unitSOField = _npcType.GetField("unitSO",
                    BindingFlags.Public | BindingFlags.Instance);
                _getCurrentHealthMethod = _npcType.GetMethod("GetCurrentHealth",
                    BindingFlags.Public | BindingFlags.Instance);

                // NPC state properties (inherited from Unit)
                _isAliveProp = _npcType.GetProperty("IsAlive",
                    BindingFlags.Public | BindingFlags.Instance);
                _isCivilianProp = _npcType.GetProperty("IsCivilian",
                    BindingFlags.Public | BindingFlags.Instance);
                _isCoweringProp = _npcType.GetProperty("IsCowering",
                    BindingFlags.Public | BindingFlags.Instance);

                if (_isAliveProp != null)
                    Plugin.Log.LogInfo("EnemyAISync: Found Unit.IsAlive");
                if (_isCivilianProp != null)
                    Plugin.Log.LogInfo("EnemyAISync: Found Unit.IsCivilian");
                if (_isCoweringProp != null)
                    Plugin.Log.LogInfo("EnemyAISync: Found Unit.IsCowering");

                // Unit.unitState field + UnitState.Dead enum — for GetTarget fallback dead-check
                _unitStateField = _npcType.GetField("unitState",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_unitStateField != null)
                {
                    Plugin.Log.LogInfo("EnemyAISync: Found Unit.unitState");
                    try { _unitStateDead = Enum.Parse(_unitStateField.FieldType, "Dead"); }
                    catch { Plugin.Log.LogWarning("EnemyAISync: Could not resolve UnitState.Dead"); }
                }

                // Npc.SetShooting — hook to block ShootTriggerRoutine on client
                _setShootingMethod = _npcType.GetMethod("SetShooting",
                    BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(bool) }, null);
                if (_setShootingMethod != null)
                    Plugin.Log.LogInfo("EnemyAISync: Found Npc.SetShooting");

                // NPC activation fields
                _excludeFromNpcLODField = _npcType.GetField("excludeFromNpcLOD",
                    BindingFlags.Public | BindingFlags.Instance);
                _activateBehaviourMethod = _npcType.GetMethod("ActivateBehaviour",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (_activateBehaviourMethod != null)
                    Plugin.Log.LogInfo("EnemyAISync: Found Npc.ActivateBehaviour()");
            }

            // UnitSO.id → UnitId.value for auto-registration
            if (_unitSOField != null)
            {
                var unitSOType = _unitSOField.FieldType;
                _unitSOIdField = unitSOType.GetField("id",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_unitSOIdField != null)
                {
                    var unitIdType = _unitSOIdField.FieldType;
                    _unitIdValueField = unitIdType.GetField("value",
                        BindingFlags.Public | BindingFlags.Instance);
                }
            }

            // GameManager
            if (_gameManagerType != null)
            {
                _gmInstanceProp = _gameManagerType.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                _aliveNpcsField = _gameManagerType.GetField("aliveNpcs",
                    BindingFlags.Public | BindingFlags.Instance);
                _playerObjectProp = _gameManagerType.GetProperty("PlayerObject",
                    BindingFlags.Public | BindingFlags.Instance);
                _playerUnitProp = _gameManagerType.GetProperty("PlayerUnit",
                    BindingFlags.Public | BindingFlags.Instance);
                _gmNpcsProp = _gameManagerType.GetProperty("npcs",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_gmNpcsProp != null)
                    Plugin.Log.LogInfo("EnemyAISync: Found GameManager.npcs (all NPCs)");

                // EnemyOcclusionLayers — used for Physics.Linecast LOS checks
                _enemyOcclusionLayersProp = _gameManagerType.GetProperty("EnemyOcclusionLayers",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_enemyOcclusionLayersProp != null)
                    Plugin.Log.LogInfo("EnemyAISync: Found GameManager.EnemyOcclusionLayers");
                else
                    Plugin.Log.LogWarning("EnemyAISync: Could not find GameManager.EnemyOcclusionLayers");
            }

            // NpcUpdateManager.LateUpdate — hook target for DistanceToPlayer override
            if (_npcUpdateManagerType != null)
            {
                _npcUpdateLateUpdateMethod = _npcUpdateManagerType.GetMethod("LateUpdate",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (_npcUpdateLateUpdateMethod != null)
                    Plugin.Log.LogInfo($"EnemyAISync: Found NpcUpdateManager.LateUpdate: {_npcUpdateLateUpdateMethod}");
                else
                    Plugin.Log.LogWarning("EnemyAISync: Could not find NpcUpdateManager.LateUpdate");
            }
            else
            {
                Plugin.Log.LogWarning("EnemyAISync: Could not find NpcUpdateManager type");
            }

            // BatchedNPCRaycasts.LateUpdate — hook target for LOS injection
            if (_batchedNpcRaycastsType != null)
            {
                _batchedNpcLateUpdateMethod = _batchedNpcRaycastsType.GetMethod("LateUpdate",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (_batchedNpcLateUpdateMethod != null)
                    Plugin.Log.LogInfo($"EnemyAISync: Found BatchedNPCRaycasts.LateUpdate: {_batchedNpcLateUpdateMethod}");
                else
                    Plugin.Log.LogWarning("EnemyAISync: Could not find BatchedNPCRaycasts.LateUpdate");
            }
            else
            {
                Plugin.Log.LogWarning("EnemyAISync: Could not find BatchedNPCRaycasts type");
            }
        }

        private static System.Collections.IList GetAliveNpcs()
        {
            if (_gmInstanceProp == null || _aliveNpcsField == null)
                return null;
            try
            {
                var gm = _gmInstanceProp.GetValue(null);
                if (gm == null || (gm is UnityEngine.Object uObj && uObj == null))
                    return null;
                return _aliveNpcsField.GetValue(gm) as System.Collections.IList;
            }
            catch { return null; }
        }

        /// <summary>
        /// Get GameManager.npcs — ALL NPCs including inactive ones.
        /// Different from aliveNpcs which only contains active/alive NPCs.
        /// </summary>
        private static System.Collections.IList GetAllNpcs()
        {
            if (_gmInstanceProp == null || _gmNpcsProp == null)
                return null;
            try
            {
                var gm = _gmInstanceProp.GetValue(null);
                if (gm == null || (gm is UnityEngine.Object uObj && uObj == null))
                    return null;
                return _gmNpcsProp.GetValue(gm) as System.Collections.IList;
            }
            catch { return null; }
        }

        #endregion
    }
}
