using System;
using System.Linq;
using System.Reflection;
using MonoMod.RuntimeDetour;
using SulfurMP.Entities;
using SulfurMP.Networking;
using SulfurMP.Networking.Messages;
using SulfurMP.Players;
using Steamworks;
using UnityEngine;

namespace SulfurMP.Combat
{
    /// <summary>
    /// Host-authoritative combat synchronization.
    /// Hooks Hitbox.TakeHit(IDamager) to intercept all weapon/projectile damage:
    /// - Host hits NPC: damage applies normally via orig, result broadcast to clients
    /// - Client hits NPC: TakeHit suppressed locally, DamageRequestMessage sent to host
    /// - Host receives request: applies damage via RecieveDamage(DSD), broadcasts result
    /// - Client receives result: sets NPC health directly
    ///
    /// We hook TakeHit instead of RecieveDamage because the game's actual damage path is:
    ///   Hitbox.TakeHit(IDamager) → TakeHit(DamageSourceData) → Owner.RecieveDamage(DSD)
    /// The IDamager overload of RecieveDamage is a dead-letter wrapper never called by weapons.
    /// And the DSD overload can't be hooked via MonoMod delegates (DamageSourceData is a struct).
    ///
    /// Integrates with EntitySyncManager's DieInterceptor via pending death context
    /// to send EntityDeathMessage instead of EntityDespawnMessage for combat kills.
    /// </summary>
    public class CombatSyncManager : MonoBehaviour
    {
        public static CombatSyncManager Instance { get; private set; }

        // Pending death context — shared with EntitySyncManager.DieInterceptor
        public static bool HasPendingDeathContext;
        public static byte PendingDeathDamageTypeId;
        public static byte PendingDeathKillerIsPlayer;

        // Hook state
        private bool _hookAttempted;
        private int _hookRetryCount;
        private const int MaxHookRetries = 60;
        private Hook _takeHitHook;

        // Effect capture hooks (Phase 12)
        private Hook _setHitEffectHook;
        private Hook _drawBulletHoleHook;
        private Hook _createInvulnEffectHook;

        // Capture state — filled by host-side hooks during orig() calls
        private static bool _isCapturing;
        private static byte _capturedHitState;          // 0 = none
        private static bool _capturedBulletHole;
        private static Vector3 _capturedBulletHolePos;
        private static Vector3 _capturedBulletHoleDir;
        private static byte _capturedCaliberByte;
        private static bool _capturedInvulnerable;
        private static Vector3 _capturedInvulnerablePos;

        // Reflection cache
        private static bool _reflectionInit;

        // Types
        private static Type _unitType;
        private static Type _npcType;
        private static Type _hitboxType;              // Hitbox (MonoBehaviour)
        private static Type _damageTypeType;           // DamageType (ScriptableObject)
        private static Type _damageSourceDataType;     // DamageSourceData (struct)
        private static Type _entityStatsType;          // EntityStats
        private static Type _entityAttributesType;     // EntityAttributes (enum)
        private static Type _asyncAssetLoadingType;    // AsyncAssetLoading
        private static Type _iDamagerType;             // IDamager (interface)

        // Methods
        private static MethodInfo _takeHitMethod;          // Hitbox.TakeHit(IDamager) — hook target
        private static MethodInfo _recieveDamageDsdMethod;  // Unit.RecieveDamage(DSD) — for host InvokeRecieveDamage
        private static MethodInfo _dieMethod;              // Unit.Die()
        private static MethodInfo _getCurrentHealthMethod;  // Unit.GetCurrentHealth()
        private static MethodInfo _setStatusMethod;        // EntityStats.SetStatus(EntityAttributes, float, bool)

        // Fields / Properties
        private static PropertyInfo _hitboxOwnerProp;       // Hitbox.Owner (Unit)
        private static FieldInfo _isPlayerField;            // Unit.isPlayer (bool field)
        private static PropertyInfo _sourceUnitProp;        // IDamager.SourceUnit (property)
        private static PropertyInfo _statsProp;             // Unit.Stats (EntityStats property)
        private static FieldInfo _damageTypeIdField;        // DamageType.id (DamageTypes enum)
        private static PropertyInfo _assetLoadingInstanceProp; // AsyncAssetLoading.Instance
        private static PropertyInfo _assetSetsProp;         // AsyncAssetLoading.assetSets
        private static FieldInfo _damageTypesArrayField;    // AssetSets.damageTypes

        // DamageSourceData fields (for constructing dummy struct)
        private static FieldInfo _dsdNameField;         // DamageSourceData.name
        private static FieldInfo _dsdTransformField;    // DamageSourceData.transform

        // EntityAttributes enum value
        private static object _statusCurrentHealth;     // EntityAttributes.Status_CurrentHealth

        // Hurt animation support
        private static Type _unitEventType;                     // UnitEvent enum
        private static object _unitEventTakeDamage;             // UnitEvent.TakeDamage value
        private static MethodInfo _triggerOneShotEventMethod;   // Unit.TriggerOneShotEvent(UnitEvent)

        // Blood effect support (client-side hit effects)
        private static Type _bloodEffectDataType;                    // BloodEffectData (ScriptableObject)
        private static MethodInfo _createBloodProjectilesMethod;     // Unit.CreateBloodProjectiles(int, Vector3, BloodEffectData)
        private static PropertyInfo _currentBloodEffectDataProp;     // Unit.currentBloodEffectData
        private static FieldInfo _amountOfBloodPerHitField;          // Hitbox.AmountOfBloodPerHit (int)
        private static FieldInfo _drawsBloodField;                   // DamageType.projectilesOfThisTypeDrawBlood (bool)

        // Hit effect sync types (Phase 12)
        private static Type _hitStateType;                           // HitState enum
        private static Type _caliberTypesType;                       // CaliberTypes enum : byte

        // Hit effect sync methods (Phase 12)
        private static MethodInfo _setHitEffectMethod;               // Npc.SetHitEffect(HitState)
        private static MethodInfo _drawBulletHoleMethod;             // Npc.DrawBulletHole(Vector3, Quaternion, BloodHitEffect, CaliberTypes)
        private static MethodInfo _createInvulnEffectMethod;         // Unit.CreateInvulnerableEffect(Vector3)

        // Hit effect sync fields (Phase 12)
        private static FieldInfo _bulletHoleEffectField;             // Unit.bulletHoleEffect (BloodHitEffect)

        // MonoMod delegates for Hitbox.TakeHit(float, DamageType, IDamager, Vector3)
        // TakeHit is void, non-virtual instance method
        private delegate void orig_TakeHit(
            object self, float damage, object damageType, object source, Vector3 collisionPoint);

