using System;
using System.Reflection;
using HarmonyLib;
using MonoMod.RuntimeDetour;

namespace SulfurMP.Patches
{
    /// <summary>
    /// Bootstrap using multiple strategies to find one that works:
    /// 1. MonoMod Hook (direct detour, bypasses Harmony)
    /// 2. Harmony Prefix (in case postfixes are broken)
    /// 3. Harmony Postfix via attribute (standard approach)
    /// </summary>
    public static class BootstrapPatch
    {
        private static Hook _startupHook;
        private static Hook _steamHook;

        public static void Apply(Harmony harmony)
        {
            // Strategy 1: MonoMod direct hooks (most reliable)
            TryMonoModHook();

            // Strategy 2: Harmony Prefix (maybe postfix is broken)
            TryHarmonyPrefix(harmony);
        }

        private static void TryMonoModHook()
        {
            try
            {
                // Hook StartupLaunch.Update via MonoMod
                var startupType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.StartupLaunch");
                if (startupType != null)
                {
                    var updateMethod = startupType.GetMethod("Update",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (updateMethod != null)
                    {
                        _startupHook = new Hook(updateMethod, new Action<Action<object>, object>(OnStartupUpdate));
                        Plugin.Log.LogInfo("MonoMod: Hooked StartupLaunch.Update");
                    }
                }

                // Hook SteamManager.Update via MonoMod
                var steamType = AccessTools.TypeByName("SteamManager");
                if (steamType != null)
                {
                    var updateMethod = steamType.GetMethod("Update",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (updateMethod != null)
                    {
                        _steamHook = new Hook(updateMethod, new Action<Action<object>, object>(OnSteamUpdate));
                        Plugin.Log.LogInfo("MonoMod: Hooked SteamManager.Update");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"MonoMod hooks failed: {ex}");
            }
        }

        private static void OnStartupUpdate(Action<object> orig, object self)
        {
            orig(self);
            if (!Plugin.Initialized)
            {
                Plugin.Log.LogInfo("MonoMod: StartupLaunch.Update fired!");
                Plugin.Bootstrap();
            }
        }

        private static void OnSteamUpdate(Action<object> orig, object self)
        {
            orig(self);
            if (!Plugin.Initialized)
            {
                Plugin.Log.LogInfo("MonoMod: SteamManager.Update fired!");
                Plugin.Bootstrap();
            }
        }

        private static void TryHarmonyPrefix(Harmony harmony)
        {
            try
            {
                var prefix = new HarmonyMethod(typeof(BootstrapPatch), nameof(PrefixHook));

                var startupType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.StartupLaunch");
                if (startupType != null)
                {
                    var method = AccessTools.Method(startupType, "Update");
                    if (method != null)
                    {
                        harmony.Patch(method, prefix: prefix);
                        Plugin.Log.LogInfo("Harmony: Prefix on StartupLaunch.Update");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Harmony prefix failed: {ex}");
            }
        }

        public static void PrefixHook()
        {
            if (!Plugin.Initialized)
            {
                Plugin.Log.LogInfo("Harmony PREFIX fired!");
                Plugin.Bootstrap();
            }
        }

        public static void Dispose()
        {
            _startupHook?.Dispose();
            _steamHook?.Dispose();
        }
    }
}
