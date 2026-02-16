using System;
using Steamworks;

namespace SulfurMP.Networking
{
    /// <summary>
    /// Central event hub for network state changes. All systems subscribe here
    /// rather than polling NetworkManager directly.
    /// </summary>
    public static class NetworkEvents
    {
        /// <summary>Fired when we successfully connect to a host (client) or start hosting.</summary>
        public static event Action OnConnected;

        /// <summary>Fired when we disconnect from the session.</summary>
        public static event Action<string> OnDisconnected;

        /// <summary>Fired when a remote peer joins the session. Param is their SteamID.</summary>
        public static event Action<CSteamID> OnPeerJoined;

        /// <summary>Fired when a remote peer leaves the session. Param is their SteamID.</summary>
        public static event Action<CSteamID> OnPeerLeft;

        /// <summary>Fired when a lobby is successfully created. Param is the lobby ID.</summary>
        public static event Action<CSteamID> OnLobbyCreated;

        /// <summary>Fired when we successfully join a lobby. Param is the lobby ID.</summary>
        public static event Action<CSteamID> OnLobbyJoined;

        /// <summary>Fired when a network message is received. Params: sender SteamID, message.</summary>
        public static event Action<CSteamID, NetworkMessage> OnMessageReceived;

        // Internal raise methods — only called by NetworkManager/LobbyManager
        internal static void RaiseConnected()
        {
            try { OnConnected?.Invoke(); }
            catch (Exception ex) { Plugin.Log.LogError($"Error in OnConnected handler: {ex}"); }
        }

        internal static void RaiseDisconnected(string reason)
        {
            try { OnDisconnected?.Invoke(reason); }
            catch (Exception ex) { Plugin.Log.LogError($"Error in OnDisconnected handler: {ex}"); }
        }

        internal static void RaisePeerJoined(CSteamID steamId)
        {
            try { OnPeerJoined?.Invoke(steamId); }
            catch (Exception ex) { Plugin.Log.LogError($"Error in OnPeerJoined handler: {ex}"); }
        }

        internal static void RaisePeerLeft(CSteamID steamId)
        {
            try { OnPeerLeft?.Invoke(steamId); }
            catch (Exception ex) { Plugin.Log.LogError($"Error in OnPeerLeft handler: {ex}"); }
        }

        internal static void RaiseLobbyCreated(CSteamID lobbyId)
        {
            try { OnLobbyCreated?.Invoke(lobbyId); }
            catch (Exception ex) { Plugin.Log.LogError($"Error in OnLobbyCreated handler: {ex}"); }
        }

        internal static void RaiseLobbyJoined(CSteamID lobbyId)
        {
            try { OnLobbyJoined?.Invoke(lobbyId); }
            catch (Exception ex) { Plugin.Log.LogError($"Error in OnLobbyJoined handler: {ex}"); }
        }

        internal static void RaiseMessageReceived(CSteamID sender, NetworkMessage message)
        {
            try { OnMessageReceived?.Invoke(sender, message); }
            catch (Exception ex) { Plugin.Log.LogError($"Error in OnMessageReceived handler: {ex}"); }
        }

        /// <summary>
        /// Clears all event subscribers. Call on shutdown to prevent leaks.
        /// </summary>
        internal static void ClearAll()
        {
            OnConnected = null;
            OnDisconnected = null;
            OnPeerJoined = null;
            OnPeerLeft = null;
            OnLobbyCreated = null;
            OnLobbyJoined = null;
            OnMessageReceived = null;
        }
    }
}