        private delegate void hook_TakeHit(
            orig_TakeHit orig, object self, float damage, object damageType, object source, Vector3 collisionPoint);

        // Npc.SetHitEffect — virtual override, HitState is enum (int underlying)
        private delegate void orig_SetHitEffect(object self, int hitState);
        private delegate void hook_SetHitEffect(orig_SetHitEffect orig, object self, int hitState);

        // Npc.DrawBulletHole — instance, BloodHitEffect=object, CaliberTypes=byte
        private delegate void orig_DrawBulletHole(
            object self, Vector3 pos, Quaternion rot, object bloodEffect, byte caliber);
        private delegate void hook_DrawBulletHole(
            orig_DrawBulletHole orig, object self, Vector3 pos, Quaternion rot, object bloodEffect, byte caliber);

        // Unit.CreateInvulnerableEffect — protected, Vector3 param
        private delegate void orig_CreateInvulnEffect(object self, Vector3 collisionPoint);
        private delegate void hook_CreateInvulnEffect(orig_CreateInvulnEffect orig, object self, Vector3 collisionPoint);

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
        }

        private void OnDisable()
        {
            NetworkEvents.OnMessageReceived -= OnMessageReceived;
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
                TryInstallHook();
        }

        #region Hook Installation

        private void TryInstallHook()
        {
            InitReflection();

            if (_takeHitMethod == null)
            {
                _hookRetryCount++;
                if (_hookRetryCount >= MaxHookRetries)
                {
                    _hookAttempted = true;
                    Plugin.Log.LogWarning("CombatSync: Could not find Hitbox.TakeHit after max retries");
                }
                return;
            }

            _hookAttempted = true;

            try
            {
                _takeHitHook = new Hook(
                    _takeHitMethod,
                    new hook_TakeHit(TakeHitInterceptor));
                Plugin.Log.LogInfo("CombatSync: Installed MonoMod hook on Hitbox.TakeHit(IDamager)");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"CombatSync: Failed to hook Hitbox.TakeHit: {ex}");
            }

            // Effect capture hooks (Phase 12)
            try
            {
                if (_setHitEffectMethod != null)
                {
                    _setHitEffectHook = new Hook(
                        _setHitEffectMethod,
                        new hook_SetHitEffect(SetHitEffectInterceptor));
                    Plugin.Log.LogInfo("CombatSync: Installed hook on Npc.SetHitEffect");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"CombatSync: Failed to hook SetHitEffect: {ex.Message}");
            }

            try
            {
                if (_drawBulletHoleMethod != null)
                {
                    _drawBulletHoleHook = new Hook(
                        _drawBulletHoleMethod,
                        new hook_DrawBulletHole(DrawBulletHoleInterceptor));
                    Plugin.Log.LogInfo("CombatSync: Installed hook on Npc.DrawBulletHole");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"CombatSync: Failed to hook DrawBulletHole: {ex.Message}");
            }

