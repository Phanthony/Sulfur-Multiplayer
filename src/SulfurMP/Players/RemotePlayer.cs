using System;
using System.Reflection;
using SulfurMP.Config;
using SulfurMP.Networking;
using SulfurMP.Networking.Messages;
using UnityEngine;

namespace SulfurMP.Players
{
    /// <summary>
    /// Represents a remote player as a colored capsule with a name label.
    /// Smoothly interpolated from network snapshots.
    /// </summary>
    public class RemotePlayer : MonoBehaviour
    {
        public ulong SteamId { get; private set; }
        public string PlayerName { get; private set; }

        /// <summary>Current interpolated pitch (vertical look angle) for spectator camera.</summary>
        public float CurrentPitch { get; private set; }

        /// <summary>
        /// Head-height transform for LOS raycasts (y=1.6 above feet).
        /// Used by EnemyAISyncManager for Physics.Linecast from NPC to remote player head.
        /// Only created on host.
        /// </summary>
        public Transform CameraRoot { get; private set; }

        private NetworkInterpolation _interpolation;
        private TextMesh _nameLabel;

        // Capsule visual refs (set in Create)
        private Transform _capsule;
        private MeshRenderer _capsuleRenderer;
        private Color _baseColor;

        // Visual state tracking
        private byte _lastHealth = 255;
        private float _flashTimer;
        private float _bobPhase;
        private bool _wasGrounded = true;
        private float _landingTimer;

        // Standing capsule defaults
        private const float StandScaleY = 0.9f;
        private const float StandScaleXZ = 0.6f;
        private const float StandOffsetY = 0.9f;
        private const float CrouchScaleY = 0.5f;
        private const float CrouchOffsetY = 0.5f;
        private const float SprintTiltAngle = 12f;
        private const float LandingDuration = 0.2f;
        private const float LandingSquashY = 0.7f;
        private const float LandingExpandXZ = 0.75f;
        private const float BobAmplitude = 0.03f;
        private const float BobSpeedScale = 8f;
        private const float FlashDuration = 0.15f;
        private const float LerpSpeed = 10f;

        /// <summary>
        /// Create a remote player capsule at the given position.
        /// Built on an inactive GO so we control Awake ordering:
        /// Unit added before Hitbox so Hitbox.Awake() finds Unit via GetComponentInParent.
        /// </summary>
        public static RemotePlayer Create(ulong steamId, string playerName, Vector3 spawnPos)
        {
            // Root GO at feet level — drives position/rotation, holds RemotePlayer + Unit + Marker
            var go = new GameObject($"RemotePlayer_{steamId}");
            go.SetActive(false); // Keep inactive until fully configured
            go.transform.position = spawnPos;

            // Visual capsule as child — offset so bottom is at feet level
            // Default capsule mesh: height=2, radius=0.5, centered at origin
            // Scale (0.6, 0.9, 0.6) → effective height 1.8m, width 0.6m
            // LocalPosition y=0.9 → bottom at feet (0.9 - 0.9 = 0), top at 1.8m
            var capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            capsule.name = "Body";
            capsule.transform.SetParent(go.transform, false);
            capsule.transform.localPosition = new Vector3(0f, 0.9f, 0f);
            capsule.transform.localScale = new Vector3(0.6f, 0.9f, 0.6f);

            var net = NetworkManager.Instance;
            bool isHost = net != null && net.IsHost;

            RemotePlayerMarker marker = null;

            if (isHost)
            {
                // Host: capsule child has collider as trigger on Hitbox layer
                capsule.layer = LayerMask.NameToLayer("Hitbox");
                var collider = capsule.GetComponent<Collider>();
                if (collider != null)
                    collider.isTrigger = true;

                // Add Unit to ROOT — Hitbox.Awake() on child calls GetComponentInParent<Unit>()
                // which traverses up to root and finds it
                var unitComp = AddGameUnit(go);

                // Add Hitbox to CAPSULE CHILD (has the CapsuleCollider) — Owner set via GetComponentInParent
                AddGameHitbox(capsule);

                // Marker on root so CombatSyncManager can identify this as a remote player
                marker = go.AddComponent<RemotePlayerMarker>();
                marker.SteamId = steamId;
                marker.UnitComponent = unitComp;

                // Track Unit in RegisteredUnits for NPC target resolution
                if (unitComp != null)
                    RemotePlayerMarker.RegisteredUnits.Add(unitComp);
            }
            else
            {
                // Client: remove collider on capsule child, ignore raycast layer on root
                go.layer = 2; // Ignore Raycast
                var collider = capsule.GetComponent<Collider>();
                if (collider != null)
                    UnityEngine.Object.Destroy(collider);
            }

            // Activate — triggers Awake() on Unit (safe: just GetComponent calls)
            // and Hitbox on capsule child (finds Unit on root via GetComponentInParent → sets Owner)
            go.SetActive(true);

            // Create CameraRoot as child of root (head-height LOS target point)
            // y=1.6 from feet — correct eye height for ~1.8m player
            Transform cameraRoot = null;
            if (isHost)
            {
                var cameraRootGo = new GameObject("CameraRoot");
                cameraRootGo.transform.SetParent(go.transform, false);
                cameraRootGo.transform.localPosition = new Vector3(0f, 1.6f, 0f);
                cameraRoot = cameraRootGo.transform;
            }

            // Find a working URP material by borrowing a shader from an existing scene renderer
            var capsuleRenderer = capsule.GetComponent<MeshRenderer>();
            if (capsuleRenderer != null)
            {
                var color = GetColorForSteamId(steamId);
                var mat = FindOrCreateURPMaterial(color);
                if (mat != null)
                    capsuleRenderer.material = mat;
            }

            // Create name label as child of root — above the 1.8m capsule top
            var labelGo = new GameObject("NameLabel");
            labelGo.transform.SetParent(go.transform, false);
            labelGo.transform.localPosition = new Vector3(0f, 2.0f, 0f);

            var textMesh = labelGo.AddComponent<TextMesh>();
            textMesh.text = playerName;
            textMesh.characterSize = 0.05f;
            textMesh.fontSize = 48;
            textMesh.anchor = TextAnchor.MiddleCenter;
            textMesh.alignment = TextAlignment.Center;
            textMesh.color = Color.white;

            // Add the RemotePlayer component
            var remote = go.AddComponent<RemotePlayer>();
            remote.SteamId = steamId;
            remote.PlayerName = playerName;
            remote._nameLabel = textMesh;
            remote._interpolation = new NetworkInterpolation(MultiplayerConfig.InterpolationDelay.Value);
            remote.CameraRoot = cameraRoot;
            remote._capsule = capsule.transform;
            remote._capsuleRenderer = capsuleRenderer;
            remote._baseColor = capsuleRenderer != null ? capsuleRenderer.material.color : Color.white;

            Plugin.Log.LogInfo($"Created remote player: {playerName} ({steamId}) at {spawnPos}" +
                (isHost ? " [with Unit component]" : ""));
            return remote;
        }

