using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using MonoMod.RuntimeDetour;
using SulfurMP.Combat;
using SulfurMP.Config;
using SulfurMP.Networking;
using SulfurMP.Networking.Messages;
using SulfurMP.Players;
using Steamworks;
using UnityEngine;

namespace SulfurMP.Entities
{
    /// <summary>
    /// Central coordinator for entity (NPC) identification and sync.
    /// Host assigns NetworkEntityIds to all NPCs; clients match by UnitId + position.
    ///
    /// Hooks:
    /// - UnitSO.SpawnUnit (static) — detect new NPC spawns
    /// - Unit.Die (virtual instance) — detect NPC deaths
    /// </summary>
    public class EntitySyncManager : MonoBehaviour
    {
        public static EntitySyncManager Instance { get; private set; }

        public EntityRegistry Registry { get; private set; } = new EntityRegistry();

        // Hooks
        private bool _hookAttempted;
        private int _hookRetryCount;
        private const int MaxHookRetries = 60; // ~1 second at 60fps
        private Hook _spawnUnitHook;
        private Hook _dieHook;
        private Hook _getAimPositionHook;

        // Flag: suppresses individual spawn broadcasts during level loading (batch instead)
        private bool _batchPending;
        public bool IsBatchPending => _batchPending;
        private Coroutine _hostBatchCoroutine;

        // NPC position sync timer
        private float _npcPosSendTimer;
        private const float NpcPosSendInterval = 0.0167f; // 60Hz

        // Unmatched batch entries — retry matching periodically
        private List<EntityBatchSpawnMessage.EntityEntry> _unmatchedEntries;
        private HashSet<GameObject> _usedInBatch;
        private float _retryTimer;
        private float _retryElapsed;
        private const float RetryInterval = 1.0f;
        private const float RetryMaxDuration = 30f;

        // Pending dynamic spawns — EntitySpawnMessages that couldn't be matched immediately
        private readonly List<PendingSpawn> _pendingDynamicSpawns = new List<PendingSpawn>();
        private float _dynamicSpawnRetryTimer;
        private const float DynamicSpawnRetryInterval = 0.5f;
        private const float DynamicSpawnRetryMaxDuration = 15f;

        private struct PendingSpawn
        {
            public ushort EntityId;
            public ushort UnitIdValue;
            public Vector3 Position;
            public float Timestamp;
        }

        // Force-spawn support — host spawns NPC on behalf of client notification
        private static bool _isForceSpawning;
        // Client force-spawn — suppresses ClientNpcSpawnNotify in SpawnUnitInterceptor
        private static bool _isClientForceSpawning;
        private static MethodInfo _loadAndSpawnUnitMethod;  // UnitSO.LoadAndSpawnUnit(Vector3, Quaternion)

        // Reflection cache
        private static bool _reflectionInit;
        private static Type _unitSOType;
        private static Type _unitType;
        private static Type _npcType;
        private static MethodInfo _spawnUnitMethod;
        private static MethodInfo _dieMethod;
        private static FieldInfo _unitSOField;       // Unit.unitSO (field, type UnitSO)
        private static FieldInfo _unitSOIdField;     // UnitSO.id (field, type UnitId)
        private static FieldInfo _unitIdValueField;  // UnitId.value (field, type ushort)
        private static FieldInfo _unitStateField;    // Unit.unitState (field, type UnitState)
        private static PropertyInfo _isNpcProp;      // Unit.isNpc (property, bool)
        private static PropertyInfo _statsProp;      // Unit.Stats (property, EntityStats)
        private static MethodInfo _getCurrentHealthMethod; // Unit.GetCurrentHealth()

        // GameManager reflection (reuse pattern from LevelSyncManager)
        private static Type _gameManagerType;
        private static PropertyInfo _gmInstanceProp;
        private static FieldInfo _aliveNpcsField;
        private static PropertyInfo _gmNpcsProp;             // GameManager.npcs (all NPCs including inactive)

        // NPC fields for client-side management
        private static FieldInfo _excludeFromNpcLODField;    // Npc.excludeFromNpcLOD (bool)
        private static FieldInfo _disableVerifyPositionField; // Npc.disableVerifyPosition (bool)
        private static FieldInfo _preventNavMeshActivationField; // Npc.preventNavMeshActivation (private bool)

        // Weapon fields for resetting stale trigger on client NPCs
        private static FieldInfo _weaponField;               // Npc.weapon (public Weapon field)
        private static FieldInfo _bIsTriggerActiveField;     // Holdable.bIsTriggerActive (public bool)
        private static MethodInfo _attachWeaponMethod;       // Npc.AttachWeapon() (private, no params)

        // TriggerSpawner / Triggerable — prevent duplicate spawns
        private static Type _triggerSpawnerType;
        private static Type _triggerableType;
        private static FieldInfo _tsUnitToSpawnField;        // TriggerSpawner.unitToSpawn (public UnitSO)
        private static FieldInfo _trigHasBeenTriggeredField;  // Triggerable.hasBeenTriggered (private bool)

        // AI types for disabling client-side NPC AI
        private static Type _behaviourTreeOwnerType;
        private static Type _richAIType;
        private static Type _aiAgentType;
        private static PropertyInfo _richAICanMoveProp;  // RichAI.canMove (bool)

        // Weapon fire counter — host reads ShootEventsSinceIdle, sends monotonic byte counter
        private static PropertyInfo _shootEventsSinceIdleProp; // Weapon.ShootEventsSinceIdle (public int, get-only)

        private struct NpcSyncState
        {
            public int LastShootEventCount;
            public byte MonotonicFireCount;
            public int LastAnimStateHash;
            public byte AnimStateChangeCount;
            public string[] BoolParamNames; // Cached Bool-type animator param names (null until initialized)
        }
        private readonly Dictionary<int, NpcSyncState> _npcSyncStates = new Dictionary<int, NpcSyncState>();

        // Aim target sync — read AiAgent.target on host, hook GetAimPosition on client
        private static PropertyInfo _npcAiAgentProp;         // Npc.AiAgent (public property)
        private static FieldInfo _aiAgentTargetField;        // AiAgent.target (public Unit field)
        private static PropertyInfo _playerUnitProp;         // GameManager.PlayerUnit
        private static MethodInfo _getAimPositionMethod;     // Npc.GetAimPosition() (public, returns Vector3)

        // MonoMod delegates — static method: NO self parameter
        private delegate object orig_SpawnUnit(object unitSo, object prefab, Vector3 pos, Quaternion rot);
        private delegate object hook_SpawnUnit(orig_SpawnUnit orig, object unitSo, object prefab, Vector3 pos, Quaternion rot);

        // MonoMod delegates — virtual instance method
        private delegate void orig_Die(object self);
        private delegate void hook_Die(orig_Die orig, object self);

        // MonoMod delegates — Npc.GetAimPosition() (public instance, returns Vector3)
        private delegate Vector3 orig_GetAimPosition(object self);
        private delegate Vector3 hook_GetAimPosition(orig_GetAimPosition orig, object self);

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
            DisposeHooks();
            if (Instance == this)
                Instance = null;
        }

        private void Update()
        {
            if (!_hookAttempted)
                TryInstallHooks();

            SendNpcPositionsIfHost();
            RetryUnmatchedEntries();
            RetryPendingDynamicSpawns();
        }

        #region Hook Installation

        private void TryInstallHooks()
        {
            InitReflection();

            if (_spawnUnitMethod == null && _dieMethod == null)
            {
                _hookRetryCount++;
                if (_hookRetryCount >= MaxHookRetries)
                {
                    _hookAttempted = true;
                    Plugin.Log.LogWarning("EntitySync: Could not find SpawnUnit/Die methods after max retries");
                }
                return;
            }

            _hookAttempted = true;

            // Hook UnitSO.SpawnUnit (static)
            if (_spawnUnitMethod != null)
            {
                try
                {
                    _spawnUnitHook = new Hook(
                        _spawnUnitMethod,
                        new hook_SpawnUnit(SpawnUnitInterceptor));
                    Plugin.Log.LogInfo("EntitySync: Installed MonoMod hook on UnitSO.SpawnUnit");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"EntitySync: Failed to hook SpawnUnit: {ex}");
                }
            }

