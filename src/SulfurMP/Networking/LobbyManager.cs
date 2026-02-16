using System;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;

namespace SulfurMP.Networking
{
    /// <summary>
    /// Manages Steam Lobby creation, joining, and metadata.
    /// Works alongside NetworkManager — lobby handles discovery, NetworkManager handles data.
    /// </summary>
    public class LobbyManager : MonoBehaviour
    {
        public static LobbyManager Instance { get; private set; }

        public CSteamID CurrentLobbyId { get; private set; } = CSteamID.Nil;
        public CSteamID HostSteamId { get; private set; } = CSteamID.Nil;
        public bool InLobby => CurrentLobbyId.IsValid() && CurrentLobbyId != CSteamID.Nil;

        // Lobby metadata keys
        private const string KEY_MOD_VERSION = "sulfurmp_version";
        private const string KEY_HOST_NAME = "sulfurmp_host";
        private const string KEY_PLAYER_COUNT = "sulfurmp_players";
        private const string KEY_LOBBY_PASSWORD = "sulfurmp_password";

        /// <summary>Host-side: password for the current lobby. Empty = no password.</summary>
        public string LobbyPassword { get; set; } = "";

        /// <summary>Client-side: password to send in handshake when joining a password-protected lobby.</summary>
        public string PendingJoinPassword { get; set; } = "";

        // Steam call results
        private CallResult<LobbyCreated_t> _lobbyCreatedResult;
        private CallResult<LobbyEnter_t> _lobbyEnteredResult;
        private CallResult<LobbyMatchList_t> _lobbyListResult;

        // Steam callbacks
        private Callback<LobbyChatUpdate_t> _lobbyChatUpdate;
        private Callback<GameLobbyJoinRequested_t> _lobbyJoinRequested;

        // Lobby search results
        private readonly List<CSteamID> _lobbySearchResults = new List<CSteamID>();
        public IReadOnlyList<CSteamID> LobbySearchResults => _lobbySearchResults;
        public bool IsSearching { get; private set; }

        /// <summary>Raised when a lobby search completes.</summary>
        public event Action<IReadOnlyList<CSteamID>> OnLobbySearchComplete;

        private bool _steamReady;

        private void Awake()
        {
            if (Instance != null)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            Plugin.Log.LogInfo("LobbyManager created, waiting for Steam...");
        }

        private void Update()
        {
            if (!_steamReady)
                InitSteam();
        }

        private void InitSteam()
        {
            if (_steamReady) return;

            try
            {
                if (!SteamAPI.IsSteamRunning()) return;
                // Test that Steamworks is actually initialized
                SteamUser.GetSteamID();

                _lobbyCreatedResult = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
                _lobbyEnteredResult = CallResult<LobbyEnter_t>.Create(OnLobbyEntered);
                _lobbyListResult = CallResult<LobbyMatchList_t>.Create(OnLobbyListReceived);
                _lobbyChatUpdate = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
                _lobbyJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnGameLobbyJoinRequested);
                _steamReady = true;
                Plugin.Log.LogInfo("LobbyManager initialized.");
            }
            catch (Exception)
            {
                // Steam not ready yet, will retry next frame
            }
        }

        private void OnDestroy()
        {
            LeaveLobby();

            _lobbyCreatedResult?.Dispose();
            _lobbyEnteredResult?.Dispose();
            _lobbyListResult?.Dispose();
            _lobbyChatUpdate?.Dispose();
            _lobbyJoinRequested?.Dispose();

            if (Instance == this)
                Instance = null;
        }

        #region Create / Join / Leave

