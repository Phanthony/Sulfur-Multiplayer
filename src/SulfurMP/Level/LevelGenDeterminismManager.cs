using System;
using System.Reflection;
using MonoMod.RuntimeDetour;
using SulfurMP.Networking;
using UnityEngine;

namespace SulfurMP.Level
{
    /// <summary>
    /// Fixes level generation non-determinism that causes breakable/interactable desync.
    /// Two hooks:
    /// 1. SpawnableEventList.SelectOneItem() — uses UnityEngine.Random.Range instead of seeded RNG
    /// 2. RandomlyKeepChild.Start() — seeds from GetInstanceID() which differs per process
    /// Both are replaced with deterministic logic seeded from the level seed.
    /// </summary>
    public class LevelGenDeterminismManager : MonoBehaviour
    {
        public static LevelGenDeterminismManager Instance { get; private set; }

        // Hook state
        private bool _hookAttempted;
        private int _hookRetryCount;
        private const int MaxHookRetries = 60;
        private Hook _selectOneItemHook;
        private Hook _randomlyKeepChildStartHook;

        // SelectOneItem determinism — counter resets each level load
        private static long _lastEventSeed;
        private static int _eventCallCounter;

        // Reflection cache
        private static Type _spawnableEventListType;
        private static MethodInfo _selectOneItemMethod;
        private static Type _randomlyKeepChildType;
        private static MethodInfo _rkcStartMethod;
        private static FieldInfo _rkcMinField;
        private static FieldInfo _rkcMaxField;

        // MonoMod delegates
        private delegate object orig_SelectOneItem(object self);
        private delegate object hook_SelectOneItem(orig_SelectOneItem orig, object self);
        private delegate void orig_RKCStart(object self);
        private delegate void hook_RKCStart(orig_RKCStart orig, object self);

        private void Awake()
        {
            if (Instance != null)
            {
                Destroy(this);
                return;
            }
            Instance = this;
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
        }

        #region Hook Installation

        private void TryInstallHooks()
        {
            InitReflection();

            if (_selectOneItemMethod == null && _rkcStartMethod == null)
            {
                _hookRetryCount++;
                if (_hookRetryCount >= MaxHookRetries)
                {
                    _hookAttempted = true;
                    Plugin.Log.LogWarning("LevelGenDeterminism: Could not find any hookable methods after max retries");
                }
                return;
            }

            _hookAttempted = true;

            if (_selectOneItemMethod != null)
            {
                try
                {
                    _selectOneItemHook = new Hook(
                        _selectOneItemMethod,
                        new hook_SelectOneItem(SelectOneItemInterceptor));
                    Plugin.Log.LogInfo("LevelGenDeterminism: Hooked SpawnableEventList.SelectOneItem");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"LevelGenDeterminism: Failed to hook SelectOneItem: {ex}");
                }
            }

