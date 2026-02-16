using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Steamworks;
using UnityEngine;

namespace SulfurMP.Networking
{
    /// <summary>
    /// Connection state machine for the network session.
    /// </summary>
    public enum ConnectionState
    {
        Disconnected,
        HostingLobby,
        Connecting,
        Connected,
    }

    /// <summary>
    /// Manages Steam P2P networking connections and message routing.
    /// Uses SteamNetworkingSockets for reliable/unreliable messaging.
    /// Host-authoritative model: host runs a listen socket, clients connect via P2P.
    /// </summary>
    public class NetworkManager : MonoBehaviour
    {
        public static NetworkManager Instance { get; private set; }

        public bool IsHost { get; private set; }
        public bool IsConnected => State == ConnectionState.Connected || State == ConnectionState.HostingLobby;
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

        /// <summary>Local Steam ID.</summary>
        public CSteamID LocalSteamId { get; private set; }

        /// <summary>All connected peer SteamIDs (does not include local player).</summary>
        public IReadOnlyList<CSteamID> ConnectedPeers => _connectedPeers;

        // Host-side: listen socket and poll group
        private HSteamListenSocket _listenSocket;
        private HSteamNetPollGroup _pollGroup;

        // Client-side: connection to host
        private HSteamNetConnection _hostConnection;

        // Peer tracking
        private readonly List<CSteamID> _connectedPeers = new List<CSteamID>();
        private readonly Dictionary<CSteamID, HSteamNetConnection> _peerConnections = new Dictionary<CSteamID, HSteamNetConnection>();
        private readonly Dictionary<HSteamNetConnection, CSteamID> _connectionToPeer = new Dictionary<HSteamNetConnection, CSteamID>();

        // Handshake tracking
        private readonly HashSet<CSteamID> _handshakeCompleted = new HashSet<CSteamID>();

        // Statistics
        public int MessagesSentPerSecond { get; private set; }
        public int MessagesReceivedPerSecond { get; private set; }
        public int BytesSentPerSecond { get; private set; }
        public int BytesReceivedPerSecond { get; private set; }

        private int _msgSentCount, _msgRecvCount, _bytesSent, _bytesRecv;
        private float _statsTimer;

        // Heartbeat
        private float _heartbeatTimer;
        private const float HeartbeatInterval = 5f;
        private const float HeartbeatTimeoutSeconds = 15f;
        private readonly Dictionary<CSteamID, float> _lastHeartbeatReceived = new Dictionary<CSteamID, float>();

        // Steam callbacks
        private Callback<SteamNetConnectionStatusChangedCallback_t> _connectionStatusChanged;

        // Message receive buffer
        private readonly IntPtr[] _messageBuffer = new IntPtr[64];

        private bool _steamReady;

        private void Awake()
        {
            if (Instance != null)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            Plugin.Log.LogInfo("NetworkManager created, waiting for Steam...");
        }

        private void InitSteam()
        {
            if (_steamReady) return;

            try
            {
                if (!SteamAPI.IsSteamRunning()) return;

                LocalSteamId = SteamUser.GetSteamID();
                _connectionStatusChanged = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);
                _steamReady = true;
                Plugin.Log.LogInfo($"NetworkManager initialized. Local SteamID: {LocalSteamId}");
            }
            catch (System.Exception)
            {
                // Steam not ready yet, will retry next frame
            }
        }

        private void OnDestroy()
        {
            Shutdown();
            _connectionStatusChanged?.Dispose();

            if (Instance == this)
                Instance = null;
        }

        private void Update()
        {
            if (!_steamReady)
            {
                InitSteam();
                return;
            }

            if (State == ConnectionState.Disconnected)
                return;

            ReceiveMessages();
            UpdateHeartbeat();
            CheckHeartbeatTimeouts();
            UpdateStatistics();
        }

        #region Host

