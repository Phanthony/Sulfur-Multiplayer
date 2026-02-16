using System;
using System.Collections.Generic;
using System.Reflection;
using MonoMod.RuntimeDetour;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SulfurMP.UI
{
    /// <summary>
    /// MonoMod hook on PauseMenu.OnEnable to inject a "Multiplayer" button
    /// that opens the MultiplayerPanel full-screen UI.
    /// </summary>
    public static class PauseMenuHook
    {
        private static Hook _onEnableHook;
        private static bool _installed;

        // Track which pause menus we've already injected a button into
        private static readonly HashSet<int> _injectedInstances = new HashSet<int>();

        public static void Install()
        {
            if (_installed) return;
            _installed = true;

            try
            {
                // PauseMenu is in PerfectRandom.Sulfur.Core.UI
                var pauseMenuType = FindType("PerfectRandom.Sulfur.Core.UI.PauseMenu");
                if (pauseMenuType == null)
                {
                    Plugin.Log.LogError("PauseMenuHook: Could not find PauseMenu type");
                    return;
                }

                var onEnableMethod = pauseMenuType.GetMethod("OnEnable",
                    BindingFlags.Public | BindingFlags.Instance);
                if (onEnableMethod == null)
                {
                    Plugin.Log.LogError("PauseMenuHook: Could not find PauseMenu.OnEnable");
                    return;
                }

                _onEnableHook = new Hook(onEnableMethod,
                    new Action<Action<object>, object>(OnEnableHook));

                Plugin.Log.LogInfo("PauseMenuHook: Installed");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"PauseMenuHook: Failed to install: {ex}");
            }
        }

        private static void OnEnableHook(Action<object> orig, object self)
        {
            // Block pause menu while multiplayer panel is open
            if (MultiplayerPanel.Instance != null && MultiplayerPanel.Instance.IsVisible)
            {
                var mb = self as MonoBehaviour;
                if (mb != null)
                    mb.gameObject.SetActive(false);
                return;
            }

            orig(self);

            try
            {
                var pauseMenu = self as MonoBehaviour;
                if (pauseMenu == null) return;

                int instanceId = pauseMenu.gameObject.GetInstanceID();
                if (_injectedInstances.Contains(instanceId)) return;
                _injectedInstances.Add(instanceId);

                InjectMultiplayerButton(pauseMenu);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"PauseMenuHook: Error injecting button: {ex}");
            }
        }

        private static void InjectMultiplayerButton(MonoBehaviour pauseMenu)
        {
            var pmType = pauseMenu.GetType();

            // Get optionsButton (private SerializeField GameObject)
            var optionsField = pmType.GetField("optionsButton",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (optionsField == null)
            {
                Plugin.Log.LogError("PauseMenuHook: optionsButton field not found");
                return;
            }
            var optionsButtonGO = optionsField.GetValue(pauseMenu) as GameObject;
            if (optionsButtonGO == null)
            {
                Plugin.Log.LogError("PauseMenuHook: optionsButton GameObject is null");
                return;
            }

            // Get allButtons list (private SerializeField List<Button>)
            var allButtonsField = pmType.GetField("allButtons",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var allButtons = allButtonsField?.GetValue(pauseMenu) as List<Button>;

            // Clone the options button
            var mpButtonGO = UnityEngine.Object.Instantiate(optionsButtonGO, optionsButtonGO.transform.parent);
            mpButtonGO.name = "SulfurMP_MultiplayerButton";

            // Position after options button
            int optionsIndex = optionsButtonGO.transform.GetSiblingIndex();
            mpButtonGO.transform.SetSiblingIndex(optionsIndex + 1);

            // Change label text
            var tmpTexts = mpButtonGO.GetComponentsInChildren<TextMeshProUGUI>(true);
            foreach (var tmp in tmpTexts)
            {
                tmp.text = "Multiplayer";
            }

            // Rewire button click
            var btn = mpButtonGO.GetComponent<Button>();
            if (btn != null)
            {
                // Replace entirely — RemoveAllListeners only clears runtime listeners,
                // not persistent (serialized) ones cloned from the options button
                btn.onClick = new Button.ButtonClickedEvent();
                var pauseMenuGO = pauseMenu.gameObject;
                btn.onClick.AddListener(() =>
                {
                    pauseMenuGO.SetActive(false);
                    MultiplayerPanel.Instance?.ShowFromPauseMenu(pauseMenuGO);
                });
            }

            // Add to allButtons so PauseMenu.Update includes it for color updates
            if (allButtons != null && btn != null)
            {
                allButtons.Add(btn);
            }

            Plugin.Log.LogInfo("PauseMenuHook: Multiplayer button injected into pause menu");
        }

        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(fullName);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }
    }
}
