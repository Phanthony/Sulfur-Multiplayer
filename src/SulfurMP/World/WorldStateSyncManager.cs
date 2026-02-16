using System;
using System.Collections;
using System.Reflection;
using MonoMod.RuntimeDetour;
using SulfurMP.Networking;
using SulfurMP.Networking.Messages;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SulfurMP.World
{
    /// <summary>
    /// Synchronizes world object state (doors, gates, breakables) between host and clients.
    /// Uses position-based identification — each object type is found by nearest match.
    /// All three types follow the same pattern: both sides run native logic, broadcast result,
    /// receiver applies. One message type (WorldObjectStateMessage) covers all three.
    /// </summary>
    public class WorldStateSyncManager : MonoBehaviour
    {
        public static WorldStateSyncManager Instance { get; private set; }

        private const byte ObjTypeDoor = 0;
        private const byte ObjTypeGate = 1;
        private const byte ObjTypeBreakable = 2;
        private const byte ObjTypeChallengeDoor = 3;
        private const byte ObjTypeElevator = 4;

        // Bypass flag — prevents re-broadcast when applying received state
        private static bool _isNetworkStateChange;

        // Hook state
        private bool _hookAttempted;
        private int _hookRetryCount;
        private const int MaxHookRetries = 60;

        private Hook _doorOnInteractHook;
        private Hook _gateOpenHook;
        private Hook _gateCloseHook;
        private Hook _unitDieHook;
        private Hook _challengeOpenHook;
        private Hook _challengeCloseHook;
        private Hook _elevatorGoUpHook;
        private Hook _elevatorGoDownHook;

        // Reflection cache — initialized once
        private static bool _reflectionInit;

        // OpenableDoor
        private static Type _openableDoorType;
        private static FieldInfo _doorOpenedField;       // private bool opened
        private static FieldInfo _doorIsClosedField;     // private bool isClosed
        private static FieldInfo _doorIsMovingField;     // private bool isMoving
        private static MethodInfo _doorOpenMethod;       // private void Open()
        private static MethodInfo _doorOnInteractMethod; // public override bool OnInteract(Player)
        private static FieldInfo _doorSoundCloseField;   // private SoundEvent soundClose
        private static FieldInfo _doorSoundOpenField;    // private SoundEvent soundOpen
        private static FieldInfo _doorIsCloseableField;  // private bool isCloseable

        // MetalGate
        private static Type _metalGateType;
        private static MethodInfo _metalGateOpenMethod;  // public void Open()
        private static MethodInfo _metalGateCloseMethod; // public void Close()

        // ChallengeControlHelper
        private static Type _challengeControlHelperType;
        private static MethodInfo _openChallengeDoorMethod;  // public void OpenChallengeDoor()
        private static MethodInfo _closeChallengeDoorMethod; // public void CloseChallengeDoor()

        // CousinElevator
        private static Type _cousinElevatorType;
        private static MethodInfo _elevatorGoUpMethod;   // public void GoUp()
        private static MethodInfo _elevatorGoDownMethod;  // public void GoDown()

        // Breakable
        private static Type _breakableType;
        private static MethodInfo _breakableDieMethod;   // public override void Die()

        // Unit (parent of Breakable)
        private static Type _unitType;
        private static FieldInfo _unitStateField;        // public UnitState unitState
        private static object _unitStateDead;            // boxed UnitState.Dead value
        private static MethodInfo _unitDieMethod;        // public virtual void Die()

        // SoundEvent.Play(Transform)
        private static MethodInfo _soundEventPlayMethod;

        // Player type (for OnInteract parameter)
        private static Type _playerType;

        // MonoMod delegates
        // OpenableDoor.OnInteract(Player) returns bool
        private delegate bool orig_DoorOnInteract(object self, object player);
        private delegate bool hook_DoorOnInteract(orig_DoorOnInteract orig, object self, object player);

        // MetalGate.Open() / Close() — void, no params
        private delegate void orig_GateMethod(object self);
        private delegate void hook_GateMethod(orig_GateMethod orig, object self);

        // Unit.Die() — void, no params (hooks base virtual, filters for Breakable)
        private delegate void orig_UnitDie(object self);
        private delegate void hook_UnitDie(orig_UnitDie orig, object self);

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
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDisable()
        {
            NetworkEvents.OnMessageReceived -= OnMessageReceived;
            NetworkEvents.OnDisconnected -= OnDisconnected;
            SceneManager.sceneLoaded -= OnSceneLoaded;
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

            // Need at least one hookable method to proceed
            if (_doorOnInteractMethod == null && _metalGateOpenMethod == null && _unitDieMethod == null
                && _openChallengeDoorMethod == null && _elevatorGoUpMethod == null)
            {
                _hookRetryCount++;
                if (_hookRetryCount >= MaxHookRetries)
                {
                    _hookAttempted = true;
                    Plugin.Log.LogWarning("WorldStateSync: Could not find any hookable methods after max retries");
                }
                return;
            }

            _hookAttempted = true;

            // Door hook
            if (_doorOnInteractMethod != null)
            {
                try
                {
                    _doorOnInteractHook = new Hook(
                        _doorOnInteractMethod,
                        new hook_DoorOnInteract(DoorOnInteractInterceptor));
                    Plugin.Log.LogInfo("WorldStateSync: Hooked OpenableDoor.OnInteract");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"WorldStateSync: Failed to hook OpenableDoor.OnInteract: {ex}");
                }
            }

            // Gate hooks
            if (_metalGateOpenMethod != null)
            {
                try
                {
                    _gateOpenHook = new Hook(
                        _metalGateOpenMethod,
                        new hook_GateMethod(GateOpenInterceptor));
                    Plugin.Log.LogInfo("WorldStateSync: Hooked MetalGate.Open");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"WorldStateSync: Failed to hook MetalGate.Open: {ex}");
                }
            }

            if (_metalGateCloseMethod != null)
            {
                try
                {
                    _gateCloseHook = new Hook(
                        _metalGateCloseMethod,
                        new hook_GateMethod(GateCloseInterceptor));
                    Plugin.Log.LogInfo("WorldStateSync: Hooked MetalGate.Close");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"WorldStateSync: Failed to hook MetalGate.Close: {ex}");
                }
            }

            // ChallengeControlHelper hooks
            if (_openChallengeDoorMethod != null)
            {
                try
                {
                    _challengeOpenHook = new Hook(
                        _openChallengeDoorMethod,
                        new hook_GateMethod(ChallengeOpenInterceptor));
                    Plugin.Log.LogInfo("WorldStateSync: Hooked ChallengeControlHelper.OpenChallengeDoor");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"WorldStateSync: Failed to hook ChallengeControlHelper.OpenChallengeDoor: {ex}");
                }
            }

            if (_closeChallengeDoorMethod != null)
            {
                try
                {
                    _challengeCloseHook = new Hook(
                        _closeChallengeDoorMethod,
                        new hook_GateMethod(ChallengeCloseInterceptor));
                    Plugin.Log.LogInfo("WorldStateSync: Hooked ChallengeControlHelper.CloseChallengeDoor");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"WorldStateSync: Failed to hook ChallengeControlHelper.CloseChallengeDoor: {ex}");
                }
            }

            // CousinElevator hooks
            if (_elevatorGoUpMethod != null)
            {
                try
                {
                    _elevatorGoUpHook = new Hook(
                        _elevatorGoUpMethod,
                        new hook_GateMethod(ElevatorGoUpInterceptor));
                    Plugin.Log.LogInfo("WorldStateSync: Hooked CousinElevator.GoUp");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"WorldStateSync: Failed to hook CousinElevator.GoUp: {ex}");
                }
            }

            if (_elevatorGoDownMethod != null)
            {
                try
                {
                    _elevatorGoDownHook = new Hook(
                        _elevatorGoDownMethod,
                        new hook_GateMethod(ElevatorGoDownInterceptor));
                    Plugin.Log.LogInfo("WorldStateSync: Hooked CousinElevator.GoDown");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"WorldStateSync: Failed to hook CousinElevator.GoDown: {ex}");
                }
            }

            // Unit.Die hook (filters for Breakable instances inside interceptor)
            if (_unitDieMethod != null)
            {
                try
                {
                    _unitDieHook = new Hook(
                        _unitDieMethod,
                        new hook_UnitDie(UnitDieInterceptor));
                    Plugin.Log.LogInfo("WorldStateSync: Hooked Unit.Die (for breakable detection)");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"WorldStateSync: Failed to hook Unit.Die: {ex}");
                }
            }
        }

        private void DisposeHooks()
        {
            _doorOnInteractHook?.Dispose();
            _doorOnInteractHook = null;
            _gateOpenHook?.Dispose();
            _gateOpenHook = null;
            _gateCloseHook?.Dispose();
            _gateCloseHook = null;
            _unitDieHook?.Dispose();
            _unitDieHook = null;
            _challengeOpenHook?.Dispose();
            _challengeOpenHook = null;
            _challengeCloseHook?.Dispose();
            _challengeCloseHook = null;
            _elevatorGoUpHook?.Dispose();
            _elevatorGoUpHook = null;
            _elevatorGoDownHook?.Dispose();
            _elevatorGoDownHook = null;
            _hookAttempted = false;
        }

        #endregion

        #region Door Hook

        private static bool DoorOnInteractInterceptor(orig_DoorOnInteract orig, object self, object player)
        {
            var net = NetworkManager.Instance;

            // Not in multiplayer — pass through
            if (net == null || !net.IsConnected || _isNetworkStateChange)
                return orig(self, player);

            // Let native logic run (key check, animation, sound, etc.)
            bool result = orig(self, player);

            // Read state after native logic ran
            try
            {
                if (self is Component comp && comp != null)
                {
                    bool opened = _doorOpenedField != null && (bool)_doorOpenedField.GetValue(self);
                    bool isClosed = _doorIsClosedField != null && (bool)_doorIsClosedField.GetValue(self);

                    // Only broadcast if the door actually opened (or is closeable and toggled)
                    bool isCloseable = _doorIsCloseableField != null && (bool)_doorIsCloseableField.GetValue(self);
                    if (opened || isCloseable)
                    {
                        var pos = comp.transform.position;
                        var msg = new WorldObjectStateMessage
                        {
                            ObjectType = ObjTypeDoor,
                            PosX = pos.x,
                            PosY = pos.y,
                            PosZ = pos.z,
                            IsOpen = !isClosed
                        };
                        BroadcastWorldState(msg);
                        Plugin.Log.LogInfo($"WorldStateSync: Door interact broadcast isOpen={!isClosed} pos={pos}");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"WorldStateSync: Door broadcast failed: {ex.Message}");
            }

            return result;
        }

        #endregion

        #region Gate Hooks

        private static void GateOpenInterceptor(orig_GateMethod orig, object self)
        {
            orig(self);

            var net = NetworkManager.Instance;
            if (net == null || !net.IsConnected || _isNetworkStateChange)
                return;

            try
            {
                if (self is Component comp && comp != null)
                {
                    var pos = comp.transform.position;
                    var msg = new WorldObjectStateMessage
                    {
                        ObjectType = ObjTypeGate,
                        PosX = pos.x,
                        PosY = pos.y,
                        PosZ = pos.z,
                        IsOpen = true
                    };
                    BroadcastWorldState(msg);
                    Plugin.Log.LogInfo($"WorldStateSync: Gate Open broadcast pos={pos}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"WorldStateSync: Gate Open broadcast failed: {ex.Message}");
            }
        }

        private static void GateCloseInterceptor(orig_GateMethod orig, object self)
        {
            orig(self);

            var net = NetworkManager.Instance;
            if (net == null || !net.IsConnected || _isNetworkStateChange)
                return;

            try
            {
                if (self is Component comp && comp != null)
                {
                    var pos = comp.transform.position;
                    var msg = new WorldObjectStateMessage
                    {
                        ObjectType = ObjTypeGate,
                        PosX = pos.x,
                        PosY = pos.y,
                        PosZ = pos.z,
                        IsOpen = false
                    };
                    BroadcastWorldState(msg);
                    Plugin.Log.LogInfo($"WorldStateSync: Gate Close broadcast pos={pos}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"WorldStateSync: Gate Close broadcast failed: {ex.Message}");
            }
        }

        #endregion

        #region ChallengeControlHelper Hooks

        private static void ChallengeOpenInterceptor(orig_GateMethod orig, object self)
        {
            orig(self);

            var net = NetworkManager.Instance;
            if (net == null || !net.IsConnected || _isNetworkStateChange)
                return;

            try
            {
                if (self is Component comp && comp != null)
                {
                    var pos = comp.transform.position;
                    var msg = new WorldObjectStateMessage
                    {
                        ObjectType = ObjTypeChallengeDoor,
                        PosX = pos.x,
                        PosY = pos.y,
                        PosZ = pos.z,
                        IsOpen = true
                    };
                    BroadcastWorldState(msg);
                    Plugin.Log.LogInfo($"WorldStateSync: ChallengeDoor Open broadcast pos={pos}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"WorldStateSync: ChallengeDoor Open broadcast failed: {ex.Message}");
            }
        }

        private static void ChallengeCloseInterceptor(orig_GateMethod orig, object self)
        {
            orig(self);

            var net = NetworkManager.Instance;
            if (net == null || !net.IsConnected || _isNetworkStateChange)
                return;

            try
            {
                if (self is Component comp && comp != null)
                {
                    var pos = comp.transform.position;
                    var msg = new WorldObjectStateMessage
                    {
                        ObjectType = ObjTypeChallengeDoor,
                        PosX = pos.x,
                        PosY = pos.y,
                        PosZ = pos.z,
                        IsOpen = false
                    };
                    BroadcastWorldState(msg);
                    Plugin.Log.LogInfo($"WorldStateSync: ChallengeDoor Close broadcast pos={pos}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"WorldStateSync: ChallengeDoor Close broadcast failed: {ex.Message}");
            }
        }

        #endregion

        #region CousinElevator Hooks

        private static void ElevatorGoUpInterceptor(orig_GateMethod orig, object self)
        {
            orig(self);

            var net = NetworkManager.Instance;
            if (net == null || !net.IsConnected || _isNetworkStateChange)
                return;

            try
            {
                if (self is Component comp && comp != null)
                {
                    var pos = comp.transform.position;
                    var msg = new WorldObjectStateMessage
                    {
                        ObjectType = ObjTypeElevator,
                        PosX = pos.x,
                        PosY = pos.y,
                        PosZ = pos.z,
                        IsOpen = true // up
                    };
                    BroadcastWorldState(msg);
                    Plugin.Log.LogInfo($"WorldStateSync: Elevator GoUp broadcast pos={pos}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"WorldStateSync: Elevator GoUp broadcast failed: {ex.Message}");
            }
        }

        private static void ElevatorGoDownInterceptor(orig_GateMethod orig, object self)
        {
            orig(self);

            var net = NetworkManager.Instance;
            if (net == null || !net.IsConnected || _isNetworkStateChange)
                return;

            try
            {
                if (self is Component comp && comp != null)
                {
                    var pos = comp.transform.position;
                    var msg = new WorldObjectStateMessage
                    {
                        ObjectType = ObjTypeElevator,
                        PosX = pos.x,
                        PosY = pos.y,
                        PosZ = pos.z,
                        IsOpen = false // down
                    };
                    BroadcastWorldState(msg);
                    Plugin.Log.LogInfo($"WorldStateSync: Elevator GoDown broadcast pos={pos}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"WorldStateSync: Elevator GoDown broadcast failed: {ex.Message}");
            }
        }

        #endregion

        #region Breakable Hook (via Unit.Die filter)

        private static void UnitDieInterceptor(orig_UnitDie orig, object self)
        {
            // Only intercept Breakable instances — let everything else pass through
            bool isBreakable = _breakableType != null && _breakableType.IsInstanceOfType(self);

            if (!isBreakable)
            {
                orig(self);
                return;
            }

            // Check if already dead before calling orig
            bool wasAlive = IsUnitAlive(self);

            orig(self);

            var net = NetworkManager.Instance;
            if (net == null || !net.IsConnected || _isNetworkStateChange)
                return;

            if (!wasAlive)
            {
                Plugin.Log.LogWarning("WorldStateSync: Breakable was already dead on Die() entry, skipping broadcast");
                return;
            }

            try
            {
                if (self is Component comp && comp != null)
                {
                    var pos = comp.transform.position;
                    var msg = new WorldObjectStateMessage
                    {
                        ObjectType = ObjTypeBreakable,
                        PosX = pos.x,
                        PosY = pos.y,
                        PosZ = pos.z,
                        IsOpen = true // broken
                    };
                    BroadcastWorldState(msg);
                    Plugin.Log.LogInfo($"WorldStateSync: Breakable Die broadcast name={comp.gameObject.name} pos={pos}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"WorldStateSync: Breakable Die broadcast failed: {ex.Message}");
            }
        }

        #endregion

        #region Message Handling

        private void OnMessageReceived(CSteamID sender, NetworkMessage msg)
        {
            if (msg.Type == MessageType.InteractableState)
            {
                Plugin.Log.LogInfo($"WorldStateSync: Received InteractableState from {sender}");
                HandleWorldObjectState(sender, (WorldObjectStateMessage)msg);
            }
            else if (msg.Type == MessageType.BreakableInventory)
            {
                Plugin.Log.LogInfo($"WorldStateSync: Received BreakableInventory from {sender}");
                HandleBreakableInventory((BreakableInventoryMessage)msg);
            }
        }

        private void HandleWorldObjectState(CSteamID sender, WorldObjectStateMessage msg)
        {
            Plugin.Log.LogInfo($"WorldStateSync: HandleWorldObjectState type={msg.ObjectType} pos=({msg.PosX:F1},{msg.PosY:F1},{msg.PosZ:F1}) isOpen={msg.IsOpen}");

            var net = NetworkManager.Instance;
            if (net == null || !net.IsConnected) return;

            // Host relay: if a client sent this, forward to all OTHER clients (not back to sender)
            if (net.IsHost)
                net.SendToAllExcept(sender, msg);

            var pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);

            switch (msg.ObjectType)
            {
                case ObjTypeDoor:
                    ApplyDoorState(pos, msg.IsOpen);
                    break;
                case ObjTypeGate:
                    ApplyGateState(pos, msg.IsOpen);
                    break;
                case ObjTypeBreakable:
                    ApplyBreakableState(pos);
                    break;
                case ObjTypeChallengeDoor:
                    ApplyChallengeDoorState(pos, msg.IsOpen);
                    break;
                case ObjTypeElevator:
                    ApplyElevatorState(pos, msg.IsOpen);
                    break;
                default:
                    Plugin.Log.LogWarning($"WorldStateSync: Unknown object type {msg.ObjectType}");
                    break;
            }
        }

        private void ApplyDoorState(Vector3 pos, bool isOpen)
        {
            if (_openableDoorType == null) return;

            var door = FindNearestComponent(_openableDoorType, pos, 3f);
            if (door == null)
            {
                Plugin.Log.LogWarning($"WorldStateSync: No door found near {pos}");
                return;
            }

            _isNetworkStateChange = true;
            try
            {
                if (isOpen)
                {
                    // Opening: set isClosed=false, then call Open()
                    _doorIsClosedField?.SetValue(door, false);
                    _doorOpenMethod?.Invoke(door, null);
                }
                else
                {
                    // Closing: set isClosed=true, isMoving=true, call Open() (drives close animation)
                    _doorIsClosedField?.SetValue(door, true);
                    _doorIsMovingField?.SetValue(door, true);
                    _doorOpenMethod?.Invoke(door, null);

                    // Play close sound — Open() skips it when isClosed=true
                    PlayDoorCloseSound(door);
                }

                Plugin.Log.LogInfo($"WorldStateSync: Applied door state isOpen={isOpen} at {pos}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"WorldStateSync: ApplyDoorState failed: {ex.Message}");
            }
            finally
            {
                _isNetworkStateChange = false;
            }
        }

        private void ApplyGateState(Vector3 pos, bool isOpen)
        {
            if (_metalGateType == null) return;

            var gate = FindNearestComponent(_metalGateType, pos, 5f);
            if (gate == null)
            {
                Plugin.Log.LogWarning($"WorldStateSync: No gate found near {pos}");
                return;
            }

            _isNetworkStateChange = true;
            try
            {
                if (isOpen)
                    _metalGateOpenMethod?.Invoke(gate, null);
                else
                    _metalGateCloseMethod?.Invoke(gate, null);

                Plugin.Log.LogInfo($"WorldStateSync: Applied gate state isOpen={isOpen} at {pos}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"WorldStateSync: ApplyGateState failed: {ex.Message}");
            }
            finally
            {
                _isNetworkStateChange = false;
            }
        }

        private void ApplyBreakableState(Vector3 pos)
        {
            if (_breakableType == null) return;

            var breakable = FindNearestComponent(_breakableType, pos, 10f);
            if (breakable == null)
            {
                // Count how many breakables exist for diagnostics
                var allBreakables = UnityEngine.Object.FindObjectsOfType(_breakableType);
                Plugin.Log.LogWarning($"WorldStateSync: No breakable found within 10m of {pos} (already destroyed or out of range). Total active breakables: {allBreakables.Length}");
                return;
            }

            float matchDist = Vector3.Distance(((Component)breakable).transform.position, pos);
            string matchName = ((Component)breakable).gameObject.name;

            // Check if already dead
            if (!IsUnitAlive(breakable))
            {
                Plugin.Log.LogWarning($"WorldStateSync: Breakable '{matchName}' at {pos} already dead on receive, skipping Die()");
                return;
            }

            _isNetworkStateChange = true;
            try
            {
                _breakableDieMethod?.Invoke(breakable, null);
                Plugin.Log.LogInfo($"WorldStateSync: Applied breakable death name={matchName} dist={matchDist:F2}m at {pos}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"WorldStateSync: ApplyBreakableState failed: {ex.Message}");
            }
            finally
            {
                _isNetworkStateChange = false;
            }
        }

        private void ApplyChallengeDoorState(Vector3 pos, bool isOpen)
        {
            if (_challengeControlHelperType == null) return;

            var door = FindNearestComponent(_challengeControlHelperType, pos, 3f);
            if (door == null)
            {
                Plugin.Log.LogWarning($"WorldStateSync: No ChallengeControlHelper found near {pos}");
                return;
            }

            _isNetworkStateChange = true;
            try
            {
                var method = isOpen ? _openChallengeDoorMethod : _closeChallengeDoorMethod;
                method?.Invoke(door, null);
                Plugin.Log.LogInfo($"WorldStateSync: Applied challenge door state isOpen={isOpen} at {pos}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"WorldStateSync: ApplyChallengeDoorState failed: {ex.Message}");
            }
            finally
            {
                _isNetworkStateChange = false;
            }
        }

        private void ApplyElevatorState(Vector3 pos, bool isUp)
        {
            if (_cousinElevatorType == null) return;

            var elevator = FindNearestComponent(_cousinElevatorType, pos, 5f);
            if (elevator == null)
            {
                Plugin.Log.LogWarning($"WorldStateSync: No CousinElevator found near {pos}");
                return;
            }

            _isNetworkStateChange = true;
            try
            {
                var method = isUp ? _elevatorGoUpMethod : _elevatorGoDownMethod;
                method?.Invoke(elevator, null);
                Plugin.Log.LogInfo($"WorldStateSync: Applied elevator state isUp={isUp} at {pos}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"WorldStateSync: ApplyElevatorState failed: {ex.Message}");
            }
            finally
            {
                _isNetworkStateChange = false;
            }
        }

        private void OnDisconnected(string reason)
        {
            _isNetworkStateChange = false;
        }

        private Coroutine _breakableInventoryCoroutine;

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            var net = NetworkManager.Instance;
            if (net == null || !net.IsConnected) return;

            // Only host sends breakable inventory
            if (net.IsHost)
            {
                if (_breakableInventoryCoroutine != null)
                    StopCoroutine(_breakableInventoryCoroutine);
                _breakableInventoryCoroutine = StartCoroutine(HostSendBreakableInventoryCoroutine());
            }
        }

        /// <summary>
        /// Host waits for breakable count to stabilize after level gen, then sends
        /// the full breakable inventory to all clients for reconciliation.
        /// </summary>
        private IEnumerator HostSendBreakableInventoryCoroutine()
        {
            InitReflection();
            if (_breakableType == null)
            {
                Plugin.Log.LogWarning("WorldStateSync: Breakable type not found, cannot send inventory");
                yield break;
            }

            Plugin.Log.LogInfo("WorldStateSync [Host]: Waiting for breakables to stabilize...");

            float waited = 0f;
            const float maxWait = 30f;
            const float checkInterval = 0.5f;
            int lastCount = 0;
            float stableTime = 0f;
            const float stableThreshold = 1.5f;

            // Initial delay — let scene transition start
            yield return new WaitForSecondsRealtime(1f);
            waited += 1f;

            while (waited < maxWait)
            {
                yield return new WaitForSecondsRealtime(checkInterval);
                waited += checkInterval;

                var all = UnityEngine.Object.FindObjectsOfType(_breakableType);
                int count = all.Length;

                if (count > 0)
                {
                    if (count == lastCount)
                    {
                        stableTime += checkInterval;
                        if (stableTime >= stableThreshold)
                        {
                            Plugin.Log.LogInfo($"WorldStateSync [Host]: Breakable count stabilized at {count} after {waited:F1}s");
                            break;
                        }
                    }
                    else
                    {
                        stableTime = 0f;
                        lastCount = count;
                    }
                }

                if (waited % 5f < checkInterval)
                    Plugin.Log.LogInfo($"WorldStateSync [Host]: Still waiting for breakables... ({waited:F1}s, count={count})");
            }

            _breakableInventoryCoroutine = null;

            // Build and send inventory
            var breakables = UnityEngine.Object.FindObjectsOfType(_breakableType);
            var msg = new BreakableInventoryMessage();

            foreach (var obj in breakables)
            {
                if (obj is Component comp && comp != null)
                {
                    var p = comp.transform.position;
                    msg.Entries.Add(new BreakableInventoryMessage.BreakableEntry
                    {
                        X = p.x, Y = p.y, Z = p.z
                    });
                }
            }

            var net = NetworkManager.Instance;
            if (net != null && net.IsConnected && net.IsHost)
            {
                net.SendToAll(msg);
                Plugin.Log.LogInfo($"WorldStateSync [Host]: Sent BreakableInventory with {msg.Entries.Count} entries to clients");
            }
        }

        /// <summary>
        /// Client receives host's breakable inventory and destroys any local extras.
        /// </summary>
        private void HandleBreakableInventory(BreakableInventoryMessage msg)
        {
            var net = NetworkManager.Instance;
            if (net == null || net.IsHost) return; // Only clients reconcile

            InitReflection();
            if (_breakableType == null || _breakableDieMethod == null) return;

            var localBreakables = UnityEngine.Object.FindObjectsOfType(_breakableType);
            int matched = 0;
            int destroyed = 0;

            foreach (var obj in localBreakables)
            {
                if (!(obj is Component comp) || comp == null) continue;
                if (!IsUnitAlive(comp)) continue;

                var localPos = comp.transform.position;
                bool found = false;

                for (int i = 0; i < msg.Entries.Count; i++)
                {
                    var entry = msg.Entries[i];
                    float dx = localPos.x - entry.X;
                    float dy = localPos.y - entry.Y;
                    float dz = localPos.z - entry.Z;
                    float distSq = dx * dx + dy * dy + dz * dz;

                    if (distSq <= 1f) // 1m tolerance
                    {
                        found = true;
                        matched++;
                        break;
                    }
                }

                if (!found)
                {
                    // This breakable doesn't exist on host — destroy it
                    _isNetworkStateChange = true;
                    try
                    {
                        _breakableDieMethod.Invoke(comp, null);
                        destroyed++;
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning($"WorldStateSync: Failed to reconcile breakable '{comp.gameObject.name}': {ex.Message}");
                    }
                    finally
                    {
                        _isNetworkStateChange = false;
                    }
                }
            }

            Plugin.Log.LogInfo($"WorldStateSync [Client]: BreakableInventory reconciled — matched {matched}, destroyed {destroyed} extras (host had {msg.Entries.Count})");
        }

        #endregion

        #region Helpers

        private static void BroadcastWorldState(WorldObjectStateMessage msg)
        {
            var net = NetworkManager.Instance;
            if (net == null) return;

            if (net.IsHost)
                net.SendToAll(msg);
            else
            {
                // Client sends to host, host relays to other clients
                var hostId = LobbyManager.Instance?.HostSteamId ?? CSteamID.Nil;
                if (hostId != CSteamID.Nil)
                    net.SendMessage(hostId, msg);
            }
        }

        /// <summary>
        /// Find the nearest component of a given type within maxDistance of pos.
        /// Uses Object.FindObjectsOfType (active scene objects only — excludes prefabs by design).
        /// </summary>
        private static Component FindNearestComponent(Type componentType, Vector3 pos, float maxDistance)
        {
            var all = UnityEngine.Object.FindObjectsOfType(componentType);
            Component nearest = null;
            float nearestDist = float.MaxValue;

            foreach (var obj in all)
            {
                if (!(obj is Component comp) || comp == null)
                    continue;

                float dist = Vector3.Distance(comp.transform.position, pos);
                if (dist < nearestDist && dist <= maxDistance)
                {
                    nearestDist = dist;
                    nearest = comp;
                }
            }

            return nearest;
        }

        private static bool IsUnitAlive(object unit)
        {
            if (unit == null || _unitStateField == null || _unitStateDead == null)
                return false;

            try
            {
                var state = _unitStateField.GetValue(unit);
                return !Equals(state, _unitStateDead);
            }
            catch
            {
                return false;
            }
        }

        private static void PlayDoorCloseSound(object door)
        {
            if (_doorSoundCloseField == null || _soundEventPlayMethod == null)
                return;

            try
            {
                var sound = _doorSoundCloseField.GetValue(door);
                if (sound == null && _doorSoundOpenField != null)
                    sound = _doorSoundOpenField.GetValue(door);

                if (sound != null && door is Component comp && comp != null)
                    _soundEventPlayMethod.Invoke(sound, new object[] { comp.transform });
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"WorldStateSync: PlayDoorCloseSound failed: {ex.Message}");
            }
        }

        #endregion

        #region Reflection

        private static void InitReflection()
        {
            if (_reflectionInit) return;
            _reflectionInit = true;

            Type soundEventType = null;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (_openableDoorType == null)
                        _openableDoorType = asm.GetType("PerfectRandom.Sulfur.Core.OpenableDoor");
                    if (_metalGateType == null)
                        _metalGateType = asm.GetType("PerfectRandom.Sulfur.Gameplay.Mechanisms.MetalGate.MetalGate");
                    if (_breakableType == null)
                        _breakableType = asm.GetType("PerfectRandom.Sulfur.Core.Units.Breakable");
                    if (_unitType == null)
                        _unitType = asm.GetType("PerfectRandom.Sulfur.Core.Units.Unit");
                    // UnitState type no longer needed as separate field — derived from field type
                    if (_playerType == null)
                        _playerType = asm.GetType("PerfectRandom.Sulfur.Core.Units.Player");
                    if (soundEventType == null)
                        soundEventType = asm.GetType("Sonity.SoundEvent");
                    if (_challengeControlHelperType == null)
                        _challengeControlHelperType = asm.GetType("PerfectRandom.Sulfur.Core.ChallengeControlHelper");
                    if (_cousinElevatorType == null)
                        _cousinElevatorType = asm.GetType("PerfectRandom.Sulfur.Gameplay.CousinElevator");
                }
                catch (ReflectionTypeLoadException)
                {
                    // Some assemblies throw on GetTypes — skip
                }

                if (_openableDoorType != null && _metalGateType != null && _breakableType != null &&
                    _unitType != null && _playerType != null && soundEventType != null &&
                    _challengeControlHelperType != null && _cousinElevatorType != null)
                    break;
            }

            // OpenableDoor reflection
            if (_openableDoorType != null)
            {
                var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                _doorOpenedField = _openableDoorType.GetField("opened", flags);
                _doorIsClosedField = _openableDoorType.GetField("isClosed", flags);
                _doorIsMovingField = _openableDoorType.GetField("isMoving", flags);
                _doorIsCloseableField = _openableDoorType.GetField("isCloseable", flags);
                _doorSoundCloseField = _openableDoorType.GetField("soundClose", flags);
                _doorSoundOpenField = _openableDoorType.GetField("soundOpen", flags);
                _doorOpenMethod = _openableDoorType.GetMethod("Open", flags);

                // OnInteract is public override
                if (_playerType != null)
                {
                    _doorOnInteractMethod = _openableDoorType.GetMethod("OnInteract",
                        BindingFlags.Public | BindingFlags.Instance,
                        null, new[] { _playerType }, null);
                }

                if (_doorOnInteractMethod != null)
                    Plugin.Log.LogInfo("WorldStateSync: Found OpenableDoor.OnInteract");
                else
                    Plugin.Log.LogWarning("WorldStateSync: Could not find OpenableDoor.OnInteract");

                if (_doorOpenMethod != null)
                    Plugin.Log.LogInfo("WorldStateSync: Found OpenableDoor.Open (private)");
            }
            else
            {
                Plugin.Log.LogWarning("WorldStateSync: OpenableDoor type not found");
            }

            // MetalGate reflection
            if (_metalGateType != null)
            {
                _metalGateOpenMethod = _metalGateType.GetMethod("Open",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                _metalGateCloseMethod = _metalGateType.GetMethod("Close",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);

                if (_metalGateOpenMethod != null && _metalGateCloseMethod != null)
                    Plugin.Log.LogInfo("WorldStateSync: Found MetalGate.Open + Close");
                else
                    Plugin.Log.LogWarning("WorldStateSync: MetalGate methods not fully found");
            }
            else
            {
                Plugin.Log.LogWarning("WorldStateSync: MetalGate type not found");
            }

            // ChallengeControlHelper reflection
            if (_challengeControlHelperType != null)
            {
                _openChallengeDoorMethod = _challengeControlHelperType.GetMethod("OpenChallengeDoor",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                _closeChallengeDoorMethod = _challengeControlHelperType.GetMethod("CloseChallengeDoor",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);

                if (_openChallengeDoorMethod != null && _closeChallengeDoorMethod != null)
                    Plugin.Log.LogInfo("WorldStateSync: Found ChallengeControlHelper.OpenChallengeDoor + CloseChallengeDoor");
                else
                    Plugin.Log.LogWarning("WorldStateSync: ChallengeControlHelper methods not fully found");
            }
            else
            {
                Plugin.Log.LogWarning("WorldStateSync: ChallengeControlHelper type not found");
            }

            // CousinElevator reflection
            if (_cousinElevatorType != null)
            {
                _elevatorGoUpMethod = _cousinElevatorType.GetMethod("GoUp",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                _elevatorGoDownMethod = _cousinElevatorType.GetMethod("GoDown",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);

                if (_elevatorGoUpMethod != null && _elevatorGoDownMethod != null)
                    Plugin.Log.LogInfo("WorldStateSync: Found CousinElevator.GoUp + GoDown");
                else
                    Plugin.Log.LogWarning("WorldStateSync: CousinElevator methods not fully found");
            }
            else
            {
                Plugin.Log.LogWarning("WorldStateSync: CousinElevator type not found");
            }

            // Breakable reflection — keep Die method for invocation in ApplyBreakableState
            if (_breakableType != null)
            {
                _breakableDieMethod = _breakableType.GetMethod("Die",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);

                if (_breakableDieMethod != null)
                    Plugin.Log.LogInfo("WorldStateSync: Found Breakable.Die (for invocation)");
                else
                    Plugin.Log.LogWarning("WorldStateSync: Breakable.Die not found");
            }
            else
            {
                Plugin.Log.LogWarning("WorldStateSync: Breakable type not found");
            }

            // Unit reflection — Die method for hooking + unitState field for alive check
            if (_unitType != null)
            {
                _unitDieMethod = _unitType.GetMethod("Die",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);

                if (_unitDieMethod != null)
                    Plugin.Log.LogInfo("WorldStateSync: Found Unit.Die (for hooking)");
                else
                    Plugin.Log.LogWarning("WorldStateSync: Unit.Die not found");

                _unitStateField = _unitType.GetField("unitState",
                    BindingFlags.Public | BindingFlags.Instance);

                if (_unitStateField != null)
                {
                    try
                    {
                        _unitStateDead = Enum.Parse(_unitStateField.FieldType, "Dead");
                        Plugin.Log.LogInfo($"WorldStateSync: Resolved UnitState.Dead = {_unitStateDead}");
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning($"WorldStateSync: Failed to resolve UnitState.Dead: {ex.Message}");
                    }
                }
                else
                {
                    Plugin.Log.LogWarning("WorldStateSync: Unit.unitState field not found");
                }
            }

            // SoundEvent.Play(Transform)
            if (soundEventType != null)
            {
                _soundEventPlayMethod = soundEventType.GetMethod("Play",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(Transform) }, null);

                if (_soundEventPlayMethod != null)
                    Plugin.Log.LogInfo("WorldStateSync: Found SoundEvent.Play(Transform)");
            }
        }

        #endregion
    }
}