        /// <summary>
        /// Start hosting a multiplayer session. Creates a listen socket for incoming P2P connections.
        /// </summary>
        public bool StartHost()
        {
            if (State != ConnectionState.Disconnected)
            {
                Plugin.Log.LogWarning("Cannot start host: already in a session.");
                return false;
            }

            // Create listen socket for P2P connections
            _listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(0, 0, null);
            _pollGroup = SteamNetworkingSockets.CreatePollGroup();

            if (_listenSocket == HSteamListenSocket.Invalid)
            {
                Plugin.Log.LogError("Failed to create listen socket.");
                return false;
            }

            IsHost = true;
            State = ConnectionState.HostingLobby;
            _handshakeCompleted.Clear();

            Plugin.Log.LogInfo("Started hosting. Listen socket created.");
            NetworkEvents.RaiseConnected();

            return true;
        }

        #endregion

        #region Client

        /// <summary>
        /// Connect to a host as a client.
        /// </summary>
        public bool ConnectToHost(CSteamID hostSteamId)
        {
            if (State != ConnectionState.Disconnected)
            {
                Plugin.Log.LogWarning("Cannot connect: already in a session.");
                return false;
            }

            var identity = new SteamNetworkingIdentity();
            identity.SetSteamID(hostSteamId);

            _hostConnection = SteamNetworkingSockets.ConnectP2P(ref identity, 0, 0, null);

            if (_hostConnection == HSteamNetConnection.Invalid)
            {
                Plugin.Log.LogError($"Failed to connect to host {hostSteamId}.");
                return false;
            }

            IsHost = false;
            State = ConnectionState.Connecting;

            Plugin.Log.LogInfo($"Connecting to host {hostSteamId}...");

            return true;
        }

        #endregion

        #region Connection Status

