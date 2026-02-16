using SulfurMP.Config;
using SulfurMP.Networking;
using SulfurMP.Networking.Messages;
using Steamworks;
using UnityEngine;

namespace SulfurMP.Players
{
    /// <summary>
    /// Captures local player state at TickRate Hz and sends it to all peers.
    /// Lives on SulfurMP_Network (NOT on the player — player is destroyed on level transitions).
    /// </summary>
    public class LocalPlayerSync : MonoBehaviour
    {
        private float _sendTimer;
        private float _sendInterval;

        private void OnEnable()
        {
            _sendInterval = 1f / MultiplayerConfig.TickRate.Value;
        }

        private void Update()
        {
            var net = NetworkManager.Instance;
            if (net == null || !net.IsConnected)
                return;

            // GameManager is null on the main menu
            var gm = GetGameManager();
            if (gm == null)
                return;

            var playerObj = GetPlayerObject(gm);
            if (playerObj == null)
                return;

            _sendTimer += Time.unscaledDeltaTime;
            if (_sendTimer < _sendInterval)
                return;

            _sendTimer -= _sendInterval;

            var msg = BuildStateMessage(gm, playerObj);
            if (msg != null)
                net.SendToAll(msg);
        }

        private PlayerStateMessage BuildStateMessage(object gm, GameObject playerObj)
        {
            var msg = new PlayerStateMessage
            {
                SteamId = SteamUser.GetSteamID().m_SteamID,
                Timestamp = Time.unscaledTime,
            };

            // Position from transform
            msg.SetPosition(playerObj.transform.position);

            // Yaw from body rotation (CMF rotates the body with camera yaw)
            msg.Yaw = playerObj.transform.eulerAngles.y;

            // Pitch from camera controller
            msg.Pitch = GetPlayerPitch(gm);

            // Velocity and grounded from PlayerUnit
            var velocity = GetPlayerVelocity(gm);
            msg.SetVelocity(velocity);
            msg.IsGrounded = GetPlayerGrounded(gm);

            msg.AnimationState = GetAnimationState(playerObj);
            msg.Health = GetHealthByte(gm);

            return msg;
        }

        #region Game Access (reflection-free, via StaticInstance<GameManager>)

        // Cache the GameManager type and accessors
        private static System.Type _gameManagerType;
        private static System.Reflection.PropertyInfo _instanceProp;
        private static System.Reflection.PropertyInfo _playerObjectProp;
        private static System.Reflection.PropertyInfo _playerScriptProp;
        private static System.Reflection.PropertyInfo _playerUnitProp;
        private static bool _reflectionInitialized;

        // Walker controller reflection (for movement state flags)
        private static System.Type _walkerType;
        private static System.Reflection.FieldInfo _isSprintingField;
        private static System.Reflection.FieldInfo _isCrouchingField;
        private static System.Reflection.FieldInfo _isJumpingField;
        private static System.Reflection.FieldInfo _isFallingField;
        private static bool _walkerReflectionInitialized;

        // Health reflection
        private static System.Reflection.PropertyInfo _normalizedHealthProp;
        private static bool _healthReflectionInitialized;

        private static void InitReflection()
        {
            if (_reflectionInitialized) return;
            _reflectionInitialized = true;

            // Find GameManager type
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                _gameManagerType = asm.GetType("PerfectRandom.Sulfur.Core.GameManager");
                if (_gameManagerType != null)
                    break;
            }

            if (_gameManagerType == null)
            {
                Plugin.Log.LogWarning("LocalPlayerSync: Could not find GameManager type");
                return;
            }

            // StaticInstance<GameManager>.Instance is a property on the base class
            _instanceProp = _gameManagerType.GetProperty("Instance",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy);

