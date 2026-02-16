using System.Collections.Generic;
using SulfurMP.Networking;
using SulfurMP.Networking.Messages;
using Steamworks;
using UnityEngine;

namespace SulfurMP.Players
{
    /// <summary>
    /// Central coordinator for remote player lifecycle: spawn, state updates, despawn.
    /// Handles host relay logic so clients learn about each other through the host.
    /// </summary>
    public class PlayerReplicationManager : MonoBehaviour
    {
        public static PlayerReplicationManager Instance { get; private set; }

        private readonly Dictionary<ulong, RemotePlayer> _remotePlayers = new Dictionary<ulong, RemotePlayer>();
        private bool _localPlayerSpawnSent;
        private bool _localPlayerWasAlive;

        public IReadOnlyDictionary<ulong, RemotePlayer> RemotePlayers => _remotePlayers;

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
            NetworkEvents.OnMessageReceived += OnNetworkMessage;
            NetworkEvents.OnPeerJoined += OnPeerJoined;
            NetworkEvents.OnPeerLeft += OnPeerLeft;
            NetworkEvents.OnDisconnected += OnDisconnected;
        }

        private void OnDisable()
        {
            NetworkEvents.OnMessageReceived -= OnNetworkMessage;
            NetworkEvents.OnPeerJoined -= OnPeerJoined;
            NetworkEvents.OnPeerLeft -= OnPeerLeft;
            NetworkEvents.OnDisconnected -= OnDisconnected;
        }

        private void OnDestroy()
        {
            DestroyAllRemotePlayers();
            if (Instance == this)
                Instance = null;
        }

        private void Update()
        {
            var net = NetworkManager.Instance;
            if (net == null || !net.IsConnected)
                return;

            // Detect local player appearing/disappearing (level transitions)
            bool playerAlive = IsLocalPlayerAlive();

            if (playerAlive && !_localPlayerWasAlive)
            {
                // Player just appeared — broadcast spawn
                _localPlayerWasAlive = true;
                BroadcastLocalSpawn();
            }
            else if (!playerAlive && _localPlayerWasAlive)
            {
                // Player just disappeared (level transition) — broadcast despawn
                _localPlayerWasAlive = false;
                _localPlayerSpawnSent = false;
                BroadcastLocalDespawn();
            }
        }

        #region Message Handling

        private void OnNetworkMessage(CSteamID sender, NetworkMessage msg)
        {
            switch (msg.Type)
            {
                case MessageType.PlayerState:
                    HandlePlayerState(sender, (PlayerStateMessage)msg);
                    break;
                case MessageType.PlayerSpawn:
                    HandlePlayerSpawn(sender, (PlayerSpawnMessage)msg);
                    break;
                case MessageType.PlayerDespawn:
                    HandlePlayerDespawn(sender, (PlayerDespawnMessage)msg);
                    break;
            }
        }

        private void HandlePlayerState(CSteamID sender, PlayerStateMessage msg)
        {
            var localId = NetworkManager.Instance.LocalSteamId.m_SteamID;

            // Ignore our own state
            if (msg.SteamId == localId)
                return;

            // Host relays to other clients
            if (NetworkManager.Instance.IsHost)
            {
                NetworkManager.Instance.SendToAllExcept(sender, msg);
            }

            // Push snapshot to the corresponding remote player
            if (_remotePlayers.TryGetValue(msg.SteamId, out var remote))
            {
                remote.PushSnapshot(msg);
            }
        }

        private void HandlePlayerSpawn(CSteamID sender, PlayerSpawnMessage msg)
        {
            var localId = NetworkManager.Instance.LocalSteamId.m_SteamID;

            // Ignore our own spawn
            if (msg.SteamId == localId)
                return;

            // Host relays to other clients
            if (NetworkManager.Instance.IsHost)
            {
                NetworkManager.Instance.SendToAllExcept(sender, msg);
            }

            // Create or update the remote player
            if (!_remotePlayers.ContainsKey(msg.SteamId))
            {
                var spawnPos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                var remote = RemotePlayer.Create(msg.SteamId, msg.PlayerName, spawnPos);
                _remotePlayers[msg.SteamId] = remote;
            }
        }

        private void HandlePlayerDespawn(CSteamID sender, PlayerDespawnMessage msg)
        {
            var localId = NetworkManager.Instance.LocalSteamId.m_SteamID;

            // Ignore our own despawn
            if (msg.SteamId == localId)
                return;

            // Host relays to other clients
            if (NetworkManager.Instance.IsHost)
            {
                NetworkManager.Instance.SendToAllExcept(sender, msg);
            }

            DestroyRemotePlayer(msg.SteamId);
        }

        #endregion

        #region Peer Lifecycle

