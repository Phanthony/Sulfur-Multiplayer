using System;
using System.Reflection;
using Steamworks;
using SulfurMP.Players;
using UnityEngine;

namespace SulfurMP.Entities
{
    /// <summary>
    /// Lightweight per-NPC component that smoothly interpolates between
    /// position/rotation snapshots received from the host.
    ///
    /// Animation sync uses two cooperating mechanisms:
    /// 1. Generic Bool parameters (bitmask) — all Bool-type animator params are
    ///    synced via a ushort bitmask, keeping the state machine consistent.
    ///    Trigger-type params are excluded at discovery time.
    /// 2. CrossFade on state change — handles trigger-based transitions (melee
    ///    attacks, one-shot events) that bool sync alone can't drive.
    ///
    /// AttackID integer is set before CrossFade for melee variant selection.
    /// Fire events use a separate trigger state machine (native weapon path).
    ///
    /// Animator params are set in Update (before animator evaluates).
    /// Position/rotation lerp runs in LateUpdate (after animator/physics).
    /// </summary>
    [DefaultExecutionOrder(100)]  // Run after Npc.Update (default 0) so host-synced params win
    public class NpcMotionSmoother : MonoBehaviour
    {
        private Vector3 _targetPos;
        private float _targetRotY;
        private bool _initialized;

        // Animator state
        private Animator _animator;
        private int _animStateHash;
        private int _lastAppliedStateHash;

        // Attack variant — set before CrossFade for melee variant selection
        private byte _attackVariantId;

        // Generic Bool parameter sync — replaces per-param Moving/Attack/Cowering fields.
        // Both host and client use the same prefab → same AnimatorController, so
        // animator.parameters returns params in identical deterministic order.
        // Array index = bit position in the ushort bitmask.
        private string[] _boolParamNames;
        private ushort _boolFlags;

        // Fire event sync — host sends monotonic fire counter, client calls Shoot() on delta
        private byte _fireCount;
        private byte _lastFireCount;
        private bool _fireCountInitialized;

        // Weapon trigger reflection (static, initialized once)
        private static bool _weaponReflectInit;
        private static Type _npcReflectType;
        private static FieldInfo _npcWeaponField;        // Npc.weapon (public Weapon field)
        private static FieldInfo _triggerActiveField;     // Holdable.bIsTriggerActive (public bool)
        private static PropertyInfo _wasTriggerActiveProp; // Weapon.WasTriggerActive (public get/set)

        // Per-instance weapon cache
        private MonoBehaviour _weapon;

        // Two-frame trigger state machine for fire sync
        private int _pendingShots;
        private bool _triggerHeld;

        // Aim target sync — host sends SteamID of the player this NPC is aiming at
        private ulong _targetSteamId;
        private Vector3 _targetAimPosition;
        private bool _hasTargetAim;

        /// <summary>True when a valid aim target position has been resolved from the host-synced SteamID.</summary>
        public bool HasTargetAim => _hasTargetAim;
        /// <summary>World position the NPC should aim at (player head height).</summary>
        public Vector3 TargetAimPosition => _targetAimPosition;

        // GameManager reflection for resolving local player position (static, init once)
        private static bool _gmReflectInit;
        private static Type _gmType;
        private static PropertyInfo _gmInstanceProp;
        private static PropertyInfo _gmPlayerObjectProp;  // GameManager.PlayerObject (returns GameObject)

        private const float LerpSpeed = 12f;
        private const float RotLerpSpeed = 12f;
        private const float CrossFadeDuration = 0.15f;

        private void Awake()
        {
            _animator = GetComponent<Animator>();
            // Disable root motion so animator doesn't fight our position updates
            if (_animator != null)
            {
                _animator.applyRootMotion = false;

                // Discover all Bool-type animator parameters for generic bitmask sync.
                // Trigger-type params (e.g. "Attack" on melee NPCs) are excluded by
                // the type check — they're driven by CrossFade/SetTrigger instead.
                var allParams = _animator.parameters;
                int boolCount = 0;
                for (int i = 0; i < allParams.Length; i++)
                    if (allParams[i].type == AnimatorControllerParameterType.Bool)
                        boolCount++;
                if (boolCount > 0)
                {
                    if (boolCount > 16) boolCount = 16;
                    _boolParamNames = new string[boolCount];
                    int idx = 0;
                    for (int i = 0; i < allParams.Length && idx < boolCount; i++)
                        if (allParams[i].type == AnimatorControllerParameterType.Bool)
                            _boolParamNames[idx++] = allParams[i].name;
                }
            }

            // Cache weapon reference for trigger management
            InitWeaponReflection();
            if (_npcReflectType != null && _npcWeaponField != null)
            {
                var npc = GetComponent(_npcReflectType);
                if (npc != null)
                {
                    var w = _npcWeaponField.GetValue(npc);
                    if (w is MonoBehaviour mb && mb != null)
                        _weapon = mb;
                }
            }
        }