            if (_rkcStartMethod != null)
            {
                try
                {
                    _randomlyKeepChildStartHook = new Hook(
                        _rkcStartMethod,
                        new hook_RKCStart(RandomlyKeepChildInterceptor));
                    Plugin.Log.LogInfo("LevelGenDeterminism: Hooked RandomlyKeepChild.Start");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"LevelGenDeterminism: Failed to hook RandomlyKeepChild.Start: {ex}");
                }
            }
        }

        private void DisposeHooks()
        {
            _selectOneItemHook?.Dispose();
            _selectOneItemHook = null;
            _randomlyKeepChildStartHook?.Dispose();
            _randomlyKeepChildStartHook = null;
        }

        #endregion

        #region SelectOneItem Hook

        /// <summary>
        /// Wraps SelectOneItem with deterministic UnityEngine.Random state.
        /// The original method uses Random.Range(0, num) — we seed it deterministically
        /// from level seed + call counter so both host and client get the same results.
        /// </summary>
        private static object SelectOneItemInterceptor(orig_SelectOneItem orig, object self)
        {
            var net = NetworkManager.Instance;
            var lvl = LevelSyncManager.Instance;
            if (net == null || !net.IsConnected || lvl == null || lvl.CurrentSeed == 0)
                return orig(self);

            // Reset counter when seed changes (new level load)
            long seed = lvl.CurrentSeed;
            if (seed != _lastEventSeed)
            {
                _lastEventSeed = seed;
                _eventCallCounter = 0;
            }

            _eventCallCounter++;

            // Deterministic seed from level seed + call counter
            int deterministicSeed = unchecked((int)(seed ^ (seed >> 32) ^ (_eventCallCounter * 0x9E3779B9L)));

            // Save Unity global random state, seed deterministically, call orig, restore
            var savedState = UnityEngine.Random.state;
            try
            {
                UnityEngine.Random.InitState(deterministicSeed);
                return orig(self);
            }
            finally
            {
                UnityEngine.Random.state = savedState;
            }
        }

        #endregion

        #region RandomlyKeepChild Hook

        /// <summary>
        /// Full replacement for RandomlyKeepChild.Start().
        /// Original seeds shuffle from GetInstanceID() (per-process) and count from UnityEngine.Random.Range.
        /// We replace both with System.Random seeded from level seed + transform position hash.
        /// </summary>
        private static void RandomlyKeepChildInterceptor(orig_RKCStart orig, object self)
        {
            var net = NetworkManager.Instance;
            var lvl = LevelSyncManager.Instance;
            if (net == null || !net.IsConnected || lvl == null || lvl.CurrentSeed == 0)
            {
                orig(self);
                return;
            }

            if (!(self is MonoBehaviour mb))
            {
                orig(self);
                return;
            }

            // Read min/max via reflection
            int min = (int)_rkcMinField.GetValue(self);
            int max = (int)_rkcMaxField.GetValue(self);
            if (max < min) return; // matches original validation (throws, but we silently skip)

            int childCount = mb.transform.childCount;
            if (childCount == 0) return;

            // Deterministic seed from level seed + transform position hash
            var pos = mb.transform.position;
            int posSeed = unchecked(
                (int)(pos.x * 73856093f) ^
                (int)(pos.y * 19349663f) ^
                (int)(pos.z * 83492791f));
            int deterministicSeed = unchecked((int)(lvl.CurrentSeed ^ (lvl.CurrentSeed >> 32) ^ posSeed));
            if (deterministicSeed == 0) deterministicSeed = 1;

            var rng = new System.Random(deterministicSeed);
            int numToKeep = rng.Next(min, max); // [min, max) — same as UnityEngine.Random.Range(int,int)

            // Fisher-Yates shuffle (replaces game's ShuffledList)
            int[] indices = new int[childCount];
            for (int i = 0; i < childCount; i++) indices[i] = i;
            for (int i = childCount - 1; i > 0; i--)
            {
                int j = rng.Next(0, i + 1);
                int tmp = indices[i];
                indices[i] = indices[j];
                indices[j] = tmp;
            }

            // Activate first numToKeep, deactivate rest (same logic as original)
            for (int i = 0; i < childCount; i++)
            {
                mb.transform.GetChild(indices[i]).gameObject.SetActive(i < numToKeep);
            }
        }

        #endregion

        #region Reflection

        private static void InitReflection()
        {
            if (_spawnableEventListType != null && _randomlyKeepChildType != null)
                return;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (_spawnableEventListType == null)
                        _spawnableEventListType = asm.GetType("PerfectRandom.Sulfur.Core.SpawnableEventList");
                    if (_randomlyKeepChildType == null)
                        _randomlyKeepChildType = asm.GetType("PerfectRandom.Sulfur.Gameplay.HelperComponents.RandomlyKeepChild");
                }
                catch (ReflectionTypeLoadException) { }

                if (_spawnableEventListType != null && _randomlyKeepChildType != null)
                    break;
            }

            if (_spawnableEventListType != null && _selectOneItemMethod == null)
            {
                _selectOneItemMethod = _spawnableEventListType.GetMethod("SelectOneItem",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);

                if (_selectOneItemMethod != null)
                    Plugin.Log.LogInfo("LevelGenDeterminism: Found SpawnableEventList.SelectOneItem");
                else
                    Plugin.Log.LogWarning("LevelGenDeterminism: SpawnableEventList found but SelectOneItem method not found");
            }

            if (_randomlyKeepChildType != null && _rkcStartMethod == null)
            {
                _rkcStartMethod = _randomlyKeepChildType.GetMethod("Start",
                    BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                _rkcMinField = _randomlyKeepChildType.GetField("minNumberToKeep",
                    BindingFlags.Public | BindingFlags.Instance);
                _rkcMaxField = _randomlyKeepChildType.GetField("maxNumberToKeep",
                    BindingFlags.Public | BindingFlags.Instance);

                if (_rkcStartMethod != null && _rkcMinField != null && _rkcMaxField != null)
                    Plugin.Log.LogInfo("LevelGenDeterminism: Found RandomlyKeepChild.Start + fields");
                else
                    Plugin.Log.LogWarning($"LevelGenDeterminism: RandomlyKeepChild found but incomplete — Start={_rkcStartMethod != null} min={_rkcMinField != null} max={_rkcMaxField != null}");
            }
        }

        #endregion
    }
}