        private void OnPeerJoined(CSteamID peerId)
        {
            if (!NetworkManager.Instance.IsHost)
                return;

            Plugin.Log.LogInfo($"PlayerReplication: Peer joined {peerId}, sending spawn info...");

            // Send our own spawn to the new peer (if we have a player)
            if (_localPlayerWasAlive)
                SendLocalSpawnTo(peerId);

            // Send all existing remote players' spawns to the new peer
            foreach (var kvp in _remotePlayers)
            {
                var spawnMsg = new PlayerSpawnMessage
                {
                    SteamId = kvp.Key,
                    PlayerName = kvp.Value.PlayerName,
                    PosX = kvp.Value.transform.position.x,
                    PosY = kvp.Value.transform.position.y,
                    PosZ = kvp.Value.transform.position.z,
                };
                NetworkManager.Instance.SendMessage(peerId, spawnMsg);
            }
        }

        private void OnPeerLeft(CSteamID peerId)
        {
            var steamId = peerId.m_SteamID;

            // Destroy their remote player
            DestroyRemotePlayer(steamId);

            // If host, broadcast despawn to remaining clients
            if (NetworkManager.Instance.IsHost)
            {
                var despawnMsg = new PlayerDespawnMessage
                {
                    SteamId = steamId,
                    Reason = "Disconnected",
                };
                NetworkManager.Instance.SendToAll(despawnMsg);
            }
        }

        private void OnDisconnected(string reason)
        {
            Plugin.Log.LogInfo($"PlayerReplication: Disconnected ({reason}), cleaning up...");
            DestroyAllRemotePlayers();
            _localPlayerSpawnSent = false;
            _localPlayerWasAlive = false;
        }

        #endregion

        #region Local Player Spawn/Despawn

        private void BroadcastLocalSpawn()
        {
            if (_localPlayerSpawnSent)
                return;

            var net = NetworkManager.Instance;
            if (net == null || !net.IsConnected)
                return;

            var gm = LocalPlayerSync.GetPlayerObject(GetGameManager());
            if (gm == null)
                return;

            var pos = gm.transform.position;
            var msg = new PlayerSpawnMessage
            {
                SteamId = SteamUser.GetSteamID().m_SteamID,
                PlayerName = SteamFriends.GetPersonaName(),
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
            };

            net.SendToAll(msg);
            _localPlayerSpawnSent = true;
            Plugin.Log.LogInfo($"Broadcast local player spawn at {pos}");
        }

        private void BroadcastLocalDespawn()
        {
            var net = NetworkManager.Instance;
            if (net == null || !net.IsConnected)
                return;

            var msg = new PlayerDespawnMessage
            {
                SteamId = SteamUser.GetSteamID().m_SteamID,
                Reason = "Level transition",
            };
            net.SendToAll(msg);
            Plugin.Log.LogInfo("Broadcast local player despawn");
        }

        private void SendLocalSpawnTo(CSteamID target)
        {
            var gm = LocalPlayerSync.GetPlayerObject(GetGameManager());
            if (gm == null)
                return;

            var pos = gm.transform.position;
            var msg = new PlayerSpawnMessage
            {
                SteamId = SteamUser.GetSteamID().m_SteamID,
                PlayerName = SteamFriends.GetPersonaName(),
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
            };
            NetworkManager.Instance.SendMessage(target, msg);
        }

        #endregion

        #region Helpers

        private void DestroyRemotePlayer(ulong steamId)
        {
            if (_remotePlayers.TryGetValue(steamId, out var remote))
            {
                if (remote != null && remote.gameObject != null)
                    Destroy(remote.gameObject);
                _remotePlayers.Remove(steamId);
            }
        }

        private void DestroyAllRemotePlayers()
        {
            foreach (var kvp in _remotePlayers)
            {
                if (kvp.Value != null && kvp.Value.gameObject != null)
                    Destroy(kvp.Value.gameObject);
            }
            _remotePlayers.Clear();
        }

        private bool IsLocalPlayerAlive()
        {
            var gm = GetGameManager();
            if (gm == null) return false;
            return LocalPlayerSync.GetPlayerObject(gm) != null;
        }

        // Reuse LocalPlayerSync's reflection for GameManager access
        private static System.Type _gameManagerType;
        private static System.Reflection.PropertyInfo _instanceProp;
        private static bool _reflectionInit;

        private static object GetGameManager()
        {
            if (!_reflectionInit)
            {
                _reflectionInit = true;
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    _gameManagerType = asm.GetType("PerfectRandom.Sulfur.Core.GameManager");
                    if (_gameManagerType != null) break;
                }
                if (_gameManagerType != null)
                {
                    _instanceProp = _gameManagerType.GetProperty("Instance",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy);
                }
            }

            if (_instanceProp == null) return null;
            try
            {
                var instance = _instanceProp.GetValue(null);
                if (instance is Object unityObj && unityObj == null)
                    return null;
                return instance;
            }
            catch { return null; }
        }

        #endregion
    }
}