        /// <summary>
        /// Called when a new snapshot arrives from the host.
        /// First call snaps position to avoid lerping from origin.
        /// </summary>
        public void SetTarget(Vector3 position, float rotY, ushort boolFlags, int animStateHash, ulong targetSteamId = 0, byte fireCount = 0, byte attackVariantId = 0, byte animChangeCount = 0)
        {
            _targetPos = position;
            _targetRotY = rotY;
            _boolFlags = boolFlags;
            _animStateHash = animStateHash;
            _targetSteamId = targetSteamId;
            _fireCount = fireCount;
            _attackVariantId = attackVariantId;
            if (!_fireCountInitialized)
            {
                _fireCountInitialized = true;
                _lastFireCount = fireCount; // baseline — don't fire on first snapshot
            }

            if (!_initialized)
            {
                _initialized = true;
                transform.position = position;
                transform.rotation = Quaternion.Euler(0f, rotY, 0f);
            }
        }

        /// <summary>
        /// Sets animator parameters BEFORE the animator evaluates this frame.
        /// Unity execution order: Update → Animator evaluation → LateUpdate.
        /// Runs after Npc.Update() via [DefaultExecutionOrder(100)] so host-synced
        /// values (Moving, Attack) override the NPC's local state.
        /// </summary>
        private void Update()
        {
            ResolveAimTarget();

            if (!_initialized || _animator == null) return;

            // Generic Bool parameter sync — sets all Bool-type params from bitmask.
            // Covers Moving, Cowering, Attack (on ranged NPCs), and any future bools.
            // Trigger-type params are excluded at discovery time (Awake).
            if (_boolParamNames != null)
                for (int i = 0; i < _boolParamNames.Length; i++)
                    _animator.SetBool(_boolParamNames[i], (_boolFlags >> i & 1) != 0);

            // Lazy weapon cache — weapon may need a frame after AttachWeapon to be set
            if (_weapon == null && _npcReflectType != null && _npcWeaponField != null)
            {
                var npc = GetComponent(_npcReflectType);
                if (npc != null)
                {
                    var w = _npcWeaponField.GetValue(npc);
                    if (w is MonoBehaviour mb && mb != null)
                        _weapon = mb;
                }
            }

            // Fire event sync — detect fire counter delta and drive animation + weapon.
            // Host sends a monotonic byte counter; client detects delta, sets the
            // animator "Fire" trigger directly, and queues weapon trigger cycles.
            if (_fireCountInitialized)
            {
                int delta = (_fireCount - _lastFireCount) & 0xFF;
                if (delta > 0 && delta <= 10)
                {
                    _pendingShots += delta;
                    // Drive per-shot fire animation directly via animator trigger.
                    // The native weapon path (HandleShootingUpdate → Shoot →
                    // PlayWeaponShootAnimation) may not fire on client due to
                    // cooldown/ammo state mismatch.
                    if (_animator != null)
                        _animator.SetTrigger("Fire");
                }
                _lastFireCount = _fireCount;

                // Two-frame weapon trigger state machine (only if weapon available).
                // Drives projectile spawning, muzzle flash, and sound via native path.
                if (_weapon != null)
                {
                    if (_pendingShots > 0 && !_triggerHeld)
                    {
                        // Phase 1: trigger ON
                        try { _triggerActiveField.SetValue(_weapon, true); } catch { }
                        if (_wasTriggerActiveProp != null)
                            try { _wasTriggerActiveProp.SetValue(_weapon, false); } catch { }
                        _triggerHeld = true;
                    }
                    else if (_triggerHeld)
                    {
                        // Phase 2: trigger OFF — completes one fire cycle
                        try { _triggerActiveField.SetValue(_weapon, false); } catch { }
                        _triggerHeld = false;
                        _pendingShots--;
                    }
                    else
                    {
                        // No pending shots — keep trigger off
                        try { _triggerActiveField.SetValue(_weapon, false); } catch { }
                    }
                }
            }

            // State-hash-driven animation: CrossFade to the host's exact animator state.
            // Captures state-level transitions (Idle → Walk, Walk → Attack, etc.).
            // Per-shot fire animations within a state are handled by SetTrigger above.
            if (_animStateHash != 0 && _animStateHash != _lastAppliedStateHash)
            {
                _lastAppliedStateHash = _animStateHash;
                try { _animator.SetInteger("AttackID", _attackVariantId); } catch { }
                _animator.CrossFadeInFixedTime(_animStateHash, CrossFadeDuration, 0);
            }
        }