        private static Shader _cachedShader;

        private static Material FindOrCreateURPMaterial(Color color)
        {
            // Cache the shader once — find it from any existing visible renderer in the scene
            if (_cachedShader == null)
            {
                // Search all loaded renderers for a non-Standard shader (i.e., a real URP shader)
                var allRenderers = Resources.FindObjectsOfTypeAll<MeshRenderer>();
                foreach (var r in allRenderers)
                {
                    if (r == null || r.sharedMaterial == null || r.sharedMaterial.shader == null)
                        continue;
                    var shaderName = r.sharedMaterial.shader.name;
                    if (shaderName.Contains("Universal") || shaderName.Contains("URP") || shaderName.Contains("Lit"))
                    {
                        _cachedShader = r.sharedMaterial.shader;
                        Plugin.Log.LogInfo($"Borrowed URP shader from scene: {shaderName}");
                        break;
                    }
                }

                // If still nothing, try Shader.Find as last resort
                if (_cachedShader == null)
                {
                    _cachedShader = Shader.Find("Universal Render Pipeline/Lit")
                                 ?? Shader.Find("Universal Render Pipeline/Simple Lit")
                                 ?? Shader.Find("Universal Render Pipeline/Unlit");
                }

                if (_cachedShader == null)
                    Plugin.Log.LogError("Could not find any URP shader!");
                else
                    Plugin.Log.LogInfo($"Using shader: {_cachedShader.name}");
            }

            if (_cachedShader == null)
                return null;

            var mat = new Material(_cachedShader);
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color"))
                mat.SetColor("_Color", color);
            mat.color = color;
            return mat;
        }

        private static Color GetColorForSteamId(ulong steamId)
        {
            var hash = (int)(steamId ^ (steamId >> 32));
            float hue = Mathf.Abs(hash % 360) / 360f;
            return Color.HSVToRGB(hue, 0.7f, 0.9f);
        }

        /// <summary>
        /// Push a new state snapshot into the interpolation buffer.
        /// </summary>
        public void PushSnapshot(PlayerStateMessage msg)
        {
            _interpolation.AddSnapshot(new Snapshot
            {
                Timestamp = msg.Timestamp,
                Position = msg.GetPosition(),
                Yaw = msg.Yaw,
                Pitch = msg.Pitch,
                Velocity = msg.GetVelocity(),
                AnimationState = msg.AnimationState,
                IsGrounded = msg.IsGrounded,
                Health = msg.Health,
            }, Time.unscaledTime);
        }