            _playerObjectProp = _gameManagerType.GetProperty("PlayerObject",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

            _playerScriptProp = _gameManagerType.GetProperty("PlayerScript",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

            _playerUnitProp = _gameManagerType.GetProperty("PlayerUnit",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

            if (_instanceProp == null)
                Plugin.Log.LogWarning("LocalPlayerSync: Could not find GameManager.Instance property");
        }

        private static object GetGameManager()
        {
            InitReflection();
            if (_instanceProp == null) return null;

            try
            {
                var instance = _instanceProp.GetValue(null);
                // Unity null check — destroyed objects are "null" but not C# null
                if (instance is Object unityObj && unityObj == null)
                    return null;
                return instance;
            }
            catch { return null; }
        }

        internal static GameObject GetPlayerObject(object gm)
        {
            if (gm == null || _playerObjectProp == null) return null;
            try
            {
                var obj = _playerObjectProp.GetValue(gm) as GameObject;
                return obj != null ? obj : null;
            }
            catch { return null; }
        }

        private static float GetPlayerPitch(object gm)
        {
            if (gm == null || _playerScriptProp == null) return 0f;
            try
            {
                var playerScript = _playerScriptProp.GetValue(gm);
                if (playerScript == null) return 0f;

                // playerScript.playerCamController.CurrentXAngle
                var camControllerField = playerScript.GetType().GetField("playerCamController",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (camControllerField == null)
                {
                    // Try property
                    var camControllerProp = playerScript.GetType().GetProperty("playerCamController",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (camControllerProp == null) return 0f;

                    var camController = camControllerProp.GetValue(playerScript);
                    if (camController == null) return 0f;

                    var xAngleProp = camController.GetType().GetProperty("CurrentXAngle",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (xAngleProp == null) return 0f;

                    return (float)xAngleProp.GetValue(camController);
                }

                var camCtrl = camControllerField.GetValue(playerScript);
                if (camCtrl == null) return 0f;

                var xAngle = camCtrl.GetType().GetProperty("CurrentXAngle",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (xAngle == null) return 0f;

                return (float)xAngle.GetValue(camCtrl);
            }
            catch { return 0f; }
        }

        private static Vector3 GetPlayerVelocity(object gm)
        {
            if (gm == null || _playerUnitProp == null) return Vector3.zero;
            try
            {
                var playerUnit = _playerUnitProp.GetValue(gm);
                if (playerUnit == null) return Vector3.zero;
                if (playerUnit is Object unityObj && unityObj == null) return Vector3.zero;

                var rbProp = playerUnit.GetType().GetProperty("Rigidbody",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (rbProp == null) return Vector3.zero;

                var rb = rbProp.GetValue(playerUnit) as Rigidbody;
                return rb != null ? rb.velocity : Vector3.zero;
            }
            catch { return Vector3.zero; }
        }

        private static bool GetPlayerGrounded(object gm)
        {
            if (gm == null || _playerUnitProp == null) return false;
            try
            {
                var playerUnit = _playerUnitProp.GetValue(gm);
                if (playerUnit == null) return false;
                if (playerUnit is Object unityObj && unityObj == null) return false;

                var groundedProp = playerUnit.GetType().GetProperty("isGrounded",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (groundedProp == null) return false;

                return (bool)groundedProp.GetValue(playerUnit);
            }
            catch { return false; }
        }

        private static byte GetAnimationState(GameObject playerObj)
        {
            if (!_walkerReflectionInitialized)
            {
                _walkerReflectionInitialized = true;
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    _walkerType = asm.GetType("PerfectRandom.Sulfur.Core.Movement.ExtendedAdvancedWalkerController");
                    if (_walkerType != null) break;
                }
                if (_walkerType != null)
                {
                    var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
                    _isSprintingField = _walkerType.GetField("isSprinting", flags);
                    _isCrouchingField = _walkerType.GetField("isCrouching", flags);
                    _isJumpingField = _walkerType.GetField("isJumping", flags);
                    _isFallingField = _walkerType.GetField("isFalling", flags);
                }
            }

            if (_walkerType == null) return 0;

            try
            {
                var walker = playerObj.GetComponent(_walkerType);
                if (walker == null) return 0;

                byte state = 0;
                if (_isSprintingField != null && (bool)_isSprintingField.GetValue(walker))
                    state |= 1; // bit 0
                if (_isCrouchingField != null && (bool)_isCrouchingField.GetValue(walker))
                    state |= 2; // bit 1
                if (_isJumpingField != null && (bool)_isJumpingField.GetValue(walker))
                    state |= 4; // bit 2
                if (_isFallingField != null && (bool)_isFallingField.GetValue(walker))
                    state |= 8; // bit 3
                return state;
            }
            catch { return 0; }
        }

        private static byte GetHealthByte(object gm)
        {
            if (gm == null || _playerUnitProp == null) return 255;
            try
            {
                var playerUnit = _playerUnitProp.GetValue(gm);
                if (playerUnit == null) return 255;
                if (playerUnit is Object unityObj && unityObj == null) return 255;

                if (!_healthReflectionInitialized)
                {
                    _healthReflectionInitialized = true;
                    _normalizedHealthProp = playerUnit.GetType().GetProperty("normalizedHealth",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                }

                if (_normalizedHealthProp == null) return 255;
                float health = (float)_normalizedHealthProp.GetValue(playerUnit);
                return (byte)(Mathf.Clamp01(health) * 255f);
            }
            catch { return 255; }
        }

        #endregion
    }
}