        private static void InitWeaponReflection()
        {
            if (_weaponReflectInit) return;
            _weaponReflectInit = true;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (_npcReflectType == null)
                    _npcReflectType = asm.GetType("PerfectRandom.Sulfur.Core.Units.Npc");
                if (_npcReflectType != null) break;
            }

            if (_npcReflectType != null)
                _npcWeaponField = _npcReflectType.GetField("weapon", BindingFlags.Public | BindingFlags.Instance);

            if (_npcWeaponField != null)
            {
                var weaponType = _npcWeaponField.FieldType;
                // bIsTriggerActive is on Holdable (Weapon's base class)
                var holdableType = weaponType.BaseType;
                if (holdableType != null)
                    _triggerActiveField = holdableType.GetField("bIsTriggerActive", BindingFlags.Public | BindingFlags.Instance);
                // Fallback: check weapon type directly
                if (_triggerActiveField == null)
                    _triggerActiveField = weaponType.GetField("bIsTriggerActive", BindingFlags.Public | BindingFlags.Instance);

                // Weapon.WasTriggerActive — public get/set property wrapping protected bWasTriggerActive.
                // Used to simulate the rising-edge detection in HandleShootingUpdate.
                _wasTriggerActiveProp = weaponType.GetProperty("WasTriggerActive",
                    BindingFlags.Public | BindingFlags.Instance);
            }
        }

        /// <summary>
        /// Smoothly interpolates position and rotation toward the target.
        /// Runs in LateUpdate to override any residual position changes from
        /// the Animator (root motion) or other systems.
        /// </summary>
        private void LateUpdate()
        {
            if (!_initialized) return;

            float dt = Time.deltaTime;

            // Smooth position
            transform.position = Vector3.Lerp(transform.position, _targetPos, dt * LerpSpeed);

            // Smooth rotation
            var targetRot = Quaternion.Euler(0f, _targetRotY, 0f);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, dt * RotLerpSpeed);
        }

        /// <summary>
        /// Resolve the host-synced TargetSteamId to a world position (player head height).
        /// Called every Update so the aim position tracks the player's movement.
        /// </summary>
        private void ResolveAimTarget()
        {
            if (_targetSteamId == 0)
            {
                _hasTargetAim = false;
                return;
            }

            // Check if target is the local player
            ulong localSteamId = SteamUser.GetSteamID().m_SteamID;
            if (_targetSteamId == localSteamId)
            {
                var playerObj = GetLocalPlayerObject();
                if (playerObj != null)
                {
                    _targetAimPosition = playerObj.transform.position + new Vector3(0f, 1.6f, 0f);
                    _hasTargetAim = true;
                    return;
                }
                _hasTargetAim = false;
                return;
            }

            // Check if target is a remote player
            var replicationMgr = PlayerReplicationManager.Instance;
            if (replicationMgr != null)
            {
                var remotePlayers = replicationMgr.RemotePlayers;
                if (remotePlayers.TryGetValue(_targetSteamId, out var remotePlayer) && remotePlayer != null)
                {
                    _targetAimPosition = remotePlayer.transform.position + new Vector3(0f, 1.6f, 0f);
                    _hasTargetAim = true;
                    return;
                }
            }

            _hasTargetAim = false;
        }

        /// <summary>
        /// Get the local player's GameObject via GameManager.PlayerObject.
        /// Returns null if GameManager or PlayerObject is not available (e.g. player dead).
        /// </summary>
        private static GameObject GetLocalPlayerObject()
        {
            InitGMReflection();
            if (_gmInstanceProp == null || _gmPlayerObjectProp == null)
                return null;

            try
            {
                var gm = _gmInstanceProp.GetValue(null);
                if (gm == null || (gm is UnityEngine.Object uGm && uGm == null))
                    return null;

                var playerObj = _gmPlayerObjectProp.GetValue(gm);
                if (playerObj is GameObject go && go != null)
                    return go;
            }
            catch { }

            return null;
        }

        private static void InitGMReflection()
        {
            if (_gmReflectInit) return;
            _gmReflectInit = true;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                _gmType = asm.GetType("PerfectRandom.Sulfur.Core.GameManager");
                if (_gmType != null) break;
            }

            if (_gmType != null)
            {
                _gmInstanceProp = _gmType.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                _gmPlayerObjectProp = _gmType.GetProperty("PlayerObject",
                    BindingFlags.Public | BindingFlags.Instance);
            }
        }
    }
}