        private void Update()
        {
            // Sample interpolation buffer
            if (!_interpolation.Sample(Time.unscaledTime, out var state))
                return;

            transform.position = state.Position;
            transform.rotation = Quaternion.Euler(0f, state.Yaw, 0f);
            CurrentPitch = state.Pitch;

            float dt = Time.deltaTime;

            if (_capsule != null)
            {
                bool isSprinting = (state.AnimationState & 1) != 0;
                bool isCrouching = (state.AnimationState & 2) != 0;
                bool isGrounded = state.IsGrounded;
                float speed = state.Velocity.magnitude;

                // --- Crouch ---
                float targetScaleY = isCrouching ? CrouchScaleY : StandScaleY;
                float targetOffsetY = isCrouching ? CrouchOffsetY : StandOffsetY;

                // --- Landing squash detection ---
                if (isGrounded && !_wasGrounded)
                    _landingTimer = LandingDuration;
                _wasGrounded = isGrounded;

                float scaleXZ = StandScaleXZ;
                if (_landingTimer > 0f)
                {
                    _landingTimer -= dt;
                    float t = Mathf.Clamp01(_landingTimer / LandingDuration);
                    targetScaleY = Mathf.Lerp(targetScaleY, LandingSquashY, t);
                    scaleXZ = Mathf.Lerp(StandScaleXZ, LandingExpandXZ, t);
                }

                // Apply scale with smooth lerp
                var curScale = _capsule.localScale;
                curScale.x = Mathf.Lerp(curScale.x, scaleXZ, dt * LerpSpeed);
                curScale.y = Mathf.Lerp(curScale.y, targetScaleY, dt * LerpSpeed);
                curScale.z = Mathf.Lerp(curScale.z, scaleXZ, dt * LerpSpeed);
                _capsule.localScale = curScale;

                // --- Walk bob ---
                float bobOffset = 0f;
                if (isGrounded && speed > 0.5f)
                {
                    _bobPhase += dt * speed * BobSpeedScale;
                    bobOffset = Mathf.Sin(_bobPhase) * BobAmplitude;
                }
                else
                {
                    // Decay phase to 0 so we don't pop when stopping
                    _bobPhase = Mathf.Lerp(_bobPhase, 0f, dt * 5f);
                }

                // Apply offset with smooth lerp
                float curOffsetY = _capsule.localPosition.y;
                float finalOffsetY = targetOffsetY + bobOffset;
                curOffsetY = Mathf.Lerp(curOffsetY, finalOffsetY, dt * LerpSpeed);
                _capsule.localPosition = new Vector3(0f, curOffsetY, 0f);

                // --- Sprint tilt ---
                float targetTilt = isSprinting ? SprintTiltAngle : 0f;
                var curRot = _capsule.localRotation;
                var targetRot = Quaternion.Euler(targetTilt, 0f, 0f);
                _capsule.localRotation = Quaternion.Lerp(curRot, targetRot, dt * LerpSpeed);
            }

            // --- Damage flash ---
            if (_capsuleRenderer != null)
            {
                if (state.Health < _lastHealth)
                    _flashTimer = FlashDuration;
                _lastHealth = state.Health;

                Color targetColor;
                if (_flashTimer > 0f)
                {
                    _flashTimer -= Time.deltaTime;
                    targetColor = Color.red;
                }
                else
                {
                    targetColor = _baseColor;
                }

                var mat = _capsuleRenderer.material;
                Color curColor = mat.color;
                Color newColor = Color.Lerp(curColor, targetColor, Time.deltaTime * LerpSpeed);
                mat.color = newColor;
                if (mat.HasProperty("_BaseColor"))
                    mat.SetColor("_BaseColor", newColor);
            }

            // Billboard name label toward camera
            if (_nameLabel != null && Camera.main != null)
            {
                _nameLabel.transform.rotation = Quaternion.LookRotation(
                    _nameLabel.transform.position - Camera.main.transform.position);
            }
        }

        private static Type _unitType;
        private static Type _hitboxType;
        private static Type _unitStateType;
        private static Type _factionIdsType;

        // Cached reflection for Unit fields
        private static FieldInfo _isPlayerField;       // Unit.isPlayer (public bool field)
        private static FieldInfo _unitStateField;      // Unit.unitState (public UnitState field)
        private static PropertyInfo _overriddenFactionIdProp; // Unit.overriddenFactionId { get; set; }
        private static object _unitStateAlive;         // UnitState.Alive (enum value = 2)
        private static object _factionIdsPlayer;       // FactionIds.Player (enum value = 16)
        private static bool _unitReflectionInit;

