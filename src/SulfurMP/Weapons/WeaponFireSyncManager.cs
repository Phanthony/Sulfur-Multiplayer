using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using MonoMod.RuntimeDetour;
using SulfurMP.Networking;
using SulfurMP.Networking.Messages;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SulfurMP.Weapons
{
    /// <summary>
    /// Synchronizes weapon fire effects (sound + tracers) between players.
    /// Hooks Weapon.Shoot() to capture fire events, uses native Sonity SoundEvent
    /// for audio and native AutoPool + ProjectileSystem for cosmetic tracers.
    /// </summary>
    public class WeaponFireSyncManager : MonoBehaviour
    {
        public static WeaponFireSyncManager Instance { get; private set; }

        // Hook state
        private bool _hookAttempted;
        private int _hookRetryCount;
        private const int MaxHookRetries = 60;
        private Hook _shootHook;

        // Sound cache: ItemId → SoundEvent or AudioClip
        private readonly Dictionary<ushort, object> _weaponSoundCache = new Dictionary<ushort, object>();

        // Temporary transform for SoundEvent.Play(Transform) — positioned at fire location
        private Transform _audioTransform;

        // Reflection cache — initialized once
        private static bool _reflectionInit;

        // Weapon + Holdable types
        private static Type _weaponType;
        private static Type _holdableType;
        private static MethodInfo _shootMethod;
        private static FieldInfo _bOwnerIsNpcField;        // Holdable.bOwnerIsNpc (protected bool)
        private static PropertyInfo _isMeleeProperty;      // Weapon.IsMelee (public bool)
        private static PropertyInfo _barrelTransformProp;   // Weapon.BarrelTransform (public Transform)
        private static PropertyInfo _itemDefinitionProp;    // Holdable.ItemDefinition (public)

        // ItemDefinition → ItemId → value chain
        private static Type _itemDefinitionType;
        private static FieldInfo _itemDefIdField;           // ItemDefinition.id (field, ItemId)
        private static Type _itemIdType;
        private static FieldInfo _itemIdValueField;         // ItemId.value (field, ushort)

        // Audio extraction
        private static FieldInfo _fireClipListField;        // Weapon.fireClipList (private)
        private static Type _audioClipListType;
        private static FieldInfo _aclSoundEventField;       // AudioClipList.soundEvent (public)
        private static FieldInfo _aclAudioClipsField;       // AudioClipList.audioClips (public)
        private static FieldInfo _fireClipsField;           // Weapon.fireClips (private, legacy)

        // Sound playback
        private static Type _soundEventType;
        private static MethodInfo _soundEventPlayMethod;    // SoundEvent.Play(Transform)

        // Tracer: AutoPool
        private static Type _autoPoolType;
        private static PropertyInfo _autoPoolInstance;       // StaticInstance<AutoPool>.Instance
        private static MethodInfo _getProjectileMethod;     // AutoPool.GetProjectile(ProjectileTypes, out Projectile)

        // Tracer: ProjectileSystem
        private static Type _projSystemType;
        private static PropertyInfo _projSystemInstance;     // StaticInstance<ProjectileSystem>.Instance
        private static MethodInfo _startProjectileMethod;   // ProjectileSystem.StartProjectile(Projectile, Vector3, bool)

        // Tracer: Projectile
        private static Type _projectileType;
        private static MethodInfo _turnOffDamageMethod;     // Projectile.TurnOffDamage()
        private static MethodInfo _setOwnerMethod;          // Projectile.SetOwner(GameObject)
        private static FieldInfo _useGravityField;          // Projectile.useGravity (bool)
        private static FieldInfo _trailsField;              // Projectile.trails (TrailRenderer[])
        private static FieldInfo _lifeTimeField;            // Projectile.lifeTime (float)
        private static MethodInfo _restartLifetimeMethod;   // Projectile.RestartLifetime()
        private static MethodInfo _resetEffectsMethod;      // Projectile.ResetEffects()
        private static FieldInfo _explicitDamageField;       // Projectile.explicitDamage (float) — ProcessHit checks this raw field
        private static FieldInfo _damageComponentsField;     // Projectile.damageComponents (protected list) — stale from pool reuse

        // ProjectileTypes enum
        private static Type _projectileTypesEnum;
        private static object _bulletEnumValue;             // ProjectileTypes.Bullet (boxed)

        // MonoMod delegate
        private delegate void orig_Shoot(object self);
        private delegate void hook_Shoot(orig_Shoot orig, object self);

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
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDisable()
        {
            NetworkEvents.OnMessageReceived -= OnMessageReceived;
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnDestroy()
        {
            _shootHook?.Dispose();
            _shootHook = null;
            if (_audioTransform != null)
                Destroy(_audioTransform.gameObject);
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

            if (_shootMethod == null)
            {
                _hookRetryCount++;
                if (_hookRetryCount >= MaxHookRetries)
                {
                    _hookAttempted = true;
                    Plugin.Log.LogWarning("WeaponFireSync: Could not find Weapon.Shoot after max retries");
                }
                return;
            }

            _hookAttempted = true;

            try
            {
                _shootHook = new Hook(
                    _shootMethod,
                    new hook_Shoot(ShootInterceptor));
                Plugin.Log.LogInfo("WeaponFireSync: Hooked Weapon.Shoot");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"WeaponFireSync: Failed to hook Weapon.Shoot: {ex}");
            }
        }

        #endregion

        #region Shoot Hook

        private static void ShootInterceptor(orig_Shoot orig, object self)
        {
            // Always call orig first — let native shoot logic run
            orig(self);

            var net = NetworkManager.Instance;
            if (net == null || !net.IsConnected)
                return;

            try
            {
                // Filter: skip NPC weapons
                if (_bOwnerIsNpcField != null && (bool)_bOwnerIsNpcField.GetValue(self))
                    return;

                // Filter: skip melee weapons
                if (_isMeleeProperty != null && (bool)_isMeleeProperty.GetValue(self))
                    return;

                // Get barrel position and direction
                Vector3 barrelPos;
                Vector3 barrelDir;

                if (_barrelTransformProp != null)
                {
                    var barrelTransform = _barrelTransformProp.GetValue(self) as Transform;
                    if (barrelTransform != null)
                    {
                        barrelPos = barrelTransform.position;
                        barrelDir = barrelTransform.forward;
                    }
                    else if (self is Component comp)
                    {
                        barrelPos = comp.transform.position;
                        barrelDir = comp.transform.forward;
                    }
                    else return;
                }
                else if (self is Component comp2)
                {
                    barrelPos = comp2.transform.position;
                    barrelDir = comp2.transform.forward;
                }
                else return;

                // Get weapon item ID
                ushort itemId = 0;
                if (_itemDefinitionProp != null && _itemDefIdField != null && _itemIdValueField != null)
                {
                    var itemDef = _itemDefinitionProp.GetValue(self);
                    if (itemDef != null)
                    {
                        var id = _itemDefIdField.GetValue(itemDef);
                        itemId = (ushort)_itemIdValueField.GetValue(id);
                    }
                }

                // Cache sound from this weapon instance (opportunistic)
                if (Instance != null)
                    Instance.CacheWeaponSound(self, itemId);

                // Build and send message
                var msg = new WeaponFireMessage
                {
                    SteamId = SteamUser.GetSteamID().m_SteamID,
                    PosX = barrelPos.x,
                    PosY = barrelPos.y,
                    PosZ = barrelPos.z,
                    DirX = barrelDir.x,
                    DirY = barrelDir.y,
                    DirZ = barrelDir.z,
                    WeaponItemId = itemId
                };

                if (net.IsHost)
                    net.SendToAll(msg);
                else
                {
                    var hostId = LobbyManager.Instance?.HostSteamId ?? CSteamID.Nil;
                    if (hostId != CSteamID.Nil)
                        net.SendMessage(hostId, msg);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"WeaponFireSync: ShootInterceptor error: {ex.Message}");
            }
        }

        #endregion

        #region Message Handling

        private void OnMessageReceived(CSteamID sender, NetworkMessage msg)
        {
            if (msg.Type != MessageType.WeaponFire)
                return;

            HandleWeaponFire(sender, (WeaponFireMessage)msg);
        }

        private void HandleWeaponFire(CSteamID sender, WeaponFireMessage msg)
        {
            var net = NetworkManager.Instance;
            if (net == null || !net.IsConnected) return;

            // Host relay: forward to all other clients
            if (net.IsHost)
                net.SendToAllExcept(sender, msg);

            // Don't play our own fire effects
            if (msg.SteamId == SteamUser.GetSteamID().m_SteamID)
                return;

            var pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            var dir = new Vector3(msg.DirX, msg.DirY, msg.DirZ);

            PlayWeaponSound(pos, msg.WeaponItemId);
            SpawnCosmeticTracer(pos, dir);
        }

        #endregion

        #region Audio

        private void CacheWeaponSound(object weaponInstance, ushort itemId)
        {
            if (itemId == 0 || _weaponSoundCache.ContainsKey(itemId))
                return;

            try
            {
                // Priority 1: fireClipList.soundEvent
                if (_fireClipListField != null)
                {
                    var clipList = _fireClipListField.GetValue(weaponInstance);
                    if (clipList != null && _aclSoundEventField != null)
                    {
                        var soundEvent = _aclSoundEventField.GetValue(clipList);
                        if (soundEvent != null)
                        {
                            _weaponSoundCache[itemId] = soundEvent;
                            return;
                        }
                    }

                    // Priority 2: fireClipList.audioClips[0]
                    if (clipList != null && _aclAudioClipsField != null)
                    {
                        var clips = _aclAudioClipsField.GetValue(clipList) as AudioClip[];
                        if (clips != null && clips.Length > 0 && clips[0] != null)
                        {
                            _weaponSoundCache[itemId] = clips[0];
                            return;
                        }
                    }
                }

                // Priority 3: fireClips[0] (legacy fallback)
                if (_fireClipsField != null)
                {
                    var clips = _fireClipsField.GetValue(weaponInstance) as AudioClip[];
                    if (clips != null && clips.Length > 0 && clips[0] != null)
                    {
                        _weaponSoundCache[itemId] = clips[0];
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"WeaponFireSync: CacheWeaponSound failed for itemId={itemId}: {ex.Message}");
            }
        }

        private void PlayWeaponSound(Vector3 position, ushort itemId)
        {
            if (itemId == 0 || !_weaponSoundCache.TryGetValue(itemId, out var sound))
                return;

            try
            {
                // SoundEvent path — native 3D audio
                if (_soundEventType != null && _soundEventType.IsInstanceOfType(sound) && _soundEventPlayMethod != null)
                {
                    EnsureAudioTransform();
                    _audioTransform.position = position;
                    _soundEventPlayMethod.Invoke(sound, new object[] { _audioTransform });
                    return;
                }

                // AudioClip fallback — Unity 3D audio
                if (sound is AudioClip clip)
                {
                    AudioSource.PlayClipAtPoint(clip, position, 0.6f);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"WeaponFireSync: PlayWeaponSound failed: {ex.Message}");
            }
        }

        private void EnsureAudioTransform()
        {
            if (_audioTransform == null)
            {
                var go = new GameObject("SulfurMP_WeaponAudio");
                go.hideFlags = HideFlags.HideAndDontSave;
                DontDestroyOnLoad(go);
                _audioTransform = go.transform;
            }
        }

        /// <summary>
        /// Proactively cache sounds from all loaded weapon instances after scene load.
        /// This fills the cache so we have sounds ready when remote players fire.
        /// </summary>
        private void TryCacheAllWeapons()
        {
            if (_weaponType == null || _itemDefinitionProp == null ||
                _itemDefIdField == null || _itemIdValueField == null)
                return;

            try
            {
                var weapons = Resources.FindObjectsOfTypeAll(_weaponType);
                int cached = 0;

                foreach (var weapon in weapons)
                {
                    try
                    {
                        var itemDef = _itemDefinitionProp.GetValue(weapon);
                        if (itemDef == null) continue;

                        var id = _itemDefIdField.GetValue(itemDef);
                        var itemId = (ushort)_itemIdValueField.GetValue(id);
                        if (itemId == 0) continue;

                        if (!_weaponSoundCache.ContainsKey(itemId))
                        {
                            CacheWeaponSound(weapon, itemId);
                            if (_weaponSoundCache.ContainsKey(itemId))
                                cached++;
                        }
                    }
                    catch
                    {
                        // Skip individual weapon failures
                    }
                }

                if (cached > 0)
                    Plugin.Log.LogInfo($"WeaponFireSync: Cached {cached} weapon sounds (total: {_weaponSoundCache.Count})");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"WeaponFireSync: TryCacheAllWeapons failed: {ex.Message}");
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            var net = NetworkManager.Instance;
            if (net == null || !net.IsConnected) return;

            // Delay cache fill to let weapons spawn
            StartCoroutine(DelayedCacheFill());
        }

        private IEnumerator DelayedCacheFill()
        {
            yield return new WaitForSecondsRealtime(3f);
            TryCacheAllWeapons();
        }

        #endregion

        #region Tracers

        private void SpawnCosmeticTracer(Vector3 position, Vector3 direction)
        {
            if (_autoPoolInstance == null || _getProjectileMethod == null ||
                _projSystemInstance == null || _startProjectileMethod == null ||
                _bulletEnumValue == null)
                return;

            try
            {
                var autoPool = _autoPoolInstance.GetValue(null);
                if (autoPool == null) return;

                var projSystem = _projSystemInstance.GetValue(null);
                if (projSystem == null) return;

                // GetProjectile(ProjectileTypes.Bullet, out Projectile)
                var args = new object[] { _bulletEnumValue, null };
                _getProjectileMethod.Invoke(autoPool, args);

                var projectile = args[1];
                if (projectile == null) return;

                var projComp = projectile as Component;
                if (projComp == null) return;

                // Position + rotation
                projComp.transform.position = position;
                projComp.transform.rotation = Quaternion.LookRotation(direction);
                projComp.transform.localScale = new Vector3(0.1f, 0.1f, 0.1f);

                // Lifetime + restart
                _lifeTimeField?.SetValue(projectile, 3f);
                _restartLifetimeMethod?.Invoke(projectile, null);

                // Disable gravity for straight-line tracer
                _useGravityField?.SetValue(projectile, false);

                // No damage attribution
                _setOwnerMethod?.Invoke(projectile, new object[] { null });
                _turnOffDamageMethod?.Invoke(projectile, null);

                // Zero stale fields that ProcessHit checks directly (bypassing damageTurnedOff flag).
                // Pool reuse leaves explicitDamage > 0 and damageComponents populated from previous use.
                _explicitDamageField?.SetValue(projectile, 0f);
                _damageComponentsField?.SetValue(projectile, null);

                // Reset and enable trail renderers
                _resetEffectsMethod?.Invoke(projectile, null);
                if (_trailsField != null)
                {
                    var trails = _trailsField.GetValue(projectile) as TrailRenderer[];
                    if (trails != null)
                    {
                        foreach (var trail in trails)
                        {
                            if (trail != null)
                            {
                                trail.Clear();
                                trail.emitting = true;
                            }
                        }
                    }
                }

                // Launch via native ProjectileSystem
                // Bullet speed ~200 m/s, same as game's GetBulletSpeed range
                Vector3 velocity = direction.normalized * 200f;
                _startProjectileMethod.Invoke(projSystem, new object[] { projectile, velocity, false });
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"WeaponFireSync: SpawnCosmeticTracer failed: {ex.Message}");
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
                try
                {
                    if (_weaponType == null)
                        _weaponType = asm.GetType("PerfectRandom.Sulfur.Core.Weapons.Weapon");
                    if (_holdableType == null)
                        _holdableType = asm.GetType("PerfectRandom.Sulfur.Core.Weapons.Holdable");
                    if (_itemDefinitionType == null)
                        _itemDefinitionType = asm.GetType("PerfectRandom.Sulfur.Core.Items.ItemDefinition");
                    if (_itemIdType == null)
                        _itemIdType = asm.GetType("PerfectRandom.Sulfur.Core.ItemId");
                    if (_audioClipListType == null)
                        _audioClipListType = asm.GetType("PerfectRandom.Sulfur.Core.Audio.AudioClipList");
                    if (_soundEventType == null)
                        _soundEventType = asm.GetType("Sonity.SoundEvent");
                    if (_autoPoolType == null)
                        _autoPoolType = asm.GetType("PerfectRandom.Sulfur.Core.AutoPool");
                    if (_projSystemType == null)
                        _projSystemType = asm.GetType("PerfectRandom.Sulfur.Core.ProjectileSystem");
                    if (_projectileType == null)
                        _projectileType = asm.GetType("PerfectRandom.Sulfur.Core.Weapons.Projectile");
                    if (_projectileTypesEnum == null)
                        _projectileTypesEnum = asm.GetType("PerfectRandom.Sulfur.Core.Items.ProjectileTypes");
                }
                catch (ReflectionTypeLoadException)
                {
                    // Some assemblies throw on GetTypes — skip
                }
            }

            // Weapon.Shoot()
            if (_weaponType != null)
            {
                _shootMethod = _weaponType.GetMethod("Shoot",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);

                _isMeleeProperty = _weaponType.GetProperty("IsMelee",
                    BindingFlags.Public | BindingFlags.Instance);

                _barrelTransformProp = _weaponType.GetProperty("BarrelTransform",
                    BindingFlags.Public | BindingFlags.Instance);

                // Audio fields (private on Weapon)
                var privateFlags = BindingFlags.NonPublic | BindingFlags.Instance;
                _fireClipListField = _weaponType.GetField("fireClipList", privateFlags);
                _fireClipsField = _weaponType.GetField("fireClips", privateFlags);

                if (_shootMethod != null)
                    Plugin.Log.LogInfo("WeaponFireSync: Found Weapon.Shoot");
                else
                    Plugin.Log.LogWarning("WeaponFireSync: Weapon.Shoot not found");
            }
            else
            {
                Plugin.Log.LogWarning("WeaponFireSync: Weapon type not found");
            }

            // Holdable fields
            if (_holdableType != null)
            {
                _bOwnerIsNpcField = _holdableType.GetField("bOwnerIsNpc",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                _itemDefinitionProp = _holdableType.GetProperty("ItemDefinition",
                    BindingFlags.Public | BindingFlags.Instance);

                if (_bOwnerIsNpcField != null)
                    Plugin.Log.LogInfo("WeaponFireSync: Found Holdable.bOwnerIsNpc");
            }

            // ItemDefinition.id → ItemId.value
            if (_itemDefinitionType != null)
            {
                _itemDefIdField = _itemDefinitionType.GetField("id",
                    BindingFlags.Public | BindingFlags.Instance);
            }

            if (_itemIdType != null)
            {
                _itemIdValueField = _itemIdType.GetField("value",
                    BindingFlags.Public | BindingFlags.Instance);
            }

            // AudioClipList fields
            if (_audioClipListType != null)
            {
                _aclSoundEventField = _audioClipListType.GetField("soundEvent",
                    BindingFlags.Public | BindingFlags.Instance);
                _aclAudioClipsField = _audioClipListType.GetField("audioClips",
                    BindingFlags.Public | BindingFlags.Instance);
            }

            // SoundEvent.Play(Transform)
            if (_soundEventType != null)
            {
                _soundEventPlayMethod = _soundEventType.GetMethod("Play",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(Transform) }, null);

                if (_soundEventPlayMethod != null)
                    Plugin.Log.LogInfo("WeaponFireSync: Found SoundEvent.Play(Transform)");
            }

            // AutoPool — StaticInstance<AutoPool>.Instance
            if (_autoPoolType != null)
            {
                // AutoPool inherits StaticInstance<AutoPool> which has public static Instance property
                _autoPoolInstance = _autoPoolType.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

                if (_autoPoolInstance != null)
                    Plugin.Log.LogInfo("WeaponFireSync: Found AutoPool.Instance");

                // GetProjectile(ProjectileTypes, out Projectile)
                if (_projectileType != null && _projectileTypesEnum != null)
                {
                    _getProjectileMethod = _autoPoolType.GetMethod("GetProjectile",
                        BindingFlags.Public | BindingFlags.Instance,
                        null, new[] { _projectileTypesEnum, _projectileType.MakeByRefType() }, null);

                    if (_getProjectileMethod != null)
                        Plugin.Log.LogInfo("WeaponFireSync: Found AutoPool.GetProjectile");
                    else
                        Plugin.Log.LogWarning("WeaponFireSync: AutoPool.GetProjectile not found");
                }
            }

            // ProjectileSystem — StaticInstance<ProjectileSystem>.Instance
            if (_projSystemType != null)
            {
                _projSystemInstance = _projSystemType.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

                if (_projSystemInstance != null)
                    Plugin.Log.LogInfo("WeaponFireSync: Found ProjectileSystem.Instance");

                // StartProjectile(Projectile, Vector3, bool)
                if (_projectileType != null)
                {
                    _startProjectileMethod = _projSystemType.GetMethod("StartProjectile",
                        BindingFlags.Public | BindingFlags.Instance,
                        null, new[] { _projectileType, typeof(Vector3), typeof(bool) }, null);

                    if (_startProjectileMethod != null)
                        Plugin.Log.LogInfo("WeaponFireSync: Found ProjectileSystem.StartProjectile");
                    else
                        Plugin.Log.LogWarning("WeaponFireSync: ProjectileSystem.StartProjectile not found");
                }
            }

            // Projectile fields/methods
            if (_projectileType != null)
            {
                _turnOffDamageMethod = _projectileType.GetMethod("TurnOffDamage",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                _setOwnerMethod = _projectileType.GetMethod("SetOwner",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(GameObject) }, null);
                _useGravityField = _projectileType.GetField("useGravity",
                    BindingFlags.Public | BindingFlags.Instance);
                _trailsField = _projectileType.GetField("trails",
                    BindingFlags.Public | BindingFlags.Instance);
                _lifeTimeField = _projectileType.GetField("lifeTime",
                    BindingFlags.Public | BindingFlags.Instance);
                _restartLifetimeMethod = _projectileType.GetMethod("RestartLifetime",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                _resetEffectsMethod = _projectileType.GetMethod("ResetEffects",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);

                _explicitDamageField = _projectileType.GetField("explicitDamage",
                    BindingFlags.Public | BindingFlags.Instance);
                _damageComponentsField = _projectileType.GetField("damageComponents",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                if (_turnOffDamageMethod != null)
                    Plugin.Log.LogInfo("WeaponFireSync: Found Projectile methods (TurnOffDamage, SetOwner, RestartLifetime, ResetEffects)");
                if (_explicitDamageField != null)
                    Plugin.Log.LogInfo("WeaponFireSync: Found Projectile.explicitDamage + damageComponents fields");
            }

            // ProjectileTypes.Bullet enum value
            if (_projectileTypesEnum != null)
            {
                try
                {
                    _bulletEnumValue = Enum.Parse(_projectileTypesEnum, "Bullet");
                    Plugin.Log.LogInfo($"WeaponFireSync: Resolved ProjectileTypes.Bullet = {_bulletEnumValue}");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"WeaponFireSync: Failed to resolve ProjectileTypes.Bullet: {ex.Message}");
                }
            }
        }

        #endregion
    }
}