            try
            {
                if (_createInvulnEffectMethod != null)
                {
                    _createInvulnEffectHook = new Hook(
                        _createInvulnEffectMethod,
                        new hook_CreateInvulnEffect(CreateInvulnEffectInterceptor));
                    Plugin.Log.LogInfo("CombatSync: Installed hook on Unit.CreateInvulnerableEffect");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"CombatSync: Failed to hook CreateInvulnerableEffect: {ex.Message}");
            }
        }

        private void DisposeHooks()
        {
            _takeHitHook?.Dispose();
            _takeHitHook = null;
            _setHitEffectHook?.Dispose();
            _setHitEffectHook = null;
            _drawBulletHoleHook?.Dispose();
            _drawBulletHoleHook = null;
            _createInvulnEffectHook?.Dispose();
            _createInvulnEffectHook = null;
            _hookAttempted = false;
        }

        #endregion

        #region Effect Capture Hooks

        private static void ClearCapturedEffects()
        {
            _capturedHitState = 0;
            _capturedBulletHole = false;
            _capturedBulletHolePos = Vector3.zero;
            _capturedBulletHoleDir = Vector3.zero;
            _capturedCaliberByte = 0;
            _capturedInvulnerable = false;
            _capturedInvulnerablePos = Vector3.zero;
        }

        private static void SetHitEffectInterceptor(orig_SetHitEffect orig, object self, int hitState)
        {
            orig(self, hitState);
            if (_isCapturing && hitState > 0)
                _capturedHitState = (byte)hitState;
        }

        private static void DrawBulletHoleInterceptor(
            orig_DrawBulletHole orig, object self, Vector3 pos, Quaternion rot, object bloodEffect, byte caliber)
        {
            orig(self, pos, rot, bloodEffect, caliber);
            if (_isCapturing)
            {
                _capturedBulletHole = true;
                _capturedBulletHolePos = pos;
                _capturedBulletHoleDir = rot * Vector3.forward;
                _capturedCaliberByte = caliber;
            }
        }

        private static void CreateInvulnEffectInterceptor(
            orig_CreateInvulnEffect orig, object self, Vector3 collisionPoint)
        {
            orig(self, collisionPoint);
            if (_isCapturing)
            {
                _capturedInvulnerable = true;
                _capturedInvulnerablePos = collisionPoint;
            }
        }

        #endregion

        #region TakeHit Hook

        private static void TakeHitInterceptor(
            orig_TakeHit orig, object self, float damage,
            object damageType, object source, Vector3 collisionPoint)
        {
            var instance = Instance;
            var net = NetworkManager.Instance;

            // Not in multiplayer — pass through
            if (instance == null || net == null || !net.IsConnected)
            {
                orig(self, damage, damageType, source, collisionPoint);
                return;
            }

            // Get the owner (Unit) from the Hitbox
            var owner = GetHitboxOwner(self);

            // Check if this is a remote player capsule hit (has RemotePlayerMarker)
            // Now that remote players have Unit components, Owner is non-null,
            // so we must check for the marker before the NPC path
            if (owner != null && self is Component hitboxComp2 && hitboxComp2 != null)
            {
                var marker = hitboxComp2.GetComponentInParent<RemotePlayerMarker>();
                if (marker != null)
                {
                    // Skip friendly fire — host player's weapons should not damage other players
                    if (IsSourcePlayer(source))
                        return;
                    // Remote player hit by NPC — extract damage, send to owning client
                    if (net.IsHost)
                        instance.TryHandleRemotePlayerHit(self, damage, damageType, collisionPoint);
                    // Never call orig — Unit has no Stats, RecieveDamage would crash
                    return;
                }
            }

            if (owner == null)
            {
                if (IsSourcePlayer(source))
                    return;
                // Fallback: Owner is null
                if (net.IsHost)
                    instance.TryHandleRemotePlayerHit(self, damage, damageType, collisionPoint);
                return;
            }

            // Only intercept NPC damage
            if (!IsNpc(owner))
            {
                // On client: suppress NPC-sourced damage on local player.
                // Host handles NPC→player damage via remote player capsule hit → PlayerDamageMessage.
                // Without this, client-fired NPC projectiles cause double damage.
                if (!net.IsHost && IsNpcSource(source))
                    return;
                orig(self, damage, damageType, source, collisionPoint);
                return;
            }

            // Look up entity in registry
            var go = GetGameObject(owner);
            if (go == null)
            {
                orig(self, damage, damageType, source, collisionPoint);
                return;
            }

            var registry = EntitySyncManager.Instance?.Registry;
            if (registry == null || !registry.TryGetId(go, out var entityId) || !entityId.IsValid)
            {
                if (!net.IsHost)
                {
                    // Client: silently block damage to unregistered NPCs.
                    // The NPC will be registered within ~100ms after the ClientNpcSpawnNotify
                    // round-trip. The NPC is frozen during this window anyway.
                    return;
                }

                Plugin.Log.LogWarning($"CombatSync: NPC not in registry (go={go.name}), passing through");
                orig(self, damage, damageType, source, collisionPoint);
                return;
            }

            Plugin.Log.LogInfo($"CombatSync: TakeHit intercepted entity={entityId} damage={damage} isHost={net.IsHost}");

            if (net.IsHost)
            {
                instance.HostHandleTakeHit(
                    orig, self, owner, damage, damageType, source, collisionPoint, entityId);
            }
            else
            {
                instance.ClientHandleTakeHit(damage, damageType, collisionPoint, entityId);
            }
        }

        /// <summary>
        /// Host: let TakeHit proceed normally, then broadcast damage result to clients.
        /// </summary>
        private void HostHandleTakeHit(
            orig_TakeHit orig, object hitbox, object owner, float damage,
            object damageType, object source, Vector3 collisionPoint,
            NetworkEntityId entityId)
        {
            var net = NetworkManager.Instance;
            byte damageTypeId = GetDamageTypeId(damageType);
            bool sourceIsPlayer = IsSourcePlayer(source);

            float healthBefore = GetCurrentHealth(owner);

            Plugin.Log.LogInfo($"CombatSync [Host]: HostHandleTakeHit entity={entityId} " +
                $"damage={damage} healthBefore={healthBefore} damageTypeId={damageTypeId} sourceIsPlayer={sourceIsPlayer}");

            // Set pending death context for EntitySyncManager.DieInterceptor
            HasPendingDeathContext = true;
            PendingDeathDamageTypeId = damageTypeId;
            PendingDeathKillerIsPlayer = (byte)(sourceIsPlayer ? 1 : 0);

            // Capture effects during native damage processing
            ClearCapturedEffects();
            _isCapturing = true;

            try
            {
                // Let TakeHit proceed normally (applies modifiers, knockback, RecieveDamage, Die)
                orig(hitbox, damage, damageType, source, collisionPoint);
            }
            finally
            {
                HasPendingDeathContext = false;
                _isCapturing = false;
            }

            // If NPC died, DieInterceptor already sent EntityDeathMessage — skip DamageResult
            float healthAfter = GetCurrentHealth(owner);
            Plugin.Log.LogInfo($"CombatSync [Host]: After TakeHit healthAfter={healthAfter}");
            if (healthAfter <= 0f)
                return;

            // Broadcast damage result to all clients (non-lethal hit)
            if (healthAfter < healthBefore)
            {
                var msg = new DamageResultMessage
                {
                    EntityId = entityId.Value,
                    NewHealth = healthAfter,
                    FinalDamage = healthBefore - healthAfter,
                    DamageTypeId = damageTypeId,
                    PosX = collisionPoint.x,
                    PosY = collisionPoint.y,
                    PosZ = collisionPoint.z,
                    HitStateByte = _capturedHitState,
                    CaliberByte = _capturedBulletHole ? _capturedCaliberByte : (byte)0xFF,
                    DirX = _capturedBulletHoleDir.x,
                    DirY = _capturedBulletHoleDir.y,
                    DirZ = _capturedBulletHoleDir.z,
                };
                net.SendToAll(msg);
            }
            else if (_capturedHitState > 0 || _capturedInvulnerable)
            {
                // No damage dealt but effects captured (blocked/invulnerable hit)
                var blocked = new HitBlockedMessage
                {
                    EntityId = entityId.Value,
                    HitStateByte = _capturedHitState,
                    PosX = _capturedInvulnerable ? _capturedInvulnerablePos.x : collisionPoint.x,
                    PosY = _capturedInvulnerable ? _capturedInvulnerablePos.y : collisionPoint.y,
                    PosZ = _capturedInvulnerable ? _capturedInvulnerablePos.z : collisionPoint.z,
                    InvulnerableEffect = (byte)(_capturedInvulnerable ? 1 : 0),
                };
                net.SendToAll(blocked);
            }
        }

        /// <summary>
        /// Client: suppress TakeHit entirely, send damage request to host.
        /// </summary>
        private void ClientHandleTakeHit(
            float damage, object damageType, Vector3 collisionPoint,
            NetworkEntityId entityId)
        {
            byte damageTypeId = GetDamageTypeId(damageType);

            var net = NetworkManager.Instance;
            var hostId = LobbyManager.Instance?.HostSteamId ?? CSteamID.Nil;
            if (hostId == CSteamID.Nil) return;

            var msg = new DamageRequestMessage
            {
                EntityId = entityId.Value,
                Damage = damage,
                DamageTypeId = damageTypeId,
                PosX = collisionPoint.x,
                PosY = collisionPoint.y,
                PosZ = collisionPoint.z,
            };
            net.SendMessage(hostId, msg);

            Plugin.Log.LogInfo($"CombatSync [Client]: Sent DamageRequest entity={entityId} damage={damage}");
        }

        /// <summary>
        /// Host: a projectile/explosion hit a remote player's Hitbox (Owner is null).
        /// Extract damage and send PlayerDamageMessage to the owning client.
        /// </summary>
        private void TryHandleRemotePlayerHit(
            object hitbox, float damage, object damageType, Vector3 collisionPoint)
        {
            // Get the GameObject from the Hitbox component
            if (!(hitbox is Component hitboxComp) || hitboxComp == null)
                return;

            var marker = hitboxComp.GetComponentInParent<RemotePlayerMarker>();
            if (marker == null)
                return;

            var net = NetworkManager.Instance;
            if (net == null) return;

            byte damageTypeId = GetDamageTypeId(damageType);

            var msg = new PlayerDamageMessage
            {
                TargetSteamId = marker.SteamId,
                Damage = damage,
                DamageTypeId = damageTypeId,
                SourceEntityId = 0, // Projectile source — no specific entity
                PosX = collisionPoint.x,
                PosY = collisionPoint.y,
                PosZ = collisionPoint.z,
            };

            // Send to the specific client that owns this remote player
            var targetId = new CSteamID(marker.SteamId);
            net.SendMessage(targetId, msg);

            Plugin.Log.LogInfo($"CombatSync [Host]: Remote player hit! target={marker.SteamId} " +
                $"damage={damage} damageType={damageTypeId}");
        }

        #endregion

        #region Message Handling

        private void OnMessageReceived(CSteamID sender, NetworkMessage msg)
        {
            switch (msg.Type)
            {
                case MessageType.DamageEvent:
                    HandleDamageRequest(sender, (DamageRequestMessage)msg);
                    break;
                case MessageType.HitConfirm:
                    HandleDamageResult((DamageResultMessage)msg);
                    break;
                case MessageType.EntityDeath:
                    HandleEntityDeath((EntityDeathMessage)msg);
                    break;
                case MessageType.EnemyAttack:
                    HandlePlayerDamage((PlayerDamageMessage)msg);
                    break;
                case MessageType.HitBlocked:
                    HandleHitBlocked((HitBlockedMessage)msg);
                    break;
            }
        }

        /// <summary>
        /// Host receives a client's damage request. Apply damage authoritatively and broadcast result.
        /// </summary>
        private void HandleDamageRequest(CSteamID sender, DamageRequestMessage msg)
        {
            var net = NetworkManager.Instance;
            if (net == null || !net.IsHost) return;

            var registry = EntitySyncManager.Instance?.Registry;
            if (registry == null) return;

            var entityId = new NetworkEntityId(msg.EntityId);
            if (!registry.TryGetEntity(entityId, out var go) || go == null)
            {
                Plugin.Log.LogWarning($"CombatSync [Host]: DamageRequest for unknown entity {msg.EntityId}");
                return;
            }

            // Get the Unit component
            var unit = GetUnitComponent(go);
            if (unit == null)
            {
                Plugin.Log.LogWarning($"CombatSync [Host]: No Unit component on entity {msg.EntityId}");
                return;
            }

            // Record health before
            float healthBefore = GetCurrentHealth(unit);
            if (healthBefore <= 0f) return; // Already dead

            Plugin.Log.LogInfo($"CombatSync [Host]: HandleDamageRequest entity={msg.EntityId} " +
                $"damage={msg.Damage} healthBefore={healthBefore} damageTypeId={msg.DamageTypeId}");

            // Resolve DamageType ScriptableObject
            var damageTypeSO = ResolveDamageType(msg.DamageTypeId);
            if (damageTypeSO == null)
            {
                Plugin.Log.LogWarning($"CombatSync [Host]: DamageType {msg.DamageTypeId} resolved to null, " +
                    "falling back to type 7 (Normal)");
                damageTypeSO = ResolveDamageType(7);
            }

            // Create dummy DamageSourceData with target's transform to avoid NRE
            var dummySourceData = CreateDummySourceData(go);

            // Set pending death context
            byte damageTypeId = msg.DamageTypeId;
            HasPendingDeathContext = true;
            PendingDeathDamageTypeId = damageTypeId;
            PendingDeathKillerIsPlayer = 1; // Client is a player

            // Capture effects during native damage processing
            ClearCapturedEffects();
            _isCapturing = true;

            bool applied;
            try
            {
                // Apply damage via DamageSourceData overload (direct reflection, not hooked)
                applied = InvokeRecieveDamage(unit, msg.Damage, damageTypeSO, dummySourceData,
                    new Vector3(msg.PosX, msg.PosY, msg.PosZ));

                // Fallback: if InvokeRecieveDamage failed (crash inside game code),
                // apply damage directly via SetHealthDirectly + Die
                if (!applied && healthBefore > 0f)
                {
                    Plugin.Log.LogWarning($"CombatSync [Host]: InvokeRecieveDamage failed for entity {msg.EntityId}, " +
                        "applying direct health reduction");
                    float newHp = Mathf.Max(0f, healthBefore - msg.Damage);
                    SetHealthDirectly(unit, newHp);
                    if (newHp <= 0f)
                        InvokeDie(unit);
                    applied = true;
                }
            }
            finally
            {
                HasPendingDeathContext = false;
                _isCapturing = false;
            }

            if (!applied) return;

            // If NPC died, DieInterceptor already sent EntityDeathMessage — skip DamageResult
            float healthAfter = GetCurrentHealth(unit);
            Plugin.Log.LogInfo($"CombatSync [Host]: After InvokeRecieveDamage healthAfter={healthAfter}");
            if (healthAfter <= 0f) return;

            // Broadcast result (non-lethal hit)
            if (healthAfter < healthBefore)
            {
                var result = new DamageResultMessage
                {
                    EntityId = msg.EntityId,
                    NewHealth = healthAfter,
                    FinalDamage = healthBefore - healthAfter,
                    DamageTypeId = damageTypeId,
                    PosX = msg.PosX,
                    PosY = msg.PosY,
                    PosZ = msg.PosZ,
                    HitStateByte = _capturedHitState,
                    CaliberByte = _capturedBulletHole ? _capturedCaliberByte : (byte)0xFF,
                    DirX = _capturedBulletHoleDir.x,
                    DirY = _capturedBulletHoleDir.y,
                    DirZ = _capturedBulletHoleDir.z,
                };
                net.SendToAll(result);
            }
            else if (_capturedHitState > 0 || _capturedInvulnerable)
            {
                var blocked = new HitBlockedMessage
                {
                    EntityId = msg.EntityId,
                    HitStateByte = _capturedHitState,
                    PosX = _capturedInvulnerable ? _capturedInvulnerablePos.x : msg.PosX,
                    PosY = _capturedInvulnerable ? _capturedInvulnerablePos.y : msg.PosY,
                    PosZ = _capturedInvulnerable ? _capturedInvulnerablePos.z : msg.PosZ,
                    InvulnerableEffect = (byte)(_capturedInvulnerable ? 1 : 0),
                };
                net.SendToAll(blocked);
            }
        }

        /// <summary>
        /// Client receives authoritative damage result from host. Set NPC health directly
        /// and trigger hurt animation.
        /// </summary>
        private void HandleDamageResult(DamageResultMessage msg)
        {
            var net = NetworkManager.Instance;
            if (net == null || net.IsHost) return; // Clients only

            var registry = EntitySyncManager.Instance?.Registry;
            if (registry == null) return;

            var entityId = new NetworkEntityId(msg.EntityId);
            if (!registry.TryGetEntity(entityId, out var go) || go == null)
                return;

            var unit = GetUnitComponent(go);
            if (unit == null) return;

            SetHealthDirectly(unit, msg.NewHealth);
            TriggerHurtAnimation(unit);
            SpawnBloodEffects(unit, go, msg.DamageTypeId, msg.PosX, msg.PosY, msg.PosZ);

            // Hit effect sync (Phase 12)
            if (msg.HitStateByte > 0)
                InvokeSetHitEffect(unit, msg.HitStateByte);
            if (msg.CaliberByte != 0xFF)
                InvokeDrawBulletHole(unit, new Vector3(msg.PosX, msg.PosY, msg.PosZ),
                    new Vector3(msg.DirX, msg.DirY, msg.DirZ), msg.CaliberByte);
        }

        /// <summary>
        /// Client receives entity death from host. Set health to 0 and invoke Die().
        /// </summary>
        private void HandleEntityDeath(EntityDeathMessage msg)
        {
            var net = NetworkManager.Instance;
            if (net == null || net.IsHost) return; // Clients only

            var registry = EntitySyncManager.Instance?.Registry;
            if (registry == null) return;

            var entityId = new NetworkEntityId(msg.EntityId);
            if (!registry.TryGetEntity(entityId, out var go) || go == null)
            {
                Plugin.Log.LogWarning($"CombatSync [Client]: EntityDeath for unknown entity {msg.EntityId}");
                return;
            }

            var unit = GetUnitComponent(go);
            if (unit == null) return;

            // Set health to 0 first (triggers health bar update)
            SetHealthDirectly(unit, 0f);

            // Invoke Die() for death animation/effects/loot
            InvokeDie(unit);

            Plugin.Log.LogInfo($"CombatSync [Client]: Entity {msg.EntityId} killed " +
                $"(damageType={msg.DamageTypeId}, playerKill={msg.KillerIsPlayer})");
        }

        /// <summary>
        /// Client receives damage from an NPC (via host).
        /// Apply damage to local player using RecieveDamage.
        /// </summary>
        private void HandlePlayerDamage(PlayerDamageMessage msg)
        {
            // Only process if this is meant for us
            var localId = SteamUser.GetSteamID().m_SteamID;
            if (msg.TargetSteamId != localId)
                return;

            // Get local player unit
            var playerUnit = GetLocalPlayerUnit();
            if (playerUnit == null)
                return; // Player destroyed or not loaded — silently ignore

            // Don't apply damage to dead players
            float currentHealth = GetCurrentHealth(playerUnit);
            if (currentHealth <= 0f)
                return;

            // Resolve DamageType ScriptableObject
            var damageTypeSO = ResolveDamageType(msg.DamageTypeId);
            if (damageTypeSO == null)
            {
                Plugin.Log.LogWarning($"CombatSync [Client]: DamageType {msg.DamageTypeId} null, falling back to 7 (Normal)");
                damageTypeSO = ResolveDamageType(7);
            }

            // Create dummy DamageSourceData with player's own transform to avoid NRE
            var playerGo = GetGameObject(playerUnit);
            var dummySourceData = CreateDummySourceData(playerGo);

            var collisionPoint = new Vector3(msg.PosX, msg.PosY, msg.PosZ);

            // Apply damage via RecieveDamage
            bool applied = InvokeRecieveDamage(playerUnit, msg.Damage, damageTypeSO, dummySourceData, collisionPoint);

            if (!applied)
            {
                // Fallback: set health directly
                float currentHp = GetCurrentHealth(playerUnit);
                float newHp = Mathf.Max(0f, currentHp - msg.Damage);
                SetHealthDirectly(playerUnit, newHp);
                Plugin.Log.LogWarning($"CombatSync [Client]: RecieveDamage failed, set health directly: {currentHp} → {newHp}");
            }

            Plugin.Log.LogInfo($"CombatSync [Client]: Took {msg.Damage} damage from NPC " +
                $"(damageType={msg.DamageTypeId}, entity={msg.SourceEntityId})");
        }

        /// <summary>
        /// Client receives a blocked/invulnerable hit notification from host.
        /// Replay visual effects (hit flash, invulnerable spark) without health changes.
        /// </summary>
        private void HandleHitBlocked(HitBlockedMessage msg)
        {
            var net = NetworkManager.Instance;
            if (net == null || net.IsHost) return;

            var registry = EntitySyncManager.Instance?.Registry;
            if (registry == null) return;

            var entityId = new NetworkEntityId(msg.EntityId);
            if (!registry.TryGetEntity(entityId, out var go) || go == null)
                return;

            var unit = GetUnitComponent(go);
            if (unit == null) return;

            if (msg.HitStateByte > 0)
                InvokeSetHitEffect(unit, msg.HitStateByte);
            if (msg.InvulnerableEffect != 0)
                InvokeCreateInvulnerableEffect(unit, new Vector3(msg.PosX, msg.PosY, msg.PosZ));
        }

        private void OnDisconnected(string reason)
        {
            HasPendingDeathContext = false;
            _isCapturing = false;
        }

        #endregion

        #region Reflection

        private static void InitReflection()
        {
            if (_reflectionInit) return;
            _reflectionInit = true;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (_unitType == null)
                    _unitType = asm.GetType("PerfectRandom.Sulfur.Core.Units.Unit");
                if (_npcType == null)
                    _npcType = asm.GetType("PerfectRandom.Sulfur.Core.Units.Npc");
                if (_hitboxType == null)
                    _hitboxType = asm.GetType("PerfectRandom.Sulfur.Core.Units.Hitbox");
                if (_damageTypeType == null)
                    _damageTypeType = asm.GetType("PerfectRandom.Sulfur.Core.Stats.DamageType");
                if (_damageSourceDataType == null)
                    _damageSourceDataType = asm.GetType("PerfectRandom.Sulfur.Core.Units.DamageSourceData");
                if (_entityStatsType == null)
                    _entityStatsType = asm.GetType("PerfectRandom.Sulfur.Core.Stats.EntityStats");
                if (_entityAttributesType == null)
                    _entityAttributesType = asm.GetType("PerfectRandom.Sulfur.Core.Stats.EntityAttributes");
                if (_asyncAssetLoadingType == null)
                    _asyncAssetLoadingType = asm.GetType("PerfectRandom.Sulfur.Core.AsyncAssetLoading");
                if (_iDamagerType == null)
                    _iDamagerType = asm.GetType("PerfectRandom.Sulfur.Core.IDamager");
                if (_unitEventType == null)
                    _unitEventType = asm.GetType("PerfectRandom.Sulfur.Core.Units.UnitEvent");
                if (_bloodEffectDataType == null)
                    _bloodEffectDataType = asm.GetType("PerfectRandom.Sulfur.Core.BloodEffectData");
                if (_hitStateType == null)
                    _hitStateType = asm.GetType("PerfectRandom.Sulfur.Core.Units.HitState");
                if (_caliberTypesType == null)
                    _caliberTypesType = asm.GetType("PerfectRandom.Sulfur.Core.CaliberTypes");

                if (_unitType != null && _npcType != null && _hitboxType != null &&
                    _damageTypeType != null && _damageSourceDataType != null &&
                    _entityStatsType != null && _entityAttributesType != null &&
                    _asyncAssetLoadingType != null && _iDamagerType != null &&
                    _unitEventType != null && _bloodEffectDataType != null &&
                    _hitStateType != null && _caliberTypesType != null)
                    break;
            }

            // Hitbox.TakeHit — IDamager overload (hook target)
            if (_hitboxType != null && _iDamagerType != null)
            {
                var methods = _hitboxType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
                _takeHitMethod = methods.FirstOrDefault(m =>
                    m.Name == "TakeHit" &&
                    m.GetParameters().Length == 4 &&
                    m.GetParameters()[2].ParameterType == _iDamagerType);

                if (_takeHitMethod != null)
                    Plugin.Log.LogInfo($"CombatSync: Found Hitbox.TakeHit(IDamager): {_takeHitMethod}");
                else
                    Plugin.Log.LogWarning("CombatSync: Could not find Hitbox.TakeHit(IDamager)");

                // Hitbox.Owner property
                _hitboxOwnerProp = _hitboxType.GetProperty("Owner",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_hitboxOwnerProp != null)
                    Plugin.Log.LogInfo("CombatSync: Found Hitbox.Owner");
                else
                    Plugin.Log.LogWarning("CombatSync: Could not find Hitbox.Owner");

                // Hitbox.AmountOfBloodPerHit (int)
                _amountOfBloodPerHitField = _hitboxType.GetField("AmountOfBloodPerHit",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_amountOfBloodPerHitField != null)
                    Plugin.Log.LogInfo("CombatSync: Found Hitbox.AmountOfBloodPerHit");
            }

            // Unit.RecieveDamage(DSD) — for InvokeRecieveDamage on host
            if (_unitType != null)
            {
                var methods = _unitType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
                _recieveDamageDsdMethod = methods.FirstOrDefault(m =>
                    m.Name == "RecieveDamage" &&
                    m.GetParameters().Length == 5 &&
                    m.GetParameters()[2].ParameterType.Name == "DamageSourceData");

                if (_recieveDamageDsdMethod != null)
                    Plugin.Log.LogInfo($"CombatSync: Found Unit.RecieveDamage (DamageSourceData): {_recieveDamageDsdMethod}");

                _dieMethod = _unitType.GetMethod("Die",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                _getCurrentHealthMethod = _unitType.GetMethod("GetCurrentHealth",
                    BindingFlags.Public | BindingFlags.Instance);
                _isPlayerField = _unitType.GetField("isPlayer",
                    BindingFlags.Public | BindingFlags.Instance);
                _statsProp = _unitType.GetProperty("Stats",
                    BindingFlags.Public | BindingFlags.Instance);

                // TriggerOneShotEvent(UnitEvent) — for hurt animation on clients
                if (_unitEventType != null)
                {
                    _triggerOneShotEventMethod = _unitType.GetMethod("TriggerOneShotEvent",
                        BindingFlags.Public | BindingFlags.Instance,
                        null, new[] { _unitEventType }, null);
                    if (_triggerOneShotEventMethod != null)
                        Plugin.Log.LogInfo("CombatSync: Found Unit.TriggerOneShotEvent(UnitEvent)");
                }

                // Blood effect methods
                if (_bloodEffectDataType != null)
                {
                    _createBloodProjectilesMethod = _unitType.GetMethod("CreateBloodProjectiles",
                        BindingFlags.Public | BindingFlags.Instance,
                        null, new[] { typeof(int), typeof(Vector3), _bloodEffectDataType }, null);
                    if (_createBloodProjectilesMethod != null)
                        Plugin.Log.LogInfo("CombatSync: Found Unit.CreateBloodProjectiles");
                }

                _currentBloodEffectDataProp = _unitType.GetProperty("currentBloodEffectData",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_currentBloodEffectDataProp != null)
                    Plugin.Log.LogInfo("CombatSync: Found Unit.currentBloodEffectData");
            }

            // UnitEvent.TakeDamage enum value
            if (_unitEventType != null)
            {
                try
                {
                    _unitEventTakeDamage = Enum.Parse(_unitEventType, "TakeDamage");
                    if (_unitEventTakeDamage != null)
                        Plugin.Log.LogInfo("CombatSync: Resolved UnitEvent.TakeDamage");
                }
                catch
                {
                    Plugin.Log.LogWarning("CombatSync: Could not resolve UnitEvent.TakeDamage");
                }
            }

            // IDamager.SourceUnit property
            if (_iDamagerType != null)
            {
                _sourceUnitProp = _iDamagerType.GetProperty("SourceUnit",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_sourceUnitProp != null)
                    Plugin.Log.LogInfo("CombatSync: Found IDamager.SourceUnit");
            }

            // DamageType.id field
            if (_damageTypeType != null)
            {
                _damageTypeIdField = _damageTypeType.GetField("id",
                    BindingFlags.Public | BindingFlags.Instance);

                // DamageType.projectilesOfThisTypeDrawBlood (bool)
                _drawsBloodField = _damageTypeType.GetField("projectilesOfThisTypeDrawBlood",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_drawsBloodField != null)
                    Plugin.Log.LogInfo("CombatSync: Found DamageType.projectilesOfThisTypeDrawBlood");
            }

            // EntityStats.SetStatus(EntityAttributes, float, bool)
            if (_entityStatsType != null && _entityAttributesType != null)
            {
                _setStatusMethod = _entityStatsType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m =>
                        m.Name == "SetStatus" &&
                        m.GetParameters().Length == 3 &&
                        m.GetParameters()[0].ParameterType == _entityAttributesType);

                if (_setStatusMethod != null)
                    Plugin.Log.LogInfo("CombatSync: Found EntityStats.SetStatus");
            }

            // DamageSourceData fields
            if (_damageSourceDataType != null)
            {
                _dsdNameField = _damageSourceDataType.GetField("name",
                    BindingFlags.Public | BindingFlags.Instance);
                _dsdTransformField = _damageSourceDataType.GetField("transform",
                    BindingFlags.Public | BindingFlags.Instance);
            }

            // EntityAttributes.Status_CurrentHealth
            if (_entityAttributesType != null)
            {
                _statusCurrentHealth = Enum.Parse(_entityAttributesType, "Status_CurrentHealth");
                if (_statusCurrentHealth != null)
                    Plugin.Log.LogInfo("CombatSync: Resolved EntityAttributes.Status_CurrentHealth");
            }

            // Hit effect sync methods (Phase 12)
            if (_npcType != null && _hitStateType != null)
            {
                _setHitEffectMethod = _npcType.GetMethod("SetHitEffect",
                    BindingFlags.Public | BindingFlags.Instance, null, new[] { _hitStateType }, null);
                if (_setHitEffectMethod != null)
                    Plugin.Log.LogInfo("CombatSync: Found Npc.SetHitEffect(HitState)");
            }

            if (_npcType != null)
            {
                _drawBulletHoleMethod = _npcType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "DrawBulletHole" && m.GetParameters().Length == 4);
                if (_drawBulletHoleMethod != null)
                    Plugin.Log.LogInfo("CombatSync: Found Npc.DrawBulletHole");
            }

            if (_unitType != null)
            {
                _createInvulnEffectMethod = _unitType.GetMethod("CreateInvulnerableEffect",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (_createInvulnEffectMethod != null)
                    Plugin.Log.LogInfo("CombatSync: Found Unit.CreateInvulnerableEffect");

                _bulletHoleEffectField = _unitType.GetField("bulletHoleEffect",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_bulletHoleEffectField != null)
                    Plugin.Log.LogInfo("CombatSync: Found Unit.bulletHoleEffect");
            }

            // AsyncAssetLoading for DamageType ScriptableObject resolution
            if (_asyncAssetLoadingType != null)
            {
                _assetLoadingInstanceProp = _asyncAssetLoadingType.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                _assetSetsProp = _asyncAssetLoadingType.GetProperty("assetSets",
                    BindingFlags.Public | BindingFlags.Instance);
            }

            // AssetSets.damageTypes
            if (_assetSetsProp != null)
            {
                var assetSetsType = _assetSetsProp.PropertyType;
                _damageTypesArrayField = assetSetsType.GetField("damageTypes",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_damageTypesArrayField != null)
                    Plugin.Log.LogInfo("CombatSync: Found AssetSets.damageTypes array");
            }
        }

        #endregion

        #region Helpers

        private static object GetHitboxOwner(object hitbox)
        {
            if (hitbox == null || _hitboxOwnerProp == null) return null;
            try { return _hitboxOwnerProp.GetValue(hitbox); }
            catch { return null; }
        }

        private static bool IsNpc(object unit)
        {
            if (unit == null || _npcType == null) return false;
            return _npcType.IsInstanceOfType(unit);
        }

        private static GameObject GetGameObject(object unit)
        {
            if (unit is Component comp && comp != null) return comp.gameObject;
            if (unit is GameObject go && go != null) return go;
            return null;
        }

        private static object GetUnitComponent(GameObject go)
        {
            if (go == null || _unitType == null) return null;
            return go.GetComponent(_unitType);
        }

        private static float GetCurrentHealth(object unit)
        {
            if (unit == null || _getCurrentHealthMethod == null) return 0f;
            try { return (float)_getCurrentHealthMethod.Invoke(unit, null); }
            catch { return 0f; }
        }

        private static byte GetDamageTypeId(object damageType)
        {
            if (damageType == null || _damageTypeIdField == null) return 7; // Normal fallback
            try
            {
                var enumVal = _damageTypeIdField.GetValue(damageType);
                return (byte)(int)enumVal; // boxed enum → int → byte
            }
            catch { return 7; }
        }

        private static bool IsSourcePlayer(object source)
        {
            // source is an IDamager — check IDamager.SourceUnit.isPlayer
            if (source == null || _sourceUnitProp == null || _isPlayerField == null)
                return false;

            try
            {
                var sourceUnit = _sourceUnitProp.GetValue(source);
                if (sourceUnit == null || (sourceUnit is UnityEngine.Object uObj && uObj == null))
                    return false;

                return (bool)_isPlayerField.GetValue(sourceUnit);
            }
            catch { return false; }
        }

        private static bool IsNpcSource(object source)
        {
            if (source == null || _sourceUnitProp == null || _npcType == null)
                return false;
            try
            {
                var sourceUnit = _sourceUnitProp.GetValue(source);
                if (sourceUnit == null || (sourceUnit is UnityEngine.Object uObj && uObj == null))
                    return false;
                return _npcType.IsInstanceOfType(sourceUnit);
            }
            catch { return false; }
        }

        // GameManager reflection for local player access
        private static Type _gameManagerType;
        private static PropertyInfo _gmInstanceProp;
        private static PropertyInfo _playerUnitProp;
        private static bool _gmReflectionInit;

        /// <summary>
        /// Get the local player's Unit component via GameManager reflection.
        /// </summary>
        private static object GetLocalPlayerUnit()
        {
            if (!_gmReflectionInit)
            {
                _gmReflectionInit = true;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (_gameManagerType == null)
                        _gameManagerType = asm.GetType("PerfectRandom.Sulfur.Core.GameManager");
                    if (_gameManagerType != null) break;
                }

                if (_gameManagerType != null)
                {
                    _gmInstanceProp = _gameManagerType.GetProperty("Instance",
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                    _playerUnitProp = _gameManagerType.GetProperty("PlayerUnit",
                        BindingFlags.Public | BindingFlags.Instance);
                }
            }

            if (_gmInstanceProp == null || _playerUnitProp == null)
                return null;

            try
            {
                var gm = _gmInstanceProp.GetValue(null);
                if (gm == null || (gm is UnityEngine.Object uObj && uObj == null))
                    return null;

                var unit = _playerUnitProp.GetValue(gm);
                if (unit == null || (unit is UnityEngine.Object uUnit && uUnit == null))
                    return null;

                return unit;
            }
            catch { return null; }
        }

        private static object ResolveDamageType(byte damageTypeId)
        {
            if (_assetLoadingInstanceProp == null || _assetSetsProp == null || _damageTypesArrayField == null)
                return null;

            try
            {
                var loader = _assetLoadingInstanceProp.GetValue(null);
                if (loader == null || (loader is UnityEngine.Object uObj && uObj == null))
                    return null;

                var assetSets = _assetSetsProp.GetValue(loader);
                if (assetSets == null) return null;

                var array = _damageTypesArrayField.GetValue(assetSets) as Array;
                if (array == null || damageTypeId >= array.Length) return null;

                return array.GetValue(damageTypeId);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"CombatSync: Failed to resolve DamageType {damageTypeId}: {ex.Message}");
                return null;
            }
        }

        private static object CreateDummySourceData(GameObject targetGo)
        {
            if (_damageSourceDataType == null) return null;

            try
            {
                var dummy = Activator.CreateInstance(_damageSourceDataType);
                if (_dsdNameField != null)
                    _dsdNameField.SetValue(dummy, "RemotePlayer");
                if (_dsdTransformField != null && targetGo != null)
                    _dsdTransformField.SetValue(dummy, targetGo.transform);
                return dummy;
            }
            catch { return null; }
        }

        private static void SetHealthDirectly(object unit, float health)
        {
            if (unit == null || _statsProp == null || _setStatusMethod == null || _statusCurrentHealth == null)
                return;

            try
            {
                var stats = _statsProp.GetValue(unit);
                if (stats == null) return;

                _setStatusMethod.Invoke(stats, new object[] { _statusCurrentHealth, health, false });
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"CombatSync: SetHealthDirectly failed: {ex.Message}");
            }
        }

        private static bool InvokeRecieveDamage(object unit, float damage, object damageType,
            object sourceData, Vector3 collisionPoint)
        {
            if (unit == null || _recieveDamageDsdMethod == null) return false;

            try
            {
                var result = _recieveDamageDsdMethod.Invoke(unit,
                    new object[] { damage, damageType, sourceData, null, collisionPoint });
                return result is bool b && b;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"CombatSync: InvokeRecieveDamage failed: {ex.Message}");
                return false;
            }
        }

        private static void InvokeDie(object unit)
        {
            if (unit == null || _dieMethod == null) return;

            try
            {
                _dieMethod.Invoke(unit, null);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"CombatSync: InvokeDie failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Spawn blood particles at the collision point on a client receiving a DamageResult.
        /// Calls the Unit's native CreateBloodProjectiles method with appropriate config.
        /// </summary>
        private static void SpawnBloodEffects(object unit, GameObject go, byte damageTypeId,
            float posX, float posY, float posZ)
        {
            if (unit == null || _createBloodProjectilesMethod == null || _currentBloodEffectDataProp == null)
                return;

            try
            {
                // Check if this damage type draws blood
                var damageType = ResolveDamageType(damageTypeId);
                if (damageType != null && _drawsBloodField != null)
                {
                    bool drawsBlood = (bool)_drawsBloodField.GetValue(damageType);
                    if (!drawsBlood) return;
                }

                // Get blood config from unit
                var bloodData = _currentBloodEffectDataProp.GetValue(unit);
                if (bloodData == null) return;

                // Get amount from first hitbox (or default 3)
                int amount = 3;
                if (go != null && _amountOfBloodPerHitField != null)
                {
                    var hitbox = go.GetComponentInChildren(_hitboxType);
                    if (hitbox != null)
                        amount = (int)_amountOfBloodPerHitField.GetValue(hitbox);
                }

                var collisionPoint = new Vector3(posX, posY, posZ);
                _createBloodProjectilesMethod.Invoke(unit, new object[] { amount, collisionPoint, bloodData });
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"CombatSync: SpawnBloodEffects failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Trigger hurt/flinch animation on a Unit via TriggerOneShotEvent(UnitEvent.TakeDamage).
        /// Includes animator trigger + sound effects.
        /// </summary>
        private static void TriggerHurtAnimation(object unit)
        {
            if (unit == null || _triggerOneShotEventMethod == null || _unitEventTakeDamage == null)
                return;

            try
            {
                _triggerOneShotEventMethod.Invoke(unit, new[] { _unitEventTakeDamage });
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"CombatSync: TriggerHurtAnimation failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Invoke Npc.SetHitEffect(HitState) on client for hit flash (shader _HitTime/_HitType).
        /// Auto-decays via Npc.Update() hitStateOffTimer.
        /// </summary>
        private static void InvokeSetHitEffect(object unit, byte hitStateByte)
        {
            if (unit == null || _setHitEffectMethod == null || _hitStateType == null)
                return;

            try
            {
                var hitState = Enum.ToObject(_hitStateType, (int)hitStateByte);
                _setHitEffectMethod.Invoke(unit, new[] { hitState });
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"CombatSync: InvokeSetHitEffect failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Invoke Npc.DrawBulletHole on client for decal rendering.
        /// Reads bulletHoleEffect from the client's own NPC (same prefab → same SO).
        /// </summary>
        private static void InvokeDrawBulletHole(object unit, Vector3 pos, Vector3 dir, byte caliberByte)
        {
            if (unit == null || _drawBulletHoleMethod == null ||
                _bulletHoleEffectField == null || _caliberTypesType == null)
                return;

            try
            {
                var bloodEffect = _bulletHoleEffectField.GetValue(unit);
                if (bloodEffect == null || (bloodEffect is UnityEngine.Object uObj && uObj == null))
                    return;

                var rotation = dir.sqrMagnitude > 0.001f
                    ? Quaternion.LookRotation(dir)
                    : Quaternion.identity;
                var caliber = Enum.ToObject(_caliberTypesType, caliberByte);
                _drawBulletHoleMethod.Invoke(unit, new object[] { pos, rotation, bloodEffect, caliber });
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"CombatSync: InvokeDrawBulletHole failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Invoke Unit.CreateInvulnerableEffect on client for invulnerable spark particles.
        /// </summary>
        private static void InvokeCreateInvulnerableEffect(object unit, Vector3 pos)
        {
            if (unit == null || _createInvulnEffectMethod == null)
                return;

            try
            {
                _createInvulnEffectMethod.Invoke(unit, new object[] { pos });
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"CombatSync: InvokeCreateInvulnerableEffect failed: {ex.Message}");
            }
        }

        #endregion
    }
}