        /// <summary>
        /// Create a new lobby and start hosting.
        /// </summary>
        public void CreateLobby(int maxPlayers = 4)
        {
            if (InLobby)
            {
                Plugin.Log.LogWarning("Already in a lobby.");
                return;
            }

            Plugin.Log.LogInfo($"Creating lobby for {maxPlayers} players...");
            var call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, maxPlayers);
            _lobbyCreatedResult.Set(call);
        }

        /// <summary>
        /// Join an existing lobby by ID.
        /// </summary>
        public void JoinLobby(CSteamID lobbyId)
        {
            if (InLobby)
            {
                Plugin.Log.LogWarning("Already in a lobby. Leave first.");
                return;
            }

            Plugin.Log.LogInfo($"Joining lobby {lobbyId}...");
            var call = SteamMatchmaking.JoinLobby(lobbyId);
            _lobbyEnteredResult.Set(call);
        }

        /// <summary>
        /// Leave the current lobby and shut down networking.
        /// </summary>
        public void LeaveLobby()
        {
            if (!InLobby) return;

            NetworkManager.Instance?.Shutdown("Left lobby");

            SteamMatchmaking.LeaveLobby(CurrentLobbyId);
            Plugin.Log.LogInfo($"Left lobby {CurrentLobbyId}");

            CurrentLobbyId = CSteamID.Nil;
            HostSteamId = CSteamID.Nil;
            LobbyPassword = "";
            PendingJoinPassword = "";
        }

        #endregion

        #region Lobby Search

        /// <summary>
        /// Search for available SulfurMP lobbies.
        /// </summary>
        public void SearchLobbies()
        {
            if (IsSearching) return;

            IsSearching = true;
            _lobbySearchResults.Clear();

            // Note: no string filter — not all Steam API implementations reliably support metadata filters
            SteamMatchmaking.AddRequestLobbyListResultCountFilter(20);

            var call = SteamMatchmaking.RequestLobbyList();
            _lobbyListResult.Set(call);

            Plugin.Log.LogInfo("Searching for lobbies...");
        }

        /// <summary>
        /// Get lobby metadata for display purposes.
        /// </summary>
        public LobbyInfo GetLobbyInfo(CSteamID lobbyId)
        {
            return new LobbyInfo
            {
                LobbyId = lobbyId,
                HostName = SteamMatchmaking.GetLobbyData(lobbyId, KEY_HOST_NAME),
                ModVersion = SteamMatchmaking.GetLobbyData(lobbyId, KEY_MOD_VERSION),
                PlayerCount = SteamMatchmaking.GetNumLobbyMembers(lobbyId),
                MaxPlayers = SteamMatchmaking.GetLobbyMemberLimit(lobbyId),
                HasPassword = SteamMatchmaking.GetLobbyData(lobbyId, KEY_LOBBY_PASSWORD) == "1",
            };
        }

        #endregion

        #region Callbacks

        private void OnLobbyCreated(LobbyCreated_t result, bool ioFailure)
        {
            if (ioFailure || result.m_eResult != EResult.k_EResultOK)
            {
                Plugin.Log.LogError($"Failed to create lobby: {result.m_eResult} (IO failure: {ioFailure})");
                return;
            }

            CurrentLobbyId = new CSteamID(result.m_ulSteamIDLobby);
            HostSteamId = SteamUser.GetSteamID();

            // Set lobby metadata
            SteamMatchmaking.SetLobbyData(CurrentLobbyId, KEY_MOD_VERSION, PluginInfo.VERSION);
            SteamMatchmaking.SetLobbyData(CurrentLobbyId, KEY_HOST_NAME, SteamFriends.GetPersonaName());
            SteamMatchmaking.SetLobbyData(CurrentLobbyId, KEY_PLAYER_COUNT, "1");

            if (!string.IsNullOrEmpty(LobbyPassword))
                SteamMatchmaking.SetLobbyData(CurrentLobbyId, KEY_LOBBY_PASSWORD, "1");

            // Start hosting
            NetworkManager.Instance?.StartHost();

            Plugin.Log.LogInfo($"Lobby created: {CurrentLobbyId}");
            NetworkEvents.RaiseLobbyCreated(CurrentLobbyId);
        }

        private void OnLobbyEntered(LobbyEnter_t result, bool ioFailure)
        {
            if (ioFailure)
            {
                Plugin.Log.LogError("Failed to join lobby: IO failure");
                return;
            }

            var response = (EChatRoomEnterResponse)result.m_EChatRoomEnterResponse;
            if (response != EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                Plugin.Log.LogError($"Failed to join lobby: {response}");
                return;
            }

            CurrentLobbyId = new CSteamID(result.m_ulSteamIDLobby);
            HostSteamId = SteamMatchmaking.GetLobbyOwner(CurrentLobbyId);

            Plugin.Log.LogInfo($"Joined lobby {CurrentLobbyId} (host: {HostSteamId})");
            NetworkEvents.RaiseLobbyJoined(CurrentLobbyId);

            // Connect to host via SteamNetworkingSockets
            NetworkManager.Instance?.ConnectToHost(HostSteamId);
        }

        private void OnLobbyListReceived(LobbyMatchList_t result, bool ioFailure)
        {
            IsSearching = false;

            if (ioFailure)
            {
                Plugin.Log.LogError("Lobby search failed: IO failure");
                return;
            }

            _lobbySearchResults.Clear();
            for (int i = 0; i < (int)result.m_nLobbiesMatching; i++)
            {
                var lobbyId = SteamMatchmaking.GetLobbyByIndex(i);
                _lobbySearchResults.Add(lobbyId);
            }

            Plugin.Log.LogInfo($"Found {_lobbySearchResults.Count} lobbies");
            OnLobbySearchComplete?.Invoke(_lobbySearchResults);
        }

        private void OnLobbyChatUpdate(LobbyChatUpdate_t callback)
        {
            var lobbyId = new CSteamID(callback.m_ulSteamIDLobby);
            if (lobbyId != CurrentLobbyId) return;

            var stateChange = (EChatMemberStateChange)callback.m_rgfChatMemberStateChange;
            var changedUser = new CSteamID(callback.m_ulSteamIDUserChanged);

            if (stateChange.HasFlag(EChatMemberStateChange.k_EChatMemberStateChangeLeft) ||
                stateChange.HasFlag(EChatMemberStateChange.k_EChatMemberStateChangeDisconnected) ||
                stateChange.HasFlag(EChatMemberStateChange.k_EChatMemberStateChangeKicked) ||
                stateChange.HasFlag(EChatMemberStateChange.k_EChatMemberStateChangeBanned))
            {
                Plugin.Log.LogInfo($"Player left lobby: {changedUser}");
            }
            else if (stateChange.HasFlag(EChatMemberStateChange.k_EChatMemberStateChangeEntered))
            {
                Plugin.Log.LogInfo($"Player joined lobby: {changedUser}");
            }

            // Update player count metadata
            if (NetworkManager.Instance != null && NetworkManager.Instance.IsHost)
            {
                int count = SteamMatchmaking.GetNumLobbyMembers(CurrentLobbyId);
                SteamMatchmaking.SetLobbyData(CurrentLobbyId, KEY_PLAYER_COUNT, count.ToString());
            }
        }

        private void OnGameLobbyJoinRequested(GameLobbyJoinRequested_t callback)
        {
            // Player clicked "Join Game" on a friend's Steam profile
            Plugin.Log.LogInfo($"Join request via Steam overlay for lobby {callback.m_steamIDLobby}");
            JoinLobby(callback.m_steamIDLobby);
        }

        #endregion
    }

    /// <summary>
    /// Lobby information for UI display.
    /// </summary>
    public struct LobbyInfo
    {
        public CSteamID LobbyId;
        public string HostName;
        public string ModVersion;
        public int PlayerCount;
        public int MaxPlayers;
        public bool HasPassword;
    }
}