        private void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t callback)
        {
            var conn = callback.m_hConn;
            var info = callback.m_info;
            var oldState = callback.m_eOldState;

            Plugin.Log.LogInfo($"Connection status changed: {oldState} -> {info.m_eState} (peer: {info.m_identityRemote.GetSteamID()})");

            if (IsHost)
                HandleHostConnectionChange(conn, ref info, oldState);
            else
                HandleClientConnectionChange(conn, ref info, oldState);
        }

        private void HandleHostConnectionChange(HSteamNetConnection conn, ref SteamNetConnectionInfo_t info, ESteamNetworkingConnectionState oldState)
        {
            switch (info.m_eState)
            {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                    // Accept incoming connection
                    var result = SteamNetworkingSockets.AcceptConnection(conn);
                    if (result != EResult.k_EResultOK)
                    {
                        Plugin.Log.LogError($"Failed to accept connection: {result}");
                        SteamNetworkingSockets.CloseConnection(conn, 0, "Accept failed", false);
                        return;
                    }
                    // Add to poll group for efficient message retrieval
                    SteamNetworkingSockets.SetConnectionPollGroup(conn, _pollGroup);
                    Plugin.Log.LogInfo($"Accepted connection from {info.m_identityRemote.GetSteamID()}");
                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                    var peerSteamId = info.m_identityRemote.GetSteamID();
                    RegisterPeer(peerSteamId, conn);
                    Plugin.Log.LogInfo($"Peer connected: {peerSteamId}");
                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                    var disconnectedPeer = info.m_identityRemote.GetSteamID();
                    UnregisterPeer(disconnectedPeer, conn);
                    SteamNetworkingSockets.CloseConnection(conn, 0, null, false);
                    Plugin.Log.LogInfo($"Peer disconnected: {disconnectedPeer} ({info.m_eState})");
                    break;
            }
        }

        private void HandleClientConnectionChange(HSteamNetConnection conn, ref SteamNetConnectionInfo_t info, ESteamNetworkingConnectionState oldState)
        {
            switch (info.m_eState)
            {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                    State = ConnectionState.Connected;
                    Plugin.Log.LogInfo("Connected to host!");
                    NetworkEvents.RaiseConnected();

                    // Send handshake
                    SendHandshake();
                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                    Plugin.Log.LogInfo("Disconnected by host.");
                    Shutdown("Host closed the connection");
                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                    Plugin.Log.LogWarning($"Connection problem: {info.m_szEndDebug}");
                    Shutdown("Connection problem detected");
                    break;
            }
        }

        #endregion

        #region Peer Management

        private void RegisterPeer(CSteamID steamId, HSteamNetConnection connection)
        {
            if (!_connectedPeers.Contains(steamId))
                _connectedPeers.Add(steamId);

            _peerConnections[steamId] = connection;
            _connectionToPeer[connection] = steamId;
            _lastHeartbeatReceived[steamId] = Time.unscaledTime;

            NetworkEvents.RaisePeerJoined(steamId);
        }

        private void UnregisterPeer(CSteamID steamId, HSteamNetConnection connection)
        {
            _connectedPeers.Remove(steamId);
            _peerConnections.Remove(steamId);
            _connectionToPeer.Remove(connection);
            _handshakeCompleted.Remove(steamId);
            _lastHeartbeatReceived.Remove(steamId);

            NetworkEvents.RaisePeerLeft(steamId);
        }

        #endregion

        #region Send/Receive

        /// <summary>
        /// Send a message to a specific peer.
        /// </summary>
        public void SendMessage(CSteamID target, NetworkMessage message)
        {
            HSteamNetConnection conn;

            if (IsHost)
            {
                if (!_peerConnections.TryGetValue(target, out conn))
                {
                    Plugin.Log.LogWarning($"Cannot send to {target}: not connected.");
                    return;
                }
            }
            else
            {
                conn = _hostConnection;
            }

            SendOnConnection(conn, message);
        }

        /// <summary>
        /// Send a message to all connected peers. Host only.
        /// </summary>
        public void SendToAll(NetworkMessage message)
        {
            if (!IsHost)
            {
                // Clients can only send to host
                SendOnConnection(_hostConnection, message);
                return;
            }

            foreach (var kvp in _peerConnections)
            {
                SendOnConnection(kvp.Value, message);
            }
        }

        /// <summary>
        /// Send a message to all connected peers except one. Host only.
        /// </summary>
        public void SendToAllExcept(CSteamID except, NetworkMessage message)
        {
            if (!IsHost) return;

            foreach (var kvp in _peerConnections)
            {
                if (kvp.Key != except)
                    SendOnConnection(kvp.Value, message);
            }
        }

        private void SendOnConnection(HSteamNetConnection conn, NetworkMessage message)
        {
            if (conn == HSteamNetConnection.Invalid)
                return;

            byte[] data = MessageSerializer.Serialize(message);

            int sendFlags = message.Reliable
                ? Constants.k_nSteamNetworkingSend_Reliable
                : Constants.k_nSteamNetworkingSend_Unreliable;

            // Pin the data and send
            GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                long messageOut;
                EResult result = SteamNetworkingSockets.SendMessageToConnection(
                    conn,
                    handle.AddrOfPinnedObject(),
                    (uint)data.Length,
                    sendFlags,
                    out messageOut);

                if (result != EResult.k_EResultOK)
                {
                    Plugin.Log.LogWarning($"SendMessage failed: {result}");
                }
                else
                {
                    _msgSentCount++;
                    _bytesSent += data.Length;
                }
            }
            finally
            {
                handle.Free();
            }
        }

        private void ReceiveMessages()
        {
            if (IsHost)
                ReceiveMessagesOnPollGroup();
            else
                ReceiveMessagesOnConnection();
        }

        private void ReceiveMessagesOnPollGroup()
        {
            int numMessages = SteamNetworkingSockets.ReceiveMessagesOnPollGroup(
                _pollGroup, _messageBuffer, _messageBuffer.Length);

            for (int i = 0; i < numMessages; i++)
            {
                ProcessReceivedMessage(_messageBuffer[i]);
            }
        }

        private void ReceiveMessagesOnConnection()
        {
            if (_hostConnection == HSteamNetConnection.Invalid)
                return;

            int numMessages = SteamNetworkingSockets.ReceiveMessagesOnConnection(
                _hostConnection, _messageBuffer, _messageBuffer.Length);

            for (int i = 0; i < numMessages; i++)
            {
                ProcessReceivedMessage(_messageBuffer[i]);
            }
        }

        private void ProcessReceivedMessage(IntPtr msgPtr)
        {
            var steamMsg = SteamNetworkingMessage_t.FromIntPtr(msgPtr);

            try
            {
                int size = steamMsg.m_cbSize;
                if (size <= 0)
                    return;

                byte[] data = new byte[size];
                Marshal.Copy(steamMsg.m_pData, data, 0, size);

                _msgRecvCount++;
                _bytesRecv += size;

                // Identify sender
                CSteamID sender;
                if (IsHost)
                {
                    if (!_connectionToPeer.TryGetValue(steamMsg.m_conn, out sender))
                    {
                        // Could be a new connection not yet registered
                        sender = steamMsg.m_identityPeer.GetSteamID();
                    }
                }
                else
                {
                    // On client, all messages come from the host
                    sender = LobbyManager.Instance?.HostSteamId ?? CSteamID.Nil;
                }

                var message = MessageSerializer.Deserialize(data, data.Length);
                if (message != null)
                {
                    HandleMessage(sender, message);
                    NetworkEvents.RaiseMessageReceived(sender, message);
                }
            }
            finally
            {
                SteamNetworkingMessage_t.Release(msgPtr);
            }
        }

        #endregion

        #region Message Handling

        private void HandleMessage(CSteamID sender, NetworkMessage message)
        {
            // Track heartbeats for timeout detection
            if (message.Type == MessageType.Heartbeat)
            {
                _lastHeartbeatReceived[sender] = Time.unscaledTime;
            }

            switch (message.Type)
            {
                case MessageType.Handshake:
                    HandleHandshake(sender, (Messages.HandshakeMessage)message);
                    break;
                case MessageType.HandshakeResponse:
                    HandleHandshakeResponse(sender, (Messages.HandshakeResponseMessage)message);
                    break;
                case MessageType.Disconnect:
                    HandleDisconnect(sender, (Messages.DisconnectMessage)message);
                    break;
                case MessageType.Ping:
                    HandlePing(sender, (Messages.PingMessage)message);
                    break;
            }
        }

        private void HandleHandshake(CSteamID sender, Messages.HandshakeMessage msg)
        {
            if (!IsHost) return;

            Plugin.Log.LogInfo($"Received handshake from {sender}: {msg.PlayerName} (v{msg.ModVersion})");

            bool versionMatch = msg.ModVersion == PluginInfo.VERSION;

            // Password check
            bool passwordOk = true;
            var lm = LobbyManager.Instance;
            if (lm != null && !string.IsNullOrEmpty(lm.LobbyPassword))
                passwordOk = msg.Password == lm.LobbyPassword;

            bool accepted = versionMatch && passwordOk;
            string rejectReason = "";
            if (!versionMatch)
                rejectReason = $"Version mismatch: host={PluginInfo.VERSION}, client={msg.ModVersion}";
            else if (!passwordOk)
                rejectReason = "Incorrect password";

            var response = new Messages.HandshakeResponseMessage
            {
                Accepted = accepted,
                RejectReason = rejectReason,
                HostName = SteamFriends.GetPersonaName()
            };

            SendMessage(sender, response);

            if (accepted)
            {
                _handshakeCompleted.Add(sender);
                Plugin.Log.LogInfo($"Handshake completed with {msg.PlayerName}");
            }
            else
            {
                Plugin.Log.LogWarning($"Rejected {msg.PlayerName}: {rejectReason}");
            }
        }

        private void HandleHandshakeResponse(CSteamID sender, Messages.HandshakeResponseMessage msg)
        {
            if (IsHost) return;

            if (msg.Accepted)
            {
                Plugin.Log.LogInfo($"Handshake accepted by host ({msg.HostName})");
                // Init heartbeat timestamp for the host
                _lastHeartbeatReceived[sender] = Time.unscaledTime;
            }
            else
            {
                Plugin.Log.LogWarning($"Handshake rejected: {msg.RejectReason}");
                Shutdown(msg.RejectReason);
            }
        }

        private void HandleDisconnect(CSteamID sender, Messages.DisconnectMessage msg)
        {
            Plugin.Log.LogInfo($"Peer {sender} disconnecting: {msg.Reason}");
        }

        private void HandlePing(CSteamID sender, Messages.PingMessage msg)
        {
            var pong = new Messages.PongMessage { OriginalTimestamp = msg.Timestamp };
            SendMessage(sender, pong);
        }

        private void SendHandshake()
        {
            var lm = LobbyManager.Instance;
            var handshake = new Messages.HandshakeMessage
            {
                ModVersion = PluginInfo.VERSION,
                PlayerName = SteamFriends.GetPersonaName(),
                Password = lm != null ? lm.PendingJoinPassword : ""
            };
            SendToAll(handshake);

            // Clear pending password after sending
            if (lm != null)
                lm.PendingJoinPassword = "";
        }

        #endregion

        #region Heartbeat / Stats

        private void UpdateHeartbeat()
        {
            _heartbeatTimer += Time.unscaledDeltaTime;
            if (_heartbeatTimer >= HeartbeatInterval)
            {
                _heartbeatTimer = 0f;
                var heartbeat = new Messages.HeartbeatMessage { Timestamp = Time.unscaledTime };
                SendToAll(heartbeat);
            }
        }

        private void CheckHeartbeatTimeouts()
        {
            float now = Time.unscaledTime;

            if (IsHost)
            {
                // Host checks all peers for timeout
                var timedOut = new List<CSteamID>();
                foreach (var kvp in _lastHeartbeatReceived)
                {
                    if (now - kvp.Value >= HeartbeatTimeoutSeconds)
                        timedOut.Add(kvp.Key);
                }

                foreach (var peer in timedOut)
                {
                    Plugin.Log.LogWarning($"Peer {peer} heartbeat timeout ({HeartbeatTimeoutSeconds}s)");
                    if (_peerConnections.TryGetValue(peer, out var conn))
                    {
                        UnregisterPeer(peer, conn);
                        SteamNetworkingSockets.CloseConnection(conn, 0, "Heartbeat timeout", false);
                    }
                }
            }
            else
            {
                // Client checks host for timeout
                foreach (var kvp in _lastHeartbeatReceived)
                {
                    if (now - kvp.Value >= HeartbeatTimeoutSeconds)
                    {
                        Plugin.Log.LogWarning($"Host heartbeat timeout ({HeartbeatTimeoutSeconds}s)");
                        Shutdown("Host timed out");
                        return;
                    }
                }
            }
        }

        private void UpdateStatistics()
        {
            _statsTimer += Time.unscaledDeltaTime;
            if (_statsTimer >= 1f)
            {
                MessagesSentPerSecond = _msgSentCount;
                MessagesReceivedPerSecond = _msgRecvCount;
                BytesSentPerSecond = _bytesSent;
                BytesReceivedPerSecond = _bytesRecv;

                _msgSentCount = 0;
                _msgRecvCount = 0;
                _bytesSent = 0;
                _bytesRecv = 0;
                _statsTimer = 0f;
            }
        }

        #endregion

        #region Shutdown

        /// <summary>
        /// Cleanly shut down all networking.
        /// </summary>
        public void Shutdown(string reason = "Disconnected")
        {
            if (State == ConnectionState.Disconnected)
                return;

            Plugin.Log.LogInfo($"Shutting down network: {reason}");

            // Notify peers
            try
            {
                var disconnectMsg = new Messages.DisconnectMessage { Reason = reason };
                SendToAll(disconnectMsg);
            }
            catch (Exception) { /* Best effort */ }

            // Close all connections
            if (IsHost)
            {
                foreach (var kvp in _peerConnections)
                {
                    SteamNetworkingSockets.CloseConnection(kvp.Value, 0, reason, false);
                }

                if (_pollGroup != HSteamNetPollGroup.Invalid)
                    SteamNetworkingSockets.DestroyPollGroup(_pollGroup);

                if (_listenSocket != HSteamListenSocket.Invalid)
                    SteamNetworkingSockets.CloseListenSocket(_listenSocket);

                _pollGroup = HSteamNetPollGroup.Invalid;
                _listenSocket = HSteamListenSocket.Invalid;
            }
            else
            {
                if (_hostConnection != HSteamNetConnection.Invalid)
                {
                    SteamNetworkingSockets.CloseConnection(_hostConnection, 0, reason, false);
                    _hostConnection = HSteamNetConnection.Invalid;
                }
            }

            _connectedPeers.Clear();
            _peerConnections.Clear();
            _connectionToPeer.Clear();
            _handshakeCompleted.Clear();
            _lastHeartbeatReceived.Clear();

            IsHost = false;
            State = ConnectionState.Disconnected;

            NetworkEvents.RaiseDisconnected(reason);
        }

        #endregion
    }
}