        private static void InitUnitReflection()
        {
            if (_unitReflectionInit) return;
            _unitReflectionInit = true;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (_unitType == null)
                    _unitType = asm.GetType("PerfectRandom.Sulfur.Core.Units.Unit");
                if (_unitStateType == null)
                    _unitStateType = asm.GetType("PerfectRandom.Sulfur.Core.Units.UnitState");
                if (_factionIdsType == null)
                    _factionIdsType = asm.GetType("FactionIds");
                if (_factionIdsType == null)
                    _factionIdsType = asm.GetType("PerfectRandom.Sulfur.Core.FactionIds");

                if (_unitType != null && _unitStateType != null && _factionIdsType != null)
                    break;
            }

            if (_unitType != null)
            {
                _isPlayerField = _unitType.GetField("isPlayer",
                    BindingFlags.Public | BindingFlags.Instance);
                _unitStateField = _unitType.GetField("unitState",
                    BindingFlags.Public | BindingFlags.Instance);
                _overriddenFactionIdProp = _unitType.GetProperty("overriddenFactionId",
                    BindingFlags.Public | BindingFlags.Instance);
            }

            if (_unitStateType != null)
            {
                try { _unitStateAlive = Enum.Parse(_unitStateType, "Alive"); }
                catch { Plugin.Log.LogWarning("RemotePlayer: Could not resolve UnitState.Alive"); }
            }

            if (_factionIdsType != null)
            {
                try { _factionIdsPlayer = Enum.Parse(_factionIdsType, "Player"); }
                catch { Plugin.Log.LogWarning("RemotePlayer: Could not resolve FactionIds.Player"); }
            }
        }

        /// <summary>
        /// Add a Unit component to the remote player capsule (host only).
        /// Configured so the AI system treats it as a valid targetable unit:
        /// - isPlayer = false (prevents GetPositionToAimAt crash on PlayerScript)
        /// - overriddenFactionId = Player (AI sees it as hostile target in Player faction)
        /// - unitState = Alive (default Dead=0 would make AI ignore it)
        /// - enabled = false (prevents Start() which crashes on unitSO.name)
        /// </summary>
        private static object AddGameUnit(GameObject go)
        {
            InitUnitReflection();

            if (_unitType == null)
            {
                Plugin.Log.LogWarning("RemotePlayer: Could not find Unit type");
                return null;
            }

            var unit = go.AddComponent(_unitType);

            // isPlayer = false — prevents GetPositionToAimAt() from accessing PlayerScript.cameraControls
            if (_isPlayerField != null)
                _isPlayerField.SetValue(unit, false);

            // overriddenFactionId = Player (16) — HasOverriddenFaction auto-computes true
            if (_overriddenFactionIdProp != null && _factionIdsPlayer != null)
                _overriddenFactionIdProp.SetValue(unit, _factionIdsPlayer);

            // unitState = Alive (2) — default is Dead (0), AI ignores dead units
            if (_unitStateField != null && _unitStateAlive != null)
                _unitStateField.SetValue(unit, _unitStateAlive);

            // Disable to prevent Start() from running (crashes on unitSO.name)
            // Awake() is safe (just GetComponent calls for collider/animator/rigidbody)
            if (unit is Behaviour behaviour)
                behaviour.enabled = false;

            Plugin.Log.LogInfo($"RemotePlayer: Added Unit component to {go.name} " +
                $"(unitState=Alive, faction=Player, isPlayer=false, enabled=false)");

            return unit;
        }

        private static void AddGameHitbox(GameObject go)
        {
            if (_hitboxType == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    _hitboxType = asm.GetType("PerfectRandom.Sulfur.Core.Units.Hitbox");
                    if (_hitboxType != null) break;
                }
            }

            if (_hitboxType != null)
            {
                go.AddComponent(_hitboxType);
                Plugin.Log.LogInfo($"RemotePlayer: Added Hitbox component to {go.name}");
            }
            else
            {
                Plugin.Log.LogWarning("RemotePlayer: Could not find Hitbox type");
            }
        }

        private void OnDestroy()
        {
            var net = NetworkManager.Instance;
            if (net != null && net.IsHost)
            {
                // Clean up RegisteredUnits
                var marker = GetComponent<RemotePlayerMarker>();
                if (marker?.UnitComponent != null)
                    RemotePlayerMarker.RegisteredUnits.Remove(marker.UnitComponent);
            }

            Plugin.Log.LogInfo($"Destroyed remote player: {PlayerName} ({SteamId})");
        }
    }
}