            // Hook Unit.Die (virtual instance)
            if (_dieMethod != null)
            {
                try
                {
                    _dieHook = new Hook(
                        _dieMethod,
                        new hook_Die(DieInterceptor));
                    Plugin.Log.LogInfo("EntitySync: Installed MonoMod hook on Unit.Die");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"EntitySync: Failed to hook Unit.Die: {ex}");
                }
            }

            // Hook Npc.GetAimPosition (client only) — corrects arrow aim direction
            var net = NetworkManager.Instance;
            if (_getAimPositionMethod != null && net != null && !net.IsHost)
            {
                try
                {
                    _getAimPositionHook = new Hook(
                        _getAimPositionMethod,
                        new hook_GetAimPosition(GetAimPositionInterceptor));
                    Plugin.Log.LogInfo("EntitySync: Installed MonoMod hook on Npc.GetAimPosition (client)");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"EntitySync: Failed to hook GetAimPosition: {ex}");
                }
            }
        }

        private void DisposeHooks()
        {
            _spawnUnitHook?.Dispose();
            _spawnUnitHook = null;
            _dieHook?.Dispose();
            _dieHook = null;
            _getAimPositionHook?.Dispose();
            _getAimPositionHook = null;
            _hookAttempted = false;
        }

        #endregion

        #region SpawnUnit Hook

        private static object SpawnUnitInterceptor(
            orig_SpawnUnit orig, object unitSo, object prefab, Vector3 pos, Quaternion rot)
        {
            // Always call original first
            var result = orig(unitSo, prefab, pos, rot);

            var instance = Instance;
            var net = NetworkManager.Instance;
            if (instance == null || net == null || !net.IsConnected)
                return result;

            // Only track NPCs
            if (result == null || !IsNpc(result))
                return result;

            var go = GetGameObject(result);
            if (go == null)
                return result;

            var unitIdValue = GetUnitIdValue(unitSo);

            if (net.IsHost)
            {
                // Duplicate prevention: if we force-spawned this NPC type nearby already,
                // a native TriggerSpawner may spawn a duplicate. Detect and destroy it.
                if (!_isForceSpawning && !instance._batchPending)
                {
                    var existingGo = instance.FindRegisteredNpcByTypeAndPosition(unitIdValue, pos, 3f);
                    if (existingGo != null)
                    {
                        Plugin.Log.LogInfo($"EntitySync [Host]: Duplicate spawn detected UnitId={unitIdValue} " +
                            $"at {pos}, destroying duplicate");
                        UnityEngine.Object.Destroy(go);
                        return existingGo.GetComponent(_unitType) ?? result;
                    }
                }

                // Host: assign an ID
                var entityId = instance.Registry.AssignId(go);

                if (instance._batchPending)
                {
                    // During level load — suppress individual broadcasts, will batch later
                }
                else
                {
                    // Dynamic spawn — broadcast immediately
                    var health = GetHealth(result);
                    var state = GetUnitState(result);

                    var msg = new EntitySpawnMessage
                    {
                        EntityId = entityId.Value,
                        UnitIdValue = unitIdValue,
                        PosX = pos.x,
                        PosY = pos.y,
                        PosZ = pos.z,
                        Health = health,
                        State = state,
                    };
                    net.SendToAll(msg);
                    Plugin.Log.LogInfo($"EntitySync [Host]: Dynamic spawn {entityId} " +
                        $"UnitId={unitIdValue} at {pos}");
                }
            }
            else
            {
                // Client: disable AI immediately to freeze NPC at spawn position
                // for reliable matching when EntitySpawnMessage arrives from host.
                // Also check if this NPC matches a pending dynamic spawn.
                DisableClientAI(go);

                for (int i = instance._pendingDynamicSpawns.Count - 1; i >= 0; i--)
                {
                    var pending = instance._pendingDynamicSpawns[i];
                    if (pending.UnitIdValue == unitIdValue &&
                        Vector3.Distance(pos, pending.Position) < 15f &&
                        !instance.Registry.TryGetId(go, out _))
                    {
                        instance.Registry.Register(new NetworkEntityId(pending.EntityId), go);
                        instance._pendingDynamicSpawns.RemoveAt(i);
                        Plugin.Log.LogInfo($"EntitySync [Client]: Matched pending dynamic spawn " +
                            $"Entity({pending.EntityId}) UnitId={unitIdValue} on SpawnUnit hook");
                        break;
                    }
                }

                // If still unregistered after checking pending spawns:
                if (!instance.Registry.TryGetId(go, out _))
                {
                    if (_isClientForceSpawning)
                    {
                        // We're inside a client force-spawn — don't send notify back to host
                        // (host already knows about this NPC and sent us the EntitySpawnMessage)
                    }
                    else
                    {
                        // Check for duplicate: if a registered NPC of the same type already
                        // exists very close, this is a duplicate from a TriggerSpawner that
                        // fired locally after we force-spawned it. Destroy the duplicate.
                        var existingGo = instance.FindRegisteredNpcByTypeAndPositionClient(unitIdValue, pos, 5f);
                        if (existingGo != null)
                        {
                            Plugin.Log.LogInfo($"EntitySync [Client]: Duplicate NPC detected " +
                                $"UnitId={unitIdValue} at {pos}, destroying");
                            UnityEngine.Object.Destroy(go);
                            return existingGo.GetComponent(_unitType) ?? result;
                        }

                        // Genuinely new NPC — notify host to register it
                        var notifyMsg = new ClientNpcSpawnNotifyMessage
                        {
                            UnitSOId = unitIdValue,
                            PosX = pos.x,
                            PosY = pos.y,
                            PosZ = pos.z,
                        };
                        net.SendToAll(notifyMsg);
                        Plugin.Log.LogInfo($"EntitySync [Client]: Sent NpcSpawnNotify UnitId={unitIdValue} at {pos}");
                    }
                }
            }
            return result;
        }

        #endregion

        #region Die Hook

        private static void DieInterceptor(orig_Die orig, object self)
        {
            var instance = Instance;
            var net = NetworkManager.Instance;

            // Not in multiplayer — pass through
            if (instance == null || net == null || !net.IsConnected)
            {
                orig(self);
                return;
            }

            // Only process NPCs
            if (!IsNpc(self))
            {
                orig(self);
                return;
            }

            // Look up entity BEFORE death (Die may destroy the object)
            var go = GetGameObject(self);
            NetworkEntityId entityId = NetworkEntityId.None;
            if (go != null)
                instance.Registry.TryGetId(go, out entityId);

            // Let death complete
            orig(self);

            // Client: unfreeze Rigidbody so death animation ragdoll works,
            // then destroy NpcMotionSmoother so its LateUpdate position lerp
            // doesn't fight the death animation + gravity.
            if (!net.IsHost)
            {
                var dieGo = GetGameObject(self);
                if (dieGo != null)
                {
                    var rb = dieGo.GetComponent<Rigidbody>();
                    if (rb != null)
                        rb.constraints = RigidbodyConstraints.None;

                    var smoother = dieGo.GetComponent<NpcMotionSmoother>();
                    if (smoother != null)
                        UnityEngine.Object.Destroy(smoother);
                }
            }

            // Host: broadcast death or despawn
            if (net.IsHost && entityId.IsValid)
            {
                if (CombatSyncManager.HasPendingDeathContext)
                {
                    // Combat kill — send EntityDeathMessage (has damage type + killer info)
                    var deathMsg = new EntityDeathMessage
                    {
                        EntityId = entityId.Value,
                        DamageTypeId = CombatSyncManager.PendingDeathDamageTypeId,
                        KillerIsPlayer = CombatSyncManager.PendingDeathKillerIsPlayer,
                    };
                    net.SendToAll(deathMsg);
                    Plugin.Log.LogInfo($"EntitySync [Host]: NPC combat death {entityId} " +
                        $"(dmgType={deathMsg.DamageTypeId}, playerKill={deathMsg.KillerIsPlayer})");
                }
                else
                {
                    // Non-combat death — send generic despawn
                    var msg = new EntityDespawnMessage
                    {
                        EntityId = entityId.Value,
                        Reason = 0, // Death
                    };
                    net.SendToAll(msg);
                    Plugin.Log.LogInfo($"EntitySync [Host]: NPC died {entityId}");
                }
            }

            // Both sides: unregister
            if (entityId.IsValid)
                instance.Registry.Unregister(entityId);
        }

        #endregion

        #region GetAimPosition Hook (Client)

        /// <summary>
        /// Client-only hook on Npc.GetAimPosition(). When NpcMotionSmoother has a resolved
        /// aim target position (from host-synced TargetSteamId), return that instead of the
        /// default feet-level fallback. This makes DispatchProjectile → AimAt use the correct
        /// direction, so arrows/projectiles fly toward the targeted player.
        /// </summary>
        private static Vector3 GetAimPositionInterceptor(orig_GetAimPosition orig, object self)
        {
            if (self is Component comp && comp != null)
            {
                var smoother = comp.GetComponent<NpcMotionSmoother>();
                if (smoother != null && smoother.HasTargetAim)
                    return smoother.TargetAimPosition;
            }
            return orig(self);
        }

        #endregion

        #region NPC Position Sync

        private void SendNpcPositionsIfHost()
        {
            var net = NetworkManager.Instance;
            if (net == null || !net.IsHost || !net.IsConnected || _batchPending)
                return;

            _npcPosSendTimer += Time.deltaTime;
            if (_npcPosSendTimer < NpcPosSendInterval)
                return;
            _npcPosSendTimer = 0f;

            var npcs = GetAliveNpcs();
            if (npcs == null || npcs.Count == 0)
                return;

            var entries = new List<NpcPositionBatchMessage.NpcPosEntry>();

            foreach (var npcObj in npcs)
            {
                if (npcObj == null) continue;
                var go = GetGameObject(npcObj);
                if (go == null) continue;

                if (!Registry.TryGetId(go, out var entityId))
                {
                    // Unregistered alive NPC — activated mid-game by NpcUpdateManager
                    // as the host player explored. Register and broadcast spawn to clients.
                    entityId = Registry.AssignId(go);
                    var unitIdValue = GetUnitIdValueFromUnit(npcObj);
                    var health = GetHealth(npcObj);
                    var state = GetUnitState(npcObj);
                    var spawnPos = go.transform.position;

                    var spawnMsg = new EntitySpawnMessage
                    {
                        EntityId = entityId.Value,
                        UnitIdValue = unitIdValue,
                        PosX = spawnPos.x,
                        PosY = spawnPos.y,
                        PosZ = spawnPos.z,
                        Health = health,
                        State = state,
                    };
                    net.SendToAll(spawnMsg);
                    Plugin.Log.LogInfo($"EntitySync [Host]: Auto-registered active NPC {entityId} " +
                        $"UnitId={unitIdValue} at {spawnPos}");
                }

                var pos = go.transform.position;
                var rotY = go.transform.eulerAngles.y;

                // Fetch/create sync state early — BoolParamNames needed during animator packing
                int goId = go.GetInstanceID();
                if (!_npcSyncStates.TryGetValue(goId, out var syncState))
                    syncState = new NpcSyncState();

                // Pack animator state — generic Bool parameter bitmask
                ushort boolFlags = 0;
                int animStateHash = 0;
                byte attackVariantId = 0;
                var animator = go.GetComponent<Animator>();
                if (animator != null)
                {
                    if (syncState.BoolParamNames == null)
                        syncState.BoolParamNames = BuildBoolParamNames(animator);
                    var boolNames = syncState.BoolParamNames;
                    if (boolNames != null)
                        for (int bi = 0; bi < boolNames.Length; bi++)
                            if (animator.GetBool(boolNames[bi]))
                                boolFlags |= (ushort)(1 << bi);
                    animStateHash = animator.GetCurrentAnimatorStateInfo(0).fullPathHash;
                    try { attackVariantId = (byte)animator.GetInteger("AttackID"); } catch { }
                }

                // Track anim state change counter — monotonic byte that increments
                // whenever the host's animator state hash changes. Captures ALL transitions
                // including re-entries (e.g. rapid ranged attacks where 60Hz sampling
                // misses the brief Idle gap between shots).
                byte animChangeCount = 0;
                if (animStateHash != syncState.LastAnimStateHash)
                    syncState.AnimStateChangeCount = (byte)((syncState.AnimStateChangeCount + 1) & 0xFF);
                syncState.LastAnimStateHash = animStateHash;
                animChangeCount = syncState.AnimStateChangeCount;

                // Read aim target SteamID + fire counter
                ulong targetSteamId = 0;
                byte fireCount = 0;
                if (_npcType != null)
                {
                    var npc = go.GetComponent(_npcType);
                    if (npc != null)
                    {
                        // Read AiAgent.target and resolve to SteamID for client aim correction
                        targetSteamId = ResolveNpcTargetSteamId(npc);

                        // Track fire counter — monotonic byte that increments on real shots
                        if (_weaponField != null && _shootEventsSinceIdleProp != null)
                        {
                            try
                            {
                                var weapon = _weaponField.GetValue(npc);
                                if (weapon != null && !(weapon is UnityEngine.Object uWeapon && uWeapon == null))
                                {
                                    int shootEvents = (int)_shootEventsSinceIdleProp.GetValue(weapon);

                                    if (shootEvents > syncState.LastShootEventCount)
                                    {
                                        // Increased — real shots fired
                                        int delta = shootEvents - syncState.LastShootEventCount;
                                        syncState.MonotonicFireCount = (byte)((syncState.MonotonicFireCount + delta) & 0xFF);
                                    }
                                    // If decreased (idle reset) or same — don't increment
                                    syncState.LastShootEventCount = shootEvents;
                                    fireCount = syncState.MonotonicFireCount;
                                }
                            }
                            catch { }
                        }
                    }
                }

                _npcSyncStates[goId] = syncState;

                entries.Add(new NpcPositionBatchMessage.NpcPosEntry
                {
                    EntityId = entityId.Value,
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    RotY = rotY,
                    BoolFlags = boolFlags,
                    AnimStateHash = animStateHash,
                    TargetSteamId = targetSteamId,
                    FireCount = fireCount,
                    AttackVariantId = attackVariantId,
                    AnimChangeCount = animChangeCount,
                });
            }

            if (entries.Count == 0)
                return;

            var msg = new NpcPositionBatchMessage { Entries = entries.ToArray() };
            net.SendToAll(msg);
        }

        private void HandleNpcPositionBatch(NpcPositionBatchMessage msg)
        {
            var net = NetworkManager.Instance;
            if (net == null || net.IsHost) return; // Clients only

            var entries = msg.Entries;
            if (entries == null) return;

            for (int i = 0; i < entries.Length; i++)
            {
                ref var e = ref entries[i];
                var entityId = new NetworkEntityId(e.EntityId);

                if (!Registry.TryGetEntity(entityId, out var go) || go == null)
                    continue;

                // Activate inactive NPCs — host is sending positions, so they should be visible
                if (!go.activeSelf)
                {
                    go.SetActive(true);
                    DisableClientAI(go); // Also force-enables renderers
                }
                else
                {
                    // Already active — still ensure renderers are visible (safety net)
                    foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                    {
                        if (r != null && r.forceRenderingOff)
                            r.forceRenderingOff = false;
                    }
                }

                // Use smoother for interpolated movement + animation state
                var smoother = go.GetComponent<NpcMotionSmoother>();
                if (smoother == null)
                    smoother = go.AddComponent<NpcMotionSmoother>();
                smoother.SetTarget(new Vector3(e.PosX, e.PosY, e.PosZ), e.RotY, e.BoolFlags, e.AnimStateHash, e.TargetSteamId, e.FireCount, e.AttackVariantId, e.AnimChangeCount);
            }
        }

        /// <summary>
        /// Disable AI components on a client-side NPC so it doesn't run independently.
        /// Disables BehaviourTreeOwner (decisions), AiAgent (targeting/detection),
        /// and RichAI (pathfinding/movement). NPC becomes a passive puppet driven by
        /// host position sync.
        /// Also sets excludeFromNpcLOD to prevent NpcUpdateManager from deactivating
        /// the NPC, and activates it if currently inactive.
        /// </summary>
        internal static void DisableClientAI(GameObject go)
        {
            if (go == null) return;

            int disabled = 0;

            if (_behaviourTreeOwnerType != null)
            {
                var bt = go.GetComponent(_behaviourTreeOwnerType);
                if (bt != null)
                {
                    ((MonoBehaviour)bt).enabled = false;
                    disabled++;
                }
            }

            if (_aiAgentType != null)
            {
                var agent = go.GetComponent(_aiAgentType);
                if (agent != null)
                {
                    ((MonoBehaviour)agent).enabled = false;
                    disabled++;
                }
            }

            if (_richAIType != null)
            {
                var ai = go.GetComponent(_richAIType);
                if (ai != null)
                {
                    ((MonoBehaviour)ai).enabled = false;
                    // Also prevent movement via canMove property
                    if (_richAICanMoveProp != null)
                        try { _richAICanMoveProp.SetValue(ai, false); } catch { }
                    disabled++;
                }
            }

            // Disable root motion so animator doesn't fight NpcMotionSmoother position updates
            var animator = go.GetComponent<Animator>();
            if (animator != null)
                animator.applyRootMotion = false;

            // Npc stays enabled — Start() must run for hasDeathAnimation, and Update() must
            // run for death cleanup (CheckIfDeathCompleted → TurnOffInteraction).
            // NpcMotionSmoother uses [DefaultExecutionOrder(100)] to override Moving after Npc.
            if (_npcType != null)
            {
                var npc = go.GetComponent(_npcType);
                if (npc != null)
                {
                    // Stop stale coroutines (ShootTriggerRoutine etc) —
                    // they crash with NullRefs in ReloadWeapon / ShootTriggerRoutine.MoveNext.
                    ((MonoBehaviour)npc).StopAllCoroutines();

                    if (_excludeFromNpcLODField != null)
                        try { _excludeFromNpcLODField.SetValue(npc, true); } catch { }
                    if (_disableVerifyPositionField != null)
                        try { _disableVerifyPositionField.SetValue(npc, true); } catch { }
                    if (_preventNavMeshActivationField != null)
                        try { _preventNavMeshActivationField.SetValue(npc, true); } catch { }

                    // Ensure weapon is initialized — normally done in Npc.Start() → AttachWeapon().
                    // For NPCs disabled before Start() runs (dynamic spawns, late-activated),
                    // weapon field is null. Call AttachWeapon ourselves so NpcMotionSmoother
                    // can drive the weapon trigger for projectile spawning.
                    if (_weaponField != null)
                    {
                        var existingWeapon = _weaponField.GetValue(npc);
                        if (existingWeapon == null && _attachWeaponMethod != null)
                        {
                            try { _attachWeaponMethod.Invoke(npc, null); }
                            catch (Exception ex) { Plugin.Log.LogDebug($"EntitySync: AttachWeapon call: {ex.Message}"); }
                        }
                    }

                    // Reset weapon trigger to stop stale projectile firing.
                    // Keep weapon enabled — NpcMotionSmoother will drive trigger from attack flag.
                    if (_weaponField != null && _bIsTriggerActiveField != null)
                    {
                        try
                        {
                            var weaponObj = _weaponField.GetValue(npc);
                            if (weaponObj != null)
                                _bIsTriggerActiveField.SetValue(weaponObj, false);
                        }
                        catch { }
                    }

                    disabled++;
                }
            }

            // Freeze Rigidbody so Npc.FixedUpdate → UpdatePhysicsEnabling can't cause drift.
            // FreezeAll operates at the Unity physics engine level — isKinematic/useGravity
            // toggles become harmless because the engine won't move the body regardless.
            var rb = go.GetComponent<Rigidbody>();
            if (rb != null)
                rb.constraints = RigidbodyConstraints.FreezeAll;

            // Activate if currently inactive — NpcMotionSmoother.LateUpdate needs an active GO
            if (!go.activeSelf)
                go.SetActive(true);

            // Safety net: force-enable all renderers so NPC is always visible
            // Handles edge cases where renderers were disabled by unexpected systems
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r != null)
                {
                    r.forceRenderingOff = false;
                    r.enabled = true;
                }
            }

            if (disabled > 0)
                Plugin.Log.LogDebug($"EntitySync [Client]: Disabled {disabled} AI components on {go.name}");
        }

        #endregion

        #region Message Handling

        private void OnMessageReceived(CSteamID sender, NetworkMessage msg)
        {
            switch (msg.Type)
            {
                case MessageType.EntitySpawn:
                    HandleEntitySpawn((EntitySpawnMessage)msg);
                    break;
                case MessageType.EntityState:
                    HandleEntityBatchSpawn((EntityBatchSpawnMessage)msg);
                    break;
                case MessageType.EntityDespawn:
                    HandleEntityDespawn((EntityDespawnMessage)msg);
                    break;
                case MessageType.EnemyState:
                    HandleNpcPositionBatch((NpcPositionBatchMessage)msg);
                    break;
                case MessageType.ClientNpcSpawnNotify:
                    if (NetworkManager.Instance?.IsHost == true)
                        HandleClientNpcSpawnNotify(sender, (ClientNpcSpawnNotifyMessage)msg);
                    break;
            }
        }

        private void HandleEntitySpawn(EntitySpawnMessage msg)
        {
            var net = NetworkManager.Instance;
            if (net == null || net.IsHost) return; // Clients only

            var targetPos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            var go = FindUnregisteredNpcByTypeAndPosition(msg.UnitIdValue, targetPos, 15f);

            if (go != null)
            {
                Registry.Register(new NetworkEntityId(msg.EntityId), go);
                DisableClientAI(go);
                Plugin.Log.LogInfo($"EntitySync [Client]: Dynamic spawn matched " +
                    $"Entity({msg.EntityId}) UnitId={msg.UnitIdValue}");
            }
            else
            {
                // No local NPC found — force-spawn it on the client immediately.
                // This handles trigger-spawned NPCs that only exist on the host side.
                Plugin.Log.LogInfo($"EntitySync [Client]: No match for Entity({msg.EntityId}) " +
                    $"UnitId={msg.UnitIdValue} at {targetPos}, attempting force-spawn");
                ForceSpawnNpcOnClient(msg.UnitIdValue, targetPos, msg.EntityId);

                // If force-spawn succeeded, entity is now registered — verify
                if (!Registry.TryGetEntity(new NetworkEntityId(msg.EntityId), out _))
                {
                    // Force-spawn failed or SpawnUnitInterceptor didn't match — fall back to retry
                    _pendingDynamicSpawns.Add(new PendingSpawn
                    {
                        EntityId = msg.EntityId,
                        UnitIdValue = msg.UnitIdValue,
                        Position = targetPos,
                        Timestamp = Time.time,
                    });
                    Plugin.Log.LogWarning($"EntitySync [Client]: Force-spawn didn't register " +
                        $"Entity({msg.EntityId}), queued for retry");
                }
            }

            // Mark matching TriggerSpawners so they don't fire when this client walks to the area
            MarkNearbyTriggerSpawnersAsTriggered(msg.UnitIdValue, targetPos);
        }

        private void HandleEntityBatchSpawn(EntityBatchSpawnMessage msg)
        {
            var net = NetworkManager.Instance;
            if (net == null || net.IsHost) return; // Clients only

            int matched = 0;
            var entries = msg.Entries;
            if (entries == null || entries.Length == 0)
            {
                Plugin.Log.LogInfo("EntitySync [Client]: Received empty batch");
                return;
            }

            // Clear registry before batch (new level or late-join resync)
            Registry.Clear();
            _unmatchedEntries = null;

            // Track already-matched GameObjects to avoid double-matching same-type NPCs
            var used = new HashSet<GameObject>();
            var unmatched = new List<EntityBatchSpawnMessage.EntityEntry>();

            for (int i = 0; i < entries.Length; i++)
            {
                ref var e = ref entries[i];
                var targetPos = new Vector3(e.PosX, e.PosY, e.PosZ);
                var go = FindUnregisteredNpcByTypeAndPosition(e.UnitIdValue, targetPos, 15f, used);

                if (go != null)
                {
                    Registry.Register(new NetworkEntityId(e.EntityId), go);
                    DisableClientAI(go);
                    used.Add(go);
                    matched++;
                }
                else
                {
                    unmatched.Add(e);
                }
            }

            // Store unmatched entries for retry
            if (unmatched.Count > 0)
            {
                _unmatchedEntries = unmatched;
                _usedInBatch = used;
                _retryTimer = 0f;
                _retryElapsed = 0f;
                Plugin.Log.LogWarning($"EntitySync [Client]: {unmatched.Count} entities unmatched, " +
                    $"will retry for {RetryMaxDuration}s");
            }

            Plugin.Log.LogInfo($"EntitySync [Client]: EntityBatchSpawn received {entries.Length} entities, " +
                $"matched {matched}");
        }

        /// <summary>
        /// Periodically retry matching unmatched batch entries.
        /// NPCs may not have spawned on the client when the batch first arrived.
        /// Uses position-based matching first, then falls back to type-only matching.
        /// </summary>
        private void RetryUnmatchedEntries()
        {
            if (_unmatchedEntries == null || _unmatchedEntries.Count == 0)
                return;

            var net = NetworkManager.Instance;
            if (net == null || net.IsHost) return;

            _retryElapsed += Time.deltaTime;
            if (_retryElapsed >= RetryMaxDuration)
            {
                // Final fallback: match by type only (ignore position entirely)
                int fallbackMatched = 0;
                for (int i = _unmatchedEntries.Count - 1; i >= 0; i--)
                {
                    var e = _unmatchedEntries[i];
                    var go = FindUnregisteredNpcByType(e.UnitIdValue, _usedInBatch);
                    if (go != null)
                    {
                        Registry.Register(new NetworkEntityId(e.EntityId), go);
                        DisableClientAI(go);
                        _usedInBatch.Add(go);
                        _unmatchedEntries.RemoveAt(i);
                        fallbackMatched++;
                        Plugin.Log.LogInfo($"EntitySync [Client]: Type-only fallback matched " +
                            $"Entity({e.EntityId}) UnitId={e.UnitIdValue}");
                    }
                }

                if (_unmatchedEntries.Count > 0)
                    Plugin.Log.LogWarning($"EntitySync [Client]: Giving up on {_unmatchedEntries.Count} " +
                        "unmatched entities after retry timeout");
                else if (fallbackMatched > 0)
                    Plugin.Log.LogInfo($"EntitySync [Client]: All entities matched via type fallback!");

                _unmatchedEntries = null;
                _usedInBatch = null;
                return;
            }

            _retryTimer += Time.deltaTime;
            if (_retryTimer < RetryInterval)
                return;
            _retryTimer = 0f;

            int matched = 0;
            for (int i = _unmatchedEntries.Count - 1; i >= 0; i--)
            {
                var e = _unmatchedEntries[i];
                var targetPos = new Vector3(e.PosX, e.PosY, e.PosZ);
                var go = FindUnregisteredNpcByTypeAndPosition(e.UnitIdValue, targetPos, 15f, _usedInBatch);

                if (go != null)
                {
                    Registry.Register(new NetworkEntityId(e.EntityId), go);
                    DisableClientAI(go);
                    _usedInBatch.Add(go);
                    _unmatchedEntries.RemoveAt(i);
                    matched++;
                    Plugin.Log.LogInfo($"EntitySync [Client]: Retry matched Entity({e.EntityId}) " +
                        $"UnitId={e.UnitIdValue}");
                }
            }

            if (matched > 0)
                Plugin.Log.LogInfo($"EntitySync [Client]: Retry matched {matched} entities, " +
                    $"{_unmatchedEntries.Count} remaining");

            if (_unmatchedEntries.Count == 0)
            {
                Plugin.Log.LogInfo("EntitySync [Client]: All entities matched after retry!");
                _unmatchedEntries = null;
                _usedInBatch = null;
            }
        }

        /// <summary>
        /// Periodically retry matching pending dynamic spawns.
        /// The host may send EntitySpawnMessage before the client's game spawns the NPC.
        /// Also handles NPCs that only spawn on the host (wave spawners, proximity triggers).
        /// </summary>
        private void RetryPendingDynamicSpawns()
        {
            if (_pendingDynamicSpawns.Count == 0)
                return;

            var net = NetworkManager.Instance;
            if (net == null || net.IsHost) return;

            _dynamicSpawnRetryTimer += Time.deltaTime;
            if (_dynamicSpawnRetryTimer < DynamicSpawnRetryInterval)
                return;
            _dynamicSpawnRetryTimer = 0f;

            float now = Time.time;
            for (int i = _pendingDynamicSpawns.Count - 1; i >= 0; i--)
            {
                var pending = _pendingDynamicSpawns[i];

                // Timeout — give up, but log diagnostics first
                if (now - pending.Timestamp >= DynamicSpawnRetryMaxDuration)
                {
                    int totalOfType = 0, unregisteredOfType = 0;
                    float closestDist = float.MaxValue;
                    var diagNpcs = GetAllNpcs() ?? GetAliveNpcs();
                    if (diagNpcs != null)
                    {
                        foreach (var npcObj in diagNpcs)
                        {
                            if (npcObj == null) continue;
                            var npcGo = GetGameObject(npcObj);
                            if (npcGo == null) continue;
                            if (GetUnitIdValueFromUnit(npcObj) != pending.UnitIdValue) continue;
                            totalOfType++;
                            if (!Registry.TryGetId(npcGo, out _))
                                unregisteredOfType++;
                            float d = Vector3.Distance(npcGo.transform.position, pending.Position);
                            if (d < closestDist) closestDist = d;
                        }
                    }
                    Plugin.Log.LogWarning($"EntitySync [Client]: Giving up on dynamic spawn " +
                        $"Entity({pending.EntityId}) UnitId={pending.UnitIdValue} at {pending.Position} " +
                        $"after {DynamicSpawnRetryMaxDuration}s " +
                        $"(clientNpcs: total={totalOfType} unreg={unregisteredOfType} " +
                        $"closestDist={closestDist:F1}m)");
                    _pendingDynamicSpawns.RemoveAt(i);
                    continue;
                }

                var go = FindUnregisteredNpcByTypeAndPosition(pending.UnitIdValue, pending.Position, 15f);
                if (go != null)
                {
                    Registry.Register(new NetworkEntityId(pending.EntityId), go);
                    DisableClientAI(go);
                    _pendingDynamicSpawns.RemoveAt(i);
                    Plugin.Log.LogInfo($"EntitySync [Client]: Retry matched dynamic spawn " +
                        $"Entity({pending.EntityId}) UnitId={pending.UnitIdValue}");
                }
            }
        }

        private void HandleEntityDespawn(EntityDespawnMessage msg)
        {
            var net = NetworkManager.Instance;
            if (net == null || net.IsHost) return; // Clients only

            var entityId = new NetworkEntityId(msg.EntityId);

            // Get entity BEFORE unregistering so we can invoke Die()
            if (Registry.TryGetEntity(entityId, out var go) && go != null)
            {
                if (_unitType != null && _dieMethod != null)
                {
                    var unit = go.GetComponent(_unitType);
                    if (unit != null)
                    {
                        try
                        {
                            _dieMethod.Invoke(unit, null);
                            Plugin.Log.LogInfo($"EntitySync [Client]: Invoked Die() on Entity({msg.EntityId})");
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log.LogWarning($"EntitySync [Client]: Die() failed for Entity({msg.EntityId}): {ex.Message}");
                        }
                    }
                }
            }

            // Double-unregister is harmless — DieInterceptor may have already unregistered
            Registry.Unregister(entityId);
            Plugin.Log.LogInfo($"EntitySync [Client]: Despawn Entity({msg.EntityId}) reason={msg.Reason}");
        }

        /// <summary>
        /// Host: a client spawned an NPC we don't know about. Find it, activate it,
        /// register it, or force-spawn it so CombatSync works.
        /// </summary>
        private void HandleClientNpcSpawnNotify(CSteamID sender, ClientNpcSpawnNotifyMessage msg)
        {
            var net = NetworkManager.Instance;
            if (net == null || !net.IsHost) return;

            var targetPos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);

            // 1. Check if already registered nearby
            var existingGo = FindRegisteredNpcByTypeAndPosition(msg.UnitSOId, targetPos, 3f);
            if (existingGo != null)
            {
                // Idempotent: re-send EntitySpawnMessage to the requesting client
                if (Registry.TryGetId(existingGo, out var existingId))
                {
                    var unit = existingGo.GetComponent(_unitType);
                    var health = unit != null ? GetHealth(unit) : 0f;
                    var state = unit != null ? GetUnitState(unit) : (byte)0;
                    var spawnMsg = new EntitySpawnMessage
                    {
                        EntityId = existingId.Value,
                        UnitIdValue = msg.UnitSOId,
                        PosX = existingGo.transform.position.x,
                        PosY = existingGo.transform.position.y,
                        PosZ = existingGo.transform.position.z,
                        Health = health,
                        State = state,
                    };
                    net.SendMessage(sender, spawnMsg);
                    Plugin.Log.LogInfo($"EntitySync [Host]: NpcSpawnNotify — already registered {existingId}, re-sent to {sender}");
                }
            }
            else
            {
                // 2. Check for existing unregistered NPC nearby
                var unregGo = FindUnregisteredNpcByTypeAndPosition(msg.UnitSOId, targetPos, 5f);
                if (unregGo != null)
                {
                    // Activate if inactive
                    if (!unregGo.activeSelf)
                    {
                        unregGo.SetActive(true);
                        var npcComp = unregGo.GetComponent(_npcType);
                        if (npcComp != null)
                            EnemyAISyncManager.InvokeActivateBehaviourStatic(npcComp);
                    }

                    // Register and broadcast
                    var entityId = Registry.AssignId(unregGo);
                    var unit = unregGo.GetComponent(_unitType);
                    var health = unit != null ? GetHealth(unit) : 0f;
                    var state = unit != null ? GetUnitState(unit) : (byte)0;
                    var spawnMsg = new EntitySpawnMessage
                    {
                        EntityId = entityId.Value,
                        UnitIdValue = msg.UnitSOId,
                        PosX = unregGo.transform.position.x,
                        PosY = unregGo.transform.position.y,
                        PosZ = unregGo.transform.position.z,
                        Health = health,
                        State = state,
                    };
                    net.SendToAll(spawnMsg);
                    Plugin.Log.LogInfo($"EntitySync [Host]: NpcSpawnNotify — found unregistered, registered as {entityId} UnitId={msg.UnitSOId}");
                }
                else
                {
                    // 3. Force-spawn via LoadAndSpawnUnit
                    ForceSpawnNpc(msg.UnitSOId, targetPos);
                }
            }

            // All paths: mark host's TriggerSpawner so it won't fire again
            MarkNearbyTriggerSpawnersAsTriggered(msg.UnitSOId, targetPos);
        }

        /// <summary>
        /// Host: force-spawn an NPC by UnitSO ID at the given position.
        /// Uses UnitSO.LoadAndSpawnUnit (loads prefab via Addressables, then calls SpawnUnit).
        /// SpawnUnitInterceptor fires automatically → assigns ID → sends EntitySpawnMessage.
        /// </summary>
        private void ForceSpawnNpc(ushort unitSOId, Vector3 position)
        {
            // Find the UnitSO by ID
            if (_unitSOType == null || _unitSOIdField == null || _unitIdValueField == null)
            {
                Plugin.Log.LogWarning($"EntitySync [Host]: Cannot force-spawn — reflection not initialized");
                return;
            }

            object targetUnitSO = null;
            var allUnitSOs = Resources.FindObjectsOfTypeAll(_unitSOType);
            foreach (var uso in allUnitSOs)
            {
                try
                {
                    var idObj = _unitSOIdField.GetValue(uso);
                    var val = (ushort)_unitIdValueField.GetValue(idObj);
                    if (val == unitSOId)
                    {
                        targetUnitSO = uso;
                        break;
                    }
                }
                catch { }
            }

            if (targetUnitSO == null)
            {
                Plugin.Log.LogWarning($"EntitySync [Host]: Cannot find UnitSO for id {unitSOId}");
                return;
            }

            // Cache LoadAndSpawnUnit method
            if (_loadAndSpawnUnitMethod == null)
            {
                _loadAndSpawnUnitMethod = _unitSOType.GetMethod("LoadAndSpawnUnit",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(Vector3), typeof(Quaternion) },
                    null);
                if (_loadAndSpawnUnitMethod == null)
                {
                    Plugin.Log.LogWarning("EntitySync [Host]: Cannot find UnitSO.LoadAndSpawnUnit method");
                    return;
                }
            }

            _isForceSpawning = true;
            try
            {
                var result = _loadAndSpawnUnitMethod.Invoke(targetUnitSO,
                    new object[] { position, Quaternion.identity });

                if (result != null)
                {
                    Plugin.Log.LogInfo($"EntitySync [Host]: Force-spawned NPC UnitId={unitSOId} at {position}");

                    // ActivateBehaviour — SpawnUnit already calls this internally,
                    // but call explicitly in case the NPC needs it
                    if (_npcType != null && _npcType.IsInstanceOfType(result))
                        EnemyAISyncManager.InvokeActivateBehaviourStatic(result);
                }
                else
                {
                    Plugin.Log.LogWarning($"EntitySync [Host]: LoadAndSpawnUnit returned null for UnitId={unitSOId}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"EntitySync [Host]: Force-spawn failed: {ex}");
            }
            finally
            {
                _isForceSpawning = false;
            }
        }

        /// <summary>
        /// Client: force-spawn an NPC by UnitSO ID at the given position.
        /// Uses UnitSO.LoadAndSpawnUnit (synchronous via Addressables.WaitForCompletion).
        /// SpawnUnitInterceptor fires → matches pending spawn or we manually register.
        /// </summary>
        private void ForceSpawnNpcOnClient(ushort unitSOId, Vector3 position, ushort entityId)
        {
            if (_unitSOType == null || _unitSOIdField == null || _unitIdValueField == null)
            {
                Plugin.Log.LogWarning("EntitySync [Client]: Cannot force-spawn — reflection not initialized");
                return;
            }

            // Find the UnitSO by ID
            object targetUnitSO = null;
            var allUnitSOs = Resources.FindObjectsOfTypeAll(_unitSOType);
            foreach (var uso in allUnitSOs)
            {
                try
                {
                    var idObj = _unitSOIdField.GetValue(uso);
                    var val = (ushort)_unitIdValueField.GetValue(idObj);
                    if (val == unitSOId)
                    {
                        targetUnitSO = uso;
                        break;
                    }
                }
                catch { }
            }

            if (targetUnitSO == null)
            {
                Plugin.Log.LogWarning($"EntitySync [Client]: Cannot find UnitSO for id {unitSOId}");
                return;
            }

            // Cache LoadAndSpawnUnit method
            if (_loadAndSpawnUnitMethod == null)
            {
                _loadAndSpawnUnitMethod = _unitSOType.GetMethod("LoadAndSpawnUnit",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(Vector3), typeof(Quaternion) },
                    null);
                if (_loadAndSpawnUnitMethod == null)
                {
                    Plugin.Log.LogWarning("EntitySync [Client]: Cannot find UnitSO.LoadAndSpawnUnit method");
                    return;
                }
            }

            _isClientForceSpawning = true;
            try
            {
                var result = _loadAndSpawnUnitMethod.Invoke(targetUnitSO,
                    new object[] { position, Quaternion.identity });

                if (result != null)
                {
                    var go = GetGameObject(result);

                    // SpawnUnitInterceptor may have matched a pending dynamic spawn.
                    // If not, manually register + disable AI.
                    if (go != null && !Registry.TryGetId(go, out _))
                    {
                        Registry.Register(new NetworkEntityId(entityId), go);
                        DisableClientAI(go);
                        Plugin.Log.LogInfo($"EntitySync [Client]: Force-spawned + registered " +
                            $"Entity({entityId}) UnitId={unitSOId} at {position}");
                    }
                    else if (go != null)
                    {
                        Plugin.Log.LogInfo($"EntitySync [Client]: Force-spawned Entity({entityId}) " +
                            $"UnitId={unitSOId} — already registered by SpawnUnitInterceptor");
                    }
                }
                else
                {
                    Plugin.Log.LogWarning($"EntitySync [Client]: LoadAndSpawnUnit returned null " +
                        $"for UnitId={unitSOId}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"EntitySync [Client]: Force-spawn failed: {ex}");
            }
            finally
            {
                _isClientForceSpawning = false;
            }
        }

        /// <summary>
        /// Find a REGISTERED NPC matching the given UnitId value near targetPos.
        /// Used for duplicate detection and idempotent re-send.
        /// </summary>
        private GameObject FindRegisteredNpcByTypeAndPosition(ushort unitIdValue, Vector3 targetPos, float tolerance)
        {
            var npcs = GetAliveNpcs();
            if (npcs == null) return null;

            GameObject bestMatch = null;
            float bestDist = float.MaxValue;

            foreach (var npcObj in npcs)
            {
                if (npcObj == null) continue;
                var go = GetGameObject(npcObj);
                if (go == null) continue;

                // Only check registered NPCs
                if (!Registry.TryGetId(go, out _))
                    continue;

                var npcUnitId = GetUnitIdValueFromUnit(npcObj);
                if (npcUnitId != unitIdValue)
                    continue;

                float dist = Vector3.Distance(go.transform.position, targetPos);
                if (dist < tolerance && dist < bestDist)
                {
                    bestDist = dist;
                    bestMatch = go;
                }
            }

            return bestMatch;
        }

        /// <summary>
        /// Client: find a REGISTERED NPC matching type + position.
        /// Uses GetAllNpcs() (includes inactive) for comprehensive duplicate detection.
        /// </summary>
        private GameObject FindRegisteredNpcByTypeAndPositionClient(ushort unitIdValue, Vector3 targetPos, float tolerance)
        {
            var npcs = GetAllNpcs() ?? GetAliveNpcs();
            if (npcs == null) return null;

            GameObject bestMatch = null;
            float bestDist = float.MaxValue;

            foreach (var npcObj in npcs)
            {
                if (npcObj == null) continue;
                var go = GetGameObject(npcObj);
                if (go == null) continue;

                if (!Registry.TryGetId(go, out _))
                    continue;

                var npcUnitId = GetUnitIdValueFromUnit(npcObj);
                if (npcUnitId != unitIdValue)
                    continue;

                float dist = Vector3.Distance(go.transform.position, targetPos);
                if (dist < tolerance && dist < bestDist)
                {
                    bestDist = dist;
                    bestMatch = go;
                }
            }

            return bestMatch;
        }

        #endregion

        #region Peer / Disconnect Events

        private void OnPeerJoined(CSteamID peerId)
        {
            var net = NetworkManager.Instance;
            if (net == null || !net.IsHost) return;

            // Late joiner: send current entity batch
            if (Registry.Count > 0)
                SendEntityBatch(peerId);
        }

        private void OnDisconnected(string reason)
        {
            Plugin.Log.LogInfo($"EntitySync: Disconnected ({reason}), clearing registry");
            Registry.Clear();
            _npcSyncStates.Clear();
            _batchPending = false;
            _unmatchedEntries = null;
            _usedInBatch = null;
            _pendingDynamicSpawns.Clear();
            if (_hostBatchCoroutine != null)
            {
                StopCoroutine(_hostBatchCoroutine);
                _hostBatchCoroutine = null;
            }
        }

        #endregion

        #region Level Load Integration

        /// <summary>
        /// Called by LevelSyncManager when host begins a level transition.
        /// Clears registry, starts batch collection, and kicks off a coroutine
        /// that waits for NPCs to spawn before sending the batch.
        /// GoToLevel is async (starts a coroutine internally), so we can't
        /// send the batch synchronously after the call returns.
        /// </summary>
        public void OnLevelLoadStart()
        {
            Plugin.Log.LogInfo("EntitySync: Level load starting, clearing registry");
            Registry.Clear();
            _npcSyncStates.Clear();
            _batchPending = true;

            // Cancel any previous batch coroutine
            if (_hostBatchCoroutine != null)
            {
                StopCoroutine(_hostBatchCoroutine);
                _hostBatchCoroutine = null;
            }

            var net = NetworkManager.Instance;
            if (net != null && net.IsHost)
            {
                _hostBatchCoroutine = StartCoroutine(HostWaitAndSendBatchCoroutine());
            }
        }

        /// <summary>
        /// Host coroutine: waits for NPCs to appear after level generation,
        /// then scans, registers, and sends the entity batch.
        /// </summary>
        private IEnumerator HostWaitAndSendBatchCoroutine()
        {
            Plugin.Log.LogInfo("EntitySync [Host]: Waiting for NPCs to spawn after level load...");

            // Wait for LevelSyncManager to finish (_isLoadingLevel goes false → true → false)
            // and for aliveNpcs to be populated
            float waited = 0f;
            const float maxWait = 30f;
            const float checkInterval = 0.5f;
            int lastNpcCount = 0;
            float stableTime = 0f;
            const float stableThreshold = 1.5f; // NPCs must stay stable for this long

            // Initial delay — let the scene transition start
            yield return new WaitForSecondsRealtime(1f);
            waited += 1f;

            while (waited < maxWait)
            {
                yield return new WaitForSecondsRealtime(checkInterval);
                waited += checkInterval;

                var npcs = GetAliveNpcs();
                int npcCount = npcs?.Count ?? 0;

                if (npcCount > 0)
                {
                    if (npcCount == lastNpcCount)
                    {
                        stableTime += checkInterval;
                        if (stableTime >= stableThreshold)
                        {
                            Plugin.Log.LogInfo($"EntitySync [Host]: NPC count stabilized at {npcCount} " +
                                $"after {waited:F1}s");
                            break;
                        }
                    }
                    else
                    {
                        stableTime = 0f;
                        lastNpcCount = npcCount;
                    }
                }

                if (waited % 5f < checkInterval)
                {
                    Plugin.Log.LogInfo($"EntitySync [Host]: Still waiting for NPCs... " +
                        $"({waited:F1}s, count={npcCount})");
                }
            }

            // Done waiting — finalize batch
            _batchPending = false;
            _hostBatchCoroutine = null;

            RegisterAllAliveNpcs();
            Plugin.Log.LogInfo($"EntitySync [Host]: Level load complete, {Registry.Count} entities registered");
            SendEntityBatchToAll();
        }

        /// <summary>
        /// Called by LevelSyncManager after client finishes loading.
        /// Does NOT clear registry — HandleEntityBatchSpawn already clears before
        /// processing, and the batch may arrive before this cleanup runs (the client's
        /// scene transition coroutine can take 30s+ while the batch arrives in ~5s).
        /// </summary>
        public void OnClientLevelLoadComplete()
        {
            _batchPending = false;
            Plugin.Log.LogInfo($"EntitySync [Client]: Level load complete, {Registry.Count} entities tracked");
        }

        #endregion

        #region Batch Send

        private void SendEntityBatchToAll()
        {
            var net = NetworkManager.Instance;
            if (net == null || !net.IsHost) return;

            var msg = BuildBatchMessage();
            if (msg.Entries.Length > 0)
                net.SendToAll(msg);
        }

        private void SendEntityBatch(CSteamID target)
        {
            var net = NetworkManager.Instance;
            if (net == null || !net.IsHost) return;

            var msg = BuildBatchMessage();
            if (msg.Entries.Length > 0)
            {
                net.SendMessage(target, msg);
                Plugin.Log.LogInfo($"EntitySync [Host]: Sent batch ({msg.Entries.Length} entities) to {target}");
            }
        }

        private EntityBatchSpawnMessage BuildBatchMessage()
        {
            var npcs = GetAliveNpcs();
            var entries = new List<EntityBatchSpawnMessage.EntityEntry>();

            if (npcs != null)
            {
                foreach (var npcObj in npcs)
                {
                    if (npcObj == null) continue;
                    var go = GetGameObject(npcObj);
                    if (go == null) continue;

                    if (!Registry.TryGetId(go, out var entityId))
                        continue;

                    var unitIdValue = GetUnitIdValueFromUnit(npcObj);
                    var health = GetHealth(npcObj);
                    var state = GetUnitState(npcObj);
                    var pos = go.transform.position;

                    entries.Add(new EntityBatchSpawnMessage.EntityEntry
                    {
                        EntityId = entityId.Value,
                        UnitIdValue = unitIdValue,
                        PosX = pos.x,
                        PosY = pos.y,
                        PosZ = pos.z,
                        Health = health,
                        State = state,
                    });
                }
            }

            return new EntityBatchSpawnMessage { Entries = entries.ToArray() };
        }

        #endregion

        #region NPC Scanning & Matching

        private void RegisterAllAliveNpcs()
        {
            var npcs = GetAliveNpcs();
            if (npcs == null) return;

            int registered = 0;
            foreach (var npcObj in npcs)
            {
                if (npcObj == null) continue;
                var go = GetGameObject(npcObj);
                if (go == null) continue;

                // Skip if already registered (caught by SpawnUnit hook)
                if (Registry.TryGetId(go, out _))
                    continue;

                Registry.AssignId(go);
                registered++;
            }

            if (registered > 0)
                Plugin.Log.LogInfo($"EntitySync [Host]: Post-load scan registered {registered} additional NPCs");
        }

        /// <summary>
        /// Find an unregistered NPC matching the given UnitId value and close to targetPos.
        /// Uses GetAllNpcs() to include inactive NPCs — the game starts most NPCs inactive
        /// and NpcUpdateManager activates them based on player distance. On the client,
        /// NPCs far from the local player would never be found if we only checked alive ones.
        /// </summary>
        private GameObject FindUnregisteredNpcByTypeAndPosition(
            ushort unitIdValue, Vector3 targetPos, float tolerance,
            HashSet<GameObject> exclude = null)
        {
            var npcs = GetAllNpcs() ?? GetAliveNpcs();
            if (npcs == null) return null;

            GameObject bestMatch = null;
            float bestDist = float.MaxValue;

            foreach (var npcObj in npcs)
            {
                if (npcObj == null) continue;
                var go = GetGameObject(npcObj);
                if (go == null) continue;

                // Skip already registered
                if (Registry.TryGetId(go, out _))
                    continue;

                // Skip excluded
                if (exclude != null && exclude.Contains(go))
                    continue;

                // Check UnitId match
                var npcUnitId = GetUnitIdValueFromUnit(npcObj);
                if (npcUnitId != unitIdValue)
                    continue;

                // Check position proximity
                float dist = Vector3.Distance(go.transform.position, targetPos);
                if (dist < tolerance && dist < bestDist)
                {
                    bestDist = dist;
                    bestMatch = go;
                }
            }

            return bestMatch;
        }

        /// <summary>
        /// Find an unregistered NPC matching the given UnitId value (no position requirement).
        /// Used as a last-resort fallback when position-based matching fails.
        /// </summary>
        private GameObject FindUnregisteredNpcByType(ushort unitIdValue, HashSet<GameObject> exclude = null)
        {
            var npcs = GetAllNpcs() ?? GetAliveNpcs();
            if (npcs == null) return null;

            foreach (var npcObj in npcs)
            {
                if (npcObj == null) continue;
                var go = GetGameObject(npcObj);
                if (go == null) continue;

                if (Registry.TryGetId(go, out _))
                    continue;

                if (exclude != null && exclude.Contains(go))
                    continue;

                var npcUnitId = GetUnitIdValueFromUnit(npcObj);
                if (npcUnitId != unitIdValue)
                    continue;

                return go;
            }

            return null;
        }

        /// <summary>
        /// Marks nearby TriggerSpawners as already triggered to prevent duplicate NPC spawns.
        /// Called on host (HandleClientNpcSpawnNotify) and client (HandleEntitySpawn).
        /// </summary>
        private static void MarkNearbyTriggerSpawnersAsTriggered(ushort unitSOId, Vector3 pos)
        {
            if (_triggerSpawnerType == null || _triggerableType == null ||
                _tsUnitToSpawnField == null || _trigHasBeenTriggeredField == null)
                return;

            // Use FindObjectsOfTypeAll to include inactive triggers (client may not have
            // activated the trigger area yet). Filter out prefabs via scene.buildIndex == -1.
            var spawners = Resources.FindObjectsOfTypeAll(_triggerSpawnerType);
            foreach (var spawner in spawners)
            {
                if (!(spawner is Component spawnerComp)) continue;
                if (spawnerComp.gameObject.scene.buildIndex == -1) continue; // prefab, skip

                if (Vector3.Distance(spawnerComp.transform.position, pos) > 3f)
                    continue;

                var unitSo = _tsUnitToSpawnField.GetValue(spawner);
                if (unitSo == null) continue;
                if (GetUnitIdValue(unitSo) != unitSOId) continue;

                var triggerable = spawnerComp.GetComponent(_triggerableType);
                if (triggerable != null)
                {
                    _trigHasBeenTriggeredField.SetValue(triggerable, true);
                    Plugin.Log.LogInfo($"EntitySync: Marked TriggerSpawner as triggered " +
                        $"at {spawnerComp.transform.position} UnitId={unitSOId}");
                }
            }
        }

        #endregion

        #region Reflection

        private static void InitReflection()
        {
            if (_reflectionInit) return;
            _reflectionInit = true;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (_unitSOType == null)
                    _unitSOType = asm.GetType("PerfectRandom.Sulfur.Core.Units.UnitSO");
                if (_unitType == null)
                    _unitType = asm.GetType("PerfectRandom.Sulfur.Core.Units.Unit");
                if (_npcType == null)
                    _npcType = asm.GetType("PerfectRandom.Sulfur.Core.Units.Npc");
                if (_gameManagerType == null)
                    _gameManagerType = asm.GetType("PerfectRandom.Sulfur.Core.GameManager");
                if (_triggerSpawnerType == null)
                    _triggerSpawnerType = asm.GetType("PerfectRandom.Sulfur.Core.World.TriggerSpawner");
                if (_triggerableType == null)
                    _triggerableType = asm.GetType("PerfectRandom.Sulfur.Core.World.Triggerable");

                if (_unitSOType != null && _unitType != null && _npcType != null && _gameManagerType != null
                    && _triggerSpawnerType != null && _triggerableType != null)
                    break;
            }

            // UnitSO.SpawnUnit — static, 4 params
            if (_unitSOType != null)
            {
                _spawnUnitMethod = _unitSOType.GetMethod("SpawnUnit",
                    BindingFlags.Public | BindingFlags.Static);
                if (_spawnUnitMethod != null)
                    Plugin.Log.LogInfo($"EntitySync: Found UnitSO.SpawnUnit: {_spawnUnitMethod}");
                else
                    Plugin.Log.LogWarning("EntitySync: Could not find UnitSO.SpawnUnit");

                // UnitSO.id field
                _unitSOIdField = _unitSOType.GetField("id",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_unitSOIdField != null)
                    Plugin.Log.LogInfo("EntitySync: Found UnitSO.id field");
            }

            // Unit fields/properties
            if (_unitType != null)
            {
                _dieMethod = _unitType.GetMethod("Die",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_dieMethod != null)
                    Plugin.Log.LogInfo($"EntitySync: Found Unit.Die: {_dieMethod}");
                else
                    Plugin.Log.LogWarning("EntitySync: Could not find Unit.Die");

                _unitSOField = _unitType.GetField("unitSO",
                    BindingFlags.Public | BindingFlags.Instance);
                _unitStateField = _unitType.GetField("unitState",
                    BindingFlags.Public | BindingFlags.Instance);
                _isNpcProp = _unitType.GetProperty("isNpc",
                    BindingFlags.Public | BindingFlags.Instance);
                _statsProp = _unitType.GetProperty("Stats",
                    BindingFlags.Public | BindingFlags.Instance);
                _getCurrentHealthMethod = _unitType.GetMethod("GetCurrentHealth",
                    BindingFlags.Public | BindingFlags.Instance);
            }

            // UnitId.value field
            if (_unitSOIdField != null)
            {
                var unitIdType = _unitSOIdField.FieldType;
                _unitIdValueField = unitIdType.GetField("value",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_unitIdValueField != null)
                    Plugin.Log.LogInfo("EntitySync: Found UnitId.value field");
            }

            // GameManager — for aliveNpcs and npcs (all)
            if (_gameManagerType != null)
            {
                _gmInstanceProp = _gameManagerType.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                _aliveNpcsField = _gameManagerType.GetField("aliveNpcs",
                    BindingFlags.Public | BindingFlags.Instance);
                _gmNpcsProp = _gameManagerType.GetProperty("npcs",
                    BindingFlags.Public | BindingFlags.Instance);

                _playerUnitProp = _gameManagerType.GetProperty("PlayerUnit",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_playerUnitProp != null)
                    Plugin.Log.LogInfo("EntitySync: Found GameManager.PlayerUnit");

                if (_aliveNpcsField != null)
                    Plugin.Log.LogInfo("EntitySync: Found GameManager.aliveNpcs");
                else
                    Plugin.Log.LogWarning("EntitySync: Could not find GameManager.aliveNpcs");
            }

            // Npc fields — excludeFromNpcLOD
            if (_npcType != null)
            {
                _excludeFromNpcLODField = _npcType.GetField("excludeFromNpcLOD",
                    BindingFlags.Public | BindingFlags.Instance);
                _disableVerifyPositionField = _npcType.GetField("disableVerifyPosition",
                    BindingFlags.Public | BindingFlags.Instance);
                _preventNavMeshActivationField = _npcType.GetField("preventNavMeshActivation",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (_preventNavMeshActivationField != null)
                    Plugin.Log.LogInfo("EntitySync: Found Npc.preventNavMeshActivation");

                // Weapon fields for resetting stale trigger on client
                _weaponField = _npcType.GetField("weapon", BindingFlags.Public | BindingFlags.Instance);
                if (_weaponField != null)
                {
                    var weaponType = _weaponField.FieldType;
                    var holdableType = weaponType.BaseType;
                    if (holdableType != null)
                        _bIsTriggerActiveField = holdableType.GetField("bIsTriggerActive", BindingFlags.Public | BindingFlags.Instance);
                    if (_bIsTriggerActiveField == null)
                        _bIsTriggerActiveField = weaponType.GetField("bIsTriggerActive", BindingFlags.Public | BindingFlags.Instance);
                }

                // AttachWeapon — private instance, no params. Called in Npc.Start() to set weapon field.
                // NPCs disabled before Start() runs have null weapon; we call this ourselves.
                _attachWeaponMethod = _npcType.GetMethod("AttachWeapon",
                    BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);

                // Weapon.ShootEventsSinceIdle — public int get-only, counts shots fired per idle cycle
                if (_weaponField != null)
                {
                    var weaponType = _weaponField.FieldType;
                    _shootEventsSinceIdleProp = weaponType.GetProperty("ShootEventsSinceIdle",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (_shootEventsSinceIdleProp != null)
                        Plugin.Log.LogInfo("EntitySync: Found Weapon.ShootEventsSinceIdle");
                }

                // Npc.AiAgent — public property (lazy getter, calls GetComponent<AiAgent>())
                _npcAiAgentProp = _npcType.GetProperty("AiAgent",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_npcAiAgentProp != null)
                    Plugin.Log.LogInfo("EntitySync: Found Npc.AiAgent");

                // Npc.GetAimPosition — public instance, returns Vector3
                _getAimPositionMethod = _npcType.GetMethod("GetAimPosition",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (_getAimPositionMethod != null)
                    Plugin.Log.LogInfo("EntitySync: Found Npc.GetAimPosition");
            }

            // AI types for disabling client-side NPC AI
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (_behaviourTreeOwnerType == null)
                    {
                        foreach (var t in asm.GetTypes())
                        {
                            if (t.Name == "BehaviourTreeOwner")
                            {
                                _behaviourTreeOwnerType = t;
                                Plugin.Log.LogInfo($"EntitySync: Found BehaviourTreeOwner: {t.FullName}");
                                break;
                            }
                        }
                    }
                    if (_richAIType == null)
                        _richAIType = asm.GetType("Pathfinding.RichAI");
                    if (_aiAgentType == null)
                        _aiAgentType = asm.GetType("PerfectRandom.Sulfur.Core.Units.AI.AiAgent");
                }
                catch (ReflectionTypeLoadException) { /* Some assemblies fail GetTypes */ }

                if (_behaviourTreeOwnerType != null && _richAIType != null && _aiAgentType != null)
                    break;
            }

            if (_richAIType != null)
            {
                Plugin.Log.LogInfo($"EntitySync: Found RichAI: {_richAIType.FullName}");
                _richAICanMoveProp = _richAIType.GetProperty("canMove",
                    BindingFlags.Public | BindingFlags.Instance);
            }
            else
                Plugin.Log.LogWarning("EntitySync: Could not find Pathfinding.RichAI");

            if (_aiAgentType != null)
            {
                Plugin.Log.LogInfo($"EntitySync: Found AiAgent: {_aiAgentType.FullName}");

                // AiAgent.target — public Unit field, current hostile target
                _aiAgentTargetField = _aiAgentType.GetField("target",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_aiAgentTargetField != null)
                    Plugin.Log.LogInfo("EntitySync: Found AiAgent.target");
            }
            else
                Plugin.Log.LogWarning("EntitySync: Could not find AiAgent");

            // TriggerSpawner / Triggerable — for duplicate spawn prevention
            if (_triggerSpawnerType != null)
            {
                _tsUnitToSpawnField = _triggerSpawnerType.GetField("unitToSpawn",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_tsUnitToSpawnField != null)
                    Plugin.Log.LogInfo("EntitySync: Found TriggerSpawner.unitToSpawn");
            }
            if (_triggerableType != null)
            {
                _trigHasBeenTriggeredField = _triggerableType.GetField("hasBeenTriggered",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (_trigHasBeenTriggeredField != null)
                    Plugin.Log.LogInfo("EntitySync: Found Triggerable.hasBeenTriggered");
            }
        }

        /// <summary>
        /// Host only: read AiAgent.target from an NPC and resolve it to a SteamID.
        /// Checks RemotePlayerMarker.RegisteredUnits for remote players first,
        /// then checks if target is the host's PlayerUnit.
        /// Returns 0 if NPC has no target or target isn't a player.
        /// </summary>
        private static ulong ResolveNpcTargetSteamId(object npc)
        {
            if (_npcAiAgentProp == null || _aiAgentTargetField == null)
                return 0;

            try
            {
                var aiAgent = _npcAiAgentProp.GetValue(npc);
                if (aiAgent == null) return 0;

                var target = _aiAgentTargetField.GetValue(aiAgent);
                if (target == null) return 0;

                // Check if target is a remote player (via RegisteredUnits → find marker)
                if (RemotePlayerMarker.RegisteredUnits.Contains(target))
                {
                    // target is a Unit on a remote player capsule — get SteamId from marker
                    if (target is Component comp && comp != null)
                    {
                        var marker = comp.GetComponent<RemotePlayerMarker>();
                        if (marker != null)
                            return marker.SteamId;
                    }
                }

                // Check if target is the host's own PlayerUnit
                if (_playerUnitProp != null && _gmInstanceProp != null)
                {
                    var gm = _gmInstanceProp.GetValue(null);
                    if (gm != null && !(gm is UnityEngine.Object uGm && uGm == null))
                    {
                        var hostUnit = _playerUnitProp.GetValue(gm);
                        if (hostUnit != null && ReferenceEquals(target, hostUnit))
                            return SteamUser.GetSteamID().m_SteamID;
                    }
                }
            }
            catch { }

            return 0;
        }

        /// <summary>
        /// Build an array of Bool-type animator parameter names for generic bitmask sync.
        /// Both host and client use the same NPC prefab → same AnimatorController, so
        /// animator.parameters returns params in identical deterministic order.
        /// Array index = bit position in the ushort bitmask (max 16 bools).
        /// Trigger-type params (e.g. "Attack" on melee NPCs) are excluded by the type check.
        /// </summary>
        private static string[] BuildBoolParamNames(Animator animator)
        {
            var allParams = animator.parameters;
            int count = 0;
            for (int i = 0; i < allParams.Length; i++)
                if (allParams[i].type == AnimatorControllerParameterType.Bool)
                    count++;
            if (count == 0) return null;
            if (count > 16) count = 16;
            var names = new string[count];
            int idx = 0;
            for (int i = 0; i < allParams.Length && idx < count; i++)
                if (allParams[i].type == AnimatorControllerParameterType.Bool)
                    names[idx++] = allParams[i].name;
            return names;
        }

        private static bool IsNpc(object unit)
        {
            if (unit == null || _npcType == null) return false;

            // Check type directly (fastest)
            if (_npcType.IsInstanceOfType(unit))
                return true;

            // Fallback: check isNpc property
            if (_isNpcProp != null)
            {
                try { return (bool)_isNpcProp.GetValue(unit); }
                catch { return false; }
            }

            return false;
        }

        private static GameObject GetGameObject(object unit)
        {
            if (unit is Component comp && comp != null)
                return comp.gameObject;
            if (unit is GameObject go && go != null)
                return go;
            return null;
        }

        private static ushort GetUnitIdValue(object unitSo)
        {
            if (unitSo == null || _unitSOIdField == null || _unitIdValueField == null)
                return 0;

            try
            {
                var unitId = _unitSOIdField.GetValue(unitSo);
                return (ushort)_unitIdValueField.GetValue(unitId);
            }
            catch { return 0; }
        }

        private static ushort GetUnitIdValueFromUnit(object unit)
        {
            if (unit == null || _unitSOField == null)
                return 0;

            try
            {
                var unitSo = _unitSOField.GetValue(unit);
                return GetUnitIdValue(unitSo);
            }
            catch { return 0; }
        }

        private static float GetHealth(object unit)
        {
            if (unit == null || _getCurrentHealthMethod == null)
                return 0f;

            try
            {
                return (float)_getCurrentHealthMethod.Invoke(unit, null);
            }
            catch { return 0f; }
        }

        private static byte GetUnitState(object unit)
        {
            if (unit == null || _unitStateField == null)
                return 0;

            try
            {
                var state = _unitStateField.GetValue(unit);
                return (byte)(int)state; // enum → int → byte
            }
            catch { return 0; }
        }

        /// <summary>
        /// Get GameManager.npcs — ALL NPCs including inactive ones.
        /// Required for client-side matching: many NPCs start inactive and NpcUpdateManager
        /// only activates them near the local player, so inactive ones would be missed.
        /// </summary>
        private static System.Collections.IList GetAllNpcs()
        {
            InitReflection();
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

        /// <summary>
        /// Get GameManager.aliveNpcs as a list of objects (avoids strong typing).
        /// Returns null if GameManager not available.
        /// </summary>
        private static System.Collections.IList GetAliveNpcs()
        {
            InitReflection();
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

        #endregion
    }
}
