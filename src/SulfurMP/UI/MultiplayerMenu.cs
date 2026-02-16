using SulfurMP.Config;
using SulfurMP.Networking;
using SulfurMP.Networking.Messages;
using SulfurMP.Players;
using Steamworks;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SulfurMP.UI
{
    /// <summary>
    /// IMGUI multiplayer menu (toggle with F2).
    /// Provides host/join/disconnect functionality.
    /// </summary>
    public class MultiplayerMenu : MonoBehaviour
    {
        private bool _visible = false;
        private string _joinSteamIdInput = "";
        private string _statusMessage = "";
        private float _statusTimer;
        private Vector2 _lobbyListScroll;

        // Debug fake players
        private readonly System.Collections.Generic.List<FakePlayerData> _fakePlayers = new System.Collections.Generic.List<FakePlayerData>();
        private int _fakePlayerCounter;

        private class FakePlayerData
        {
            public ulong FakeSteamId;
            public RemotePlayer Remote;
            public Vector3 Origin;
            public float OrbitAngle;
            public float OrbitRadius;
            public float OrbitSpeed;
        }

        private void Start()
        {
            NetworkEvents.OnConnected += () => SetStatus("Connected!");
            NetworkEvents.OnDisconnected += reason => SetStatus($"Disconnected: {reason}");
            NetworkEvents.OnLobbyCreated += id => SetStatus($"Lobby created: {id}");
            NetworkEvents.OnLobbyJoined += id => SetStatus($"Joined lobby: {id}");
        }

        private void Update()
        {
            if (Keyboard.current != null && Keyboard.current.f8Key.wasPressedThisFrame)
                _visible = !_visible;

            if (_statusTimer > 0)
                _statusTimer -= Time.unscaledDeltaTime;

            UpdateFakePlayers();
        }

        private void OnGUI()
        {
            if (!_visible) return;

            float width = 400f;
            float height = 520f;
            float x = (Screen.width - width) / 2f;
            float y = (Screen.height - height) / 2f;

            var bgStyle = new GUIStyle(GUI.skin.box) { normal = { background = MakeTex(1, 1, new Color(0.1f, 0.1f, 0.1f, 0.95f)) } };

            GUILayout.BeginArea(new Rect(x, y, width, height));
            GUILayout.BeginVertical(bgStyle);

            GUILayout.Label("<size=18><b>SulfurMP Multiplayer</b></size>", RichCentered());
            GUILayout.Space(10);

            var nm = NetworkManager.Instance;
            var lm = LobbyManager.Instance;

            if (nm == null || lm == null)
            {
                GUILayout.Label("Networking not initialized.", RichCentered());
                GUILayout.EndVertical();
                GUILayout.EndArea();
                return;
            }

            if (!nm.IsConnected)
            {
                DrawDisconnectedUI(lm);
            }
            else
            {
                DrawConnectedUI(nm, lm);
            }

            // Debug section — always available
            DrawDebugSection();

            // Status message
            if (_statusTimer > 0 && !string.IsNullOrEmpty(_statusMessage))
            {
                GUILayout.Space(10);
                GUILayout.Label($"<color=yellow>{_statusMessage}</color>", RichCentered());
            }

            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        private void DrawDisconnectedUI(LobbyManager lm)
        {
            GUILayout.Label("<b>Host a Game</b>", RichLabel());
            if (GUILayout.Button($"Create Lobby ({MultiplayerConfig.MaxPlayers.Value} players)", GUILayout.Height(35)))
            {
                lm.CreateLobby(MultiplayerConfig.MaxPlayers.Value);
                SetStatus("Creating lobby...");
            }

            GUILayout.Space(15);
            GUILayout.Label("<b>Join a Game</b>", RichLabel());

            // Join by Steam ID
            GUILayout.BeginHorizontal();
            GUILayout.Label("Steam ID:", GUILayout.Width(70));
            _joinSteamIdInput = GUILayout.TextField(_joinSteamIdInput);
            GUILayout.EndHorizontal();

            if (GUILayout.Button("Join by Steam ID", GUILayout.Height(30)))
            {
                if (ulong.TryParse(_joinSteamIdInput.Trim(), out ulong steamId))
                {
                    lm.JoinLobby(new CSteamID(steamId));
                    SetStatus("Joining...");
                }
                else
                {
                    SetStatus("Invalid Steam ID");
                }
            }

            GUILayout.Space(10);

            // Lobby browser
            GUILayout.Label("<b>Browse Lobbies</b>", RichLabel());
            if (GUILayout.Button(lm.IsSearching ? "Searching..." : "Refresh", GUILayout.Height(25)))
            {
                if (!lm.IsSearching)
                    lm.SearchLobbies();
            }

            DrawLobbyList(lm);
        }

        private void DrawLobbyList(LobbyManager lm)
        {
            var lobbies = lm.LobbySearchResults;
            if (lobbies.Count == 0)
            {
                GUILayout.Label("No lobbies found. Friends must be hosting.", GUI.skin.label);
                return;
            }

            _lobbyListScroll = GUILayout.BeginScrollView(_lobbyListScroll, GUILayout.Height(150));
            foreach (var lobbyId in lobbies)
            {
                var info = lm.GetLobbyInfo(lobbyId);
                GUILayout.BeginHorizontal(GUI.skin.box);
                GUILayout.Label($"{info.HostName} ({info.PlayerCount}/{info.MaxPlayers})");
                if (GUILayout.Button("Join", GUILayout.Width(60)))
                {
                    lm.JoinLobby(lobbyId);
                    SetStatus("Joining...");
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }

        private void DrawConnectedUI(NetworkManager nm, LobbyManager lm)
        {
            GUILayout.Label($"<b>Status:</b> {nm.State}", RichLabel());
            GUILayout.Label($"<b>Role:</b> {(nm.IsHost ? "Host" : "Client")}");
            GUILayout.Label($"<b>Peers:</b> {nm.ConnectedPeers.Count}");

            if (lm.InLobby)
            {
                GUILayout.Label($"<b>Lobby:</b> {lm.CurrentLobbyId}");
            }

            GUILayout.Space(10);

            // Peer list
            if (nm.ConnectedPeers.Count > 0)
            {
                GUILayout.Label("<b>Connected Players:</b>", RichLabel());
                foreach (var peer in nm.ConnectedPeers)
                {
                    string name = SteamFriends.GetFriendPersonaName(peer);
                    GUILayout.Label($"  {name} ({peer})");
                }
            }

            GUILayout.Space(15);

            GUI.color = new Color(1f, 0.4f, 0.4f);
            if (GUILayout.Button("Disconnect", GUILayout.Height(35)))
            {
                lm.LeaveLobby();
                SetStatus("Disconnected");
            }
            GUI.color = Color.white;
        }

        #region Debug Fake Players

        private void DrawDebugSection()
        {
            GUILayout.Space(10);
            GUILayout.Label("<b>Debug</b>", RichLabel());

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Spawn Fake Player", GUILayout.Height(25)))
                SpawnFakePlayer();

            GUI.enabled = _fakePlayers.Count > 0;
            if (GUILayout.Button("Remove All", GUILayout.Height(25), GUILayout.Width(90)))
                RemoveAllFakePlayers();
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (_fakePlayers.Count > 0)
                GUILayout.Label($"  Fake players: {_fakePlayers.Count} (orbiting around you)");
        }

        private void SpawnFakePlayer()
        {
            // Get local player position for spawn origin
            Vector3 spawnPos = Camera.main != null ? Camera.main.transform.position + Camera.main.transform.forward * 3f : Vector3.zero;

            _fakePlayerCounter++;
            ulong fakeSteamId = 76561190000000000UL + (ulong)_fakePlayerCounter;
            string fakeName = $"TestPlayer_{_fakePlayerCounter}";

            var remote = RemotePlayer.Create(fakeSteamId, fakeName, spawnPos);

            var data = new FakePlayerData
            {
                FakeSteamId = fakeSteamId,
                Remote = remote,
                Origin = spawnPos,
                OrbitAngle = Random.Range(0f, 360f),
                OrbitRadius = Random.Range(2f, 5f),
                OrbitSpeed = Random.Range(30f, 90f) * (Random.value > 0.5f ? 1f : -1f),
            };
            _fakePlayers.Add(data);

            SetStatus($"Spawned fake player: {fakeName}");
        }

        private void UpdateFakePlayers()
        {
            for (int i = _fakePlayers.Count - 1; i >= 0; i--)
            {
                var data = _fakePlayers[i];
                if (data.Remote == null)
                {
                    _fakePlayers.RemoveAt(i);
                    continue;
                }

                // Update orbit origin to follow local player
                if (Camera.main != null)
                    data.Origin = Camera.main.transform.position;

                data.OrbitAngle += data.OrbitSpeed * Time.unscaledDeltaTime;
                float rad = data.OrbitAngle * Mathf.Deg2Rad;
                var targetPos = data.Origin + new Vector3(Mathf.Cos(rad) * data.OrbitRadius, 0f, Mathf.Sin(rad) * data.OrbitRadius);

                // Feed synthetic snapshots through the real interpolation pipeline
                var msg = new PlayerStateMessage
                {
                    SteamId = data.FakeSteamId,
                    Timestamp = Time.unscaledTime,
                    Yaw = data.OrbitAngle + 90f, // Face direction of travel
                    Pitch = 0f,
                    AnimationState = 0,
                    IsGrounded = true,
                };
                msg.SetPosition(targetPos);
                msg.SetVelocity(Vector3.zero);

                data.Remote.PushSnapshot(msg);
            }
        }

        private void RemoveAllFakePlayers()
        {
            foreach (var data in _fakePlayers)
            {
                if (data.Remote != null && data.Remote.gameObject != null)
                    Object.Destroy(data.Remote.gameObject);
            }
            _fakePlayers.Clear();
            SetStatus("Removed all fake players");
        }

        private void OnDestroy()
        {
            RemoveAllFakePlayers();
        }

        #endregion

        private void SetStatus(string message)
        {
            _statusMessage = message;
            _statusTimer = 5f;
        }

        private static GUIStyle RichLabel()
        {
            return new GUIStyle(GUI.skin.label) { richText = true };
        }

        private static GUIStyle RichCentered()
        {
            return new GUIStyle(GUI.skin.label) { richText = true, alignment = TextAnchor.MiddleCenter };
        }

        private static Texture2D MakeTex(int width, int height, Color color)
        {
            var pixels = new Color[width * height];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = color;
            var tex = new Texture2D(width, height);
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }
    }
}
