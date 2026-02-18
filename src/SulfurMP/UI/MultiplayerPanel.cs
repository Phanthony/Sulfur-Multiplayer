using System;
using System.Collections.Generic;
using SulfurMP.Config;
using SulfurMP.Networking;
using Steamworks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SulfurMP.UI
{
    /// <summary>
    /// Full-screen UGUI panel for multiplayer hosting, joining, and lobby browsing.
    /// Opened from the pause menu "Multiplayer" button. Tab-based layout.
    /// </summary>
    public class MultiplayerPanel : MonoBehaviour
    {
        public static MultiplayerPanel Instance { get; private set; }

        private Canvas _canvas;
        private GameObject _rootPanel;
        private bool _isVisible;
        public bool IsVisible => _isVisible;

        // Pause menu reference — re-enabled when Back is pressed
        private GameObject _pauseMenuGO;

        // Left column sections
        private GameObject _disconnectedSection;
        private GameObject _connectedSection;

        // Tab system
        private Button _hostTabBtn;
        private Button _joinTabBtn;
        private TextMeshProUGUI _hostTabLabel;
        private TextMeshProUGUI _joinTabLabel;
        private GameObject _hostTabContent;
        private GameObject _joinTabContent;

        // Host section
        private TMP_InputField _hostMaxPlayersInput;
        private TMP_InputField _hostPasswordInput;

        // Join section
        private TMP_InputField _joinSteamIdInput;

        // Connection info
        private TextMeshProUGUI _connectionStatusText;
        private Transform _playerListContent;

        // Lobby browser
        private Transform _lobbyListContent;
        private TMP_InputField _filterInput;
        private TextMeshProUGUI _lobbyCountText;
        private Button _refreshButton;
        private TextMeshProUGUI _refreshButtonLabel;

        // Password dialog
        private GameObject _passwordDialog;
        private TMP_InputField _passwordDialogInput;
        private CSteamID _pendingJoinLobbyId;

        // Status bar
        private TextMeshProUGUI _statusText;
        private float _statusTimer;

        // Cached lobby data for filtering
        private readonly List<LobbyInfo> _cachedLobbies = new List<LobbyInfo>();

        private static readonly Color DarkText = new Color(0.1f, 0.1f, 0.1f);

        private void Awake()
        {
            if (Instance != null)
            {
                Destroy(this);
                return;
            }
            Instance = this;
        }

        private void Start()
        {
            UITheme.EnsureInitialized();
            BuildUI();
            Hide();

            // Subscribe to network events
            NetworkEvents.OnConnected += OnNetConnected;
            NetworkEvents.OnDisconnected += OnNetDisconnected;
            NetworkEvents.OnLobbyCreated += OnNetLobbyCreated;
            NetworkEvents.OnLobbyJoined += OnNetLobbyJoined;
            NetworkEvents.OnPeerJoined += OnNetPeerChanged;
            NetworkEvents.OnPeerLeft += OnNetPeerChanged;

            if (LobbyManager.Instance != null)
                LobbyManager.Instance.OnLobbySearchComplete += OnLobbySearchComplete;
        }

        private void OnDestroy()
        {
            NetworkEvents.OnConnected -= OnNetConnected;
            NetworkEvents.OnDisconnected -= OnNetDisconnected;
            NetworkEvents.OnLobbyCreated -= OnNetLobbyCreated;
            NetworkEvents.OnLobbyJoined -= OnNetLobbyJoined;
            NetworkEvents.OnPeerJoined -= OnNetPeerChanged;
            NetworkEvents.OnPeerLeft -= OnNetPeerChanged;

            if (LobbyManager.Instance != null)
                LobbyManager.Instance.OnLobbySearchComplete -= OnLobbySearchComplete;

            if (_canvas != null)
                Destroy(_canvas.gameObject);

            if (Instance == this)
                Instance = null;
        }

        private void OnNetLobbyCreated(CSteamID _) => SetStatus("Lobby created");
        private void OnNetLobbyJoined(CSteamID _) => SetStatus("Joined lobby");
        private void OnNetPeerChanged(CSteamID _) => RefreshConnectionInfo();

        private void Update()
        {
            if (!_isVisible) return;

            // Fade status message
            if (_statusTimer > 0)
            {
                _statusTimer -= Time.unscaledDeltaTime;
                if (_statusTimer <= 0 && _statusText != null)
                    _statusText.text = "";
            }
        }

        #region Show / Hide

        /// <summary>
        /// Show the panel from the pause menu context.
        /// </summary>
        public void ShowFromPauseMenu(GameObject pauseMenuGO)
        {
            _pauseMenuGO = pauseMenuGO;
            Show();
        }

        public void Show()
        {
            UITheme.EnsureInitialized();
            _isVisible = true;
            if (_canvas != null)
                _canvas.gameObject.SetActive(true);

            RefreshView();

            // Auto-search lobbies when opening
            if (LobbyManager.Instance != null && !LobbyManager.Instance.IsSearching)
                LobbyManager.Instance.SearchLobbies();
        }

        public void Hide()
        {
            _isVisible = false;
            if (_canvas != null)
                _canvas.gameObject.SetActive(false);

            // Re-enable pause menu
            if (_pauseMenuGO != null)
            {
                _pauseMenuGO.SetActive(true);
                _pauseMenuGO = null;
            }
        }

        /// <summary>
        /// Called by PauseMenuHook.ResumeGameHook when ESC is pressed while the panel is open.
        /// Closes the password dialog if open, otherwise closes the panel entirely.
        /// </summary>
        public void HandleEscapeFromGame()
        {
            if (!_isVisible) return;

            // Password dialog open → close just the dialog
            if (_passwordDialog != null && _passwordDialog.activeSelf)
            {
                HidePasswordDialog();
                return;
            }

            // Close multiplayer panel → re-enables pause menu via Hide()
            Hide();
        }

        #endregion

        #region Build UI

        private void BuildUI()
        {
            _canvas = UIBuilder.CreateCanvas("SulfurMP_MultiplayerCanvas", 100);

            // Root panel — fills screen with margins
            _rootPanel = UIBuilder.CreatePanel(_canvas.transform, "RootPanel", UITheme.PanelBg);
            var rootRt = _rootPanel.GetComponent<RectTransform>();
            UIBuilder.SetStretch(rootRt);
            rootRt.offsetMin = new Vector2(60, 40);
            rootRt.offsetMax = new Vector2(-60, -40);

            var rootLayout = UIBuilder.AddVerticalLayout(_rootPanel, 12, new RectOffset(30, 30, 20, 20));
            rootLayout.childForceExpandHeight = false;

            // Header row
            BuildHeader(_rootPanel.transform);

            // Separator below header
            UIBuilder.CreateSeparator(_rootPanel.transform);

            // Main content area (horizontal split)
            var mainContent = new GameObject("MainContent", typeof(RectTransform));
            mainContent.transform.SetParent(_rootPanel.transform, false);
            var mainLayout = UIBuilder.AddHorizontalLayout(mainContent, 20, new RectOffset(0, 0, 0, 0));
            mainLayout.childForceExpandWidth = false;
            mainLayout.childForceExpandHeight = true;
            mainLayout.childControlWidth = true;
            mainLayout.childControlHeight = true;
            var mainLe = mainContent.AddComponent<LayoutElement>();
            mainLe.flexibleHeight = 1f;

            // Left column (35%)
            var leftCol = new GameObject("LeftColumn", typeof(RectTransform));
            leftCol.transform.SetParent(mainContent.transform, false);
            UIBuilder.AddVerticalLayout(leftCol, 10, new RectOffset(0, 0, 0, 0));
            var leftLe = leftCol.AddComponent<LayoutElement>();
            leftLe.flexibleWidth = 0.35f;

            BuildLeftColumn(leftCol.transform);

            // Vertical separator
            var vSep = new GameObject("VSeparator", typeof(RectTransform));
            vSep.transform.SetParent(mainContent.transform, false);
            var vSepImg = vSep.AddComponent<Image>();
            vSepImg.sprite = UITheme.WhiteSprite;
            vSepImg.color = UITheme.Separator;
            var vSepLe = vSep.AddComponent<LayoutElement>();
            vSepLe.preferredWidth = 1;

            // Right column (65%)
            var rightCol = new GameObject("RightColumn", typeof(RectTransform));
            rightCol.transform.SetParent(mainContent.transform, false);
            UIBuilder.AddVerticalLayout(rightCol, 8, new RectOffset(0, 0, 0, 0));
            var rightLe = rightCol.AddComponent<LayoutElement>();
            rightLe.flexibleWidth = 0.65f;

            BuildRightColumn(rightCol.transform);

            // Status bar
            _statusText = UIBuilder.CreateText(_rootPanel.transform, "StatusBar", "",
                14f, TextAlignmentOptions.Center, UITheme.SulfurYellow);
            UIBuilder.SetPreferredHeight(_statusText.gameObject, 20);

            // Password dialog (hidden by default)
            BuildPasswordDialog();
        }

        private void BuildHeader(Transform parent)
        {
            var headerRow = new GameObject("Header", typeof(RectTransform));
            headerRow.transform.SetParent(parent, false);
            var headerLayout = UIBuilder.AddHorizontalLayout(headerRow, 10);
            headerLayout.childForceExpandWidth = false;
            headerLayout.childAlignment = TextAnchor.MiddleLeft;
            UIBuilder.SetPreferredHeight(headerRow, 40);

            // Title
            var title = UIBuilder.CreateText(headerRow.transform, "Title", "Multiplayer",
                28f, TextAlignmentOptions.Left, UITheme.SulfurYellow);
            UIBuilder.SetFlexibleWidth(title.gameObject);

            // Back button
            var backBtn = UIBuilder.CreateButton(headerRow.transform, "BackButton", "Back",
                16f, () => Hide());
            UIBuilder.SetPreferredWidth(backBtn.gameObject, 100);
            UIBuilder.SetPreferredHeight(backBtn.gameObject, 36);
        }

        private void BuildLeftColumn(Transform parent)
        {
            // == Disconnected section (tabs + content) ==
            _disconnectedSection = new GameObject("DisconnectedSection", typeof(RectTransform));
            _disconnectedSection.transform.SetParent(parent, false);
            UIBuilder.AddVerticalLayout(_disconnectedSection, 10);
            var dcLe = _disconnectedSection.AddComponent<LayoutElement>();
            dcLe.flexibleHeight = 1f;

            BuildTabBar(_disconnectedSection.transform);
            BuildHostTabContent(_disconnectedSection.transform);
            BuildJoinTabContent(_disconnectedSection.transform);

            // Default to Host Game tab
            SetActiveTab(true);

            // == Connected section (info + disconnect) ==
            _connectedSection = new GameObject("ConnectedSection", typeof(RectTransform));
            _connectedSection.transform.SetParent(parent, false);
            UIBuilder.AddVerticalLayout(_connectedSection, 8);
            var cnLe = _connectedSection.AddComponent<LayoutElement>();
            cnLe.flexibleHeight = 1f;

            BuildConnectedSection(_connectedSection.transform);
        }

        private void BuildTabBar(Transform parent)
        {
            var tabBar = new GameObject("TabBar", typeof(RectTransform));
            tabBar.transform.SetParent(parent, false);
            var tabLayout = UIBuilder.AddHorizontalLayout(tabBar, 2);
            tabLayout.childForceExpandWidth = true;
            UIBuilder.SetPreferredHeight(tabBar, 36);

            // Host Game tab
            _hostTabBtn = UIBuilder.CreateButton(tabBar.transform, "HostTab", "Host Game",
                15f, () => SetActiveTab(true), UITheme.TabActive, UITheme.TabActive);
            _hostTabLabel = _hostTabBtn.GetComponentInChildren<TextMeshProUGUI>();

            // Join via ID tab
            _joinTabBtn = UIBuilder.CreateButton(tabBar.transform, "JoinTab", "Join via ID",
                15f, () => SetActiveTab(false), UITheme.TabInactive, UITheme.TabInactive);
            _joinTabLabel = _joinTabBtn.GetComponentInChildren<TextMeshProUGUI>();
        }

        private void SetActiveTab(bool hostTab)
        {
            if (_hostTabContent != null) _hostTabContent.SetActive(hostTab);
            if (_joinTabContent != null) _joinTabContent.SetActive(!hostTab);

            // Update tab button colors
            if (_hostTabBtn != null)
            {
                var hColors = _hostTabBtn.colors;
                hColors.normalColor = hostTab ? UITheme.TabActive : UITheme.TabInactive;
                hColors.highlightedColor = hostTab ? UITheme.TabActive : UITheme.TabInactive;
                hColors.pressedColor = hostTab ? UITheme.TabActive : UITheme.TabInactive;
                hColors.selectedColor = hostTab ? UITheme.TabActive : UITheme.TabInactive;
                _hostTabBtn.colors = hColors;
                _hostTabBtn.targetGraphic.color = hostTab ? UITheme.TabActive : UITheme.TabInactive;
            }
            if (_joinTabBtn != null)
            {
                var jColors = _joinTabBtn.colors;
                jColors.normalColor = hostTab ? UITheme.TabInactive : UITheme.TabActive;
                jColors.highlightedColor = hostTab ? UITheme.TabInactive : UITheme.TabActive;
                jColors.pressedColor = hostTab ? UITheme.TabInactive : UITheme.TabActive;
                jColors.selectedColor = hostTab ? UITheme.TabInactive : UITheme.TabActive;
                _joinTabBtn.colors = jColors;
                _joinTabBtn.targetGraphic.color = hostTab ? UITheme.TabInactive : UITheme.TabActive;
            }

            if (_hostTabLabel != null)
                _hostTabLabel.color = hostTab ? UITheme.SulfurYellow : UITheme.TextSecondary;
            if (_joinTabLabel != null)
                _joinTabLabel.color = hostTab ? UITheme.TextSecondary : UITheme.SulfurYellow;
        }

        private void BuildHostTabContent(Transform parent)
        {
            _hostTabContent = new GameObject("HostTabContent", typeof(RectTransform));
            _hostTabContent.transform.SetParent(parent, false);
            var layout = UIBuilder.AddVerticalLayout(_hostTabContent, 8, new RectOffset(0, 0, 8, 0));
            layout.childForceExpandHeight = false;
            var le = _hostTabContent.AddComponent<LayoutElement>();
            le.flexibleHeight = 1f;

            // Max Players label + input
            UIBuilder.CreateText(_hostTabContent.transform, "MaxPlayersLabel", "Max Players",
                13f, TextAlignmentOptions.Left, UITheme.TextSecondary);

            _hostMaxPlayersInput = UIBuilder.CreateInputField(_hostTabContent.transform, "MaxPlayers",
                "4", 14f, false);
            _hostMaxPlayersInput.contentType = TMP_InputField.ContentType.IntegerNumber;
            _hostMaxPlayersInput.text = MultiplayerConfig.MaxPlayers.Value.ToString();
            UIBuilder.SetPreferredHeight(_hostMaxPlayersInput.gameObject, 34);

            // Password label + input
            UIBuilder.CreateText(_hostTabContent.transform, "PwLabel", "Password",
                13f, TextAlignmentOptions.Left, UITheme.TextSecondary);

            _hostPasswordInput = UIBuilder.CreateInputField(_hostTabContent.transform, "HostPassword",
                "No password", 14f, false);
            UIBuilder.SetPreferredHeight(_hostPasswordInput.gameObject, 34);

            // Spacer
            var spacer = new GameObject("Spacer", typeof(RectTransform));
            spacer.transform.SetParent(_hostTabContent.transform, false);
            var spacerLe = spacer.AddComponent<LayoutElement>();
            spacerLe.preferredHeight = 8;

            // Create lobby button — SulfurYellow bg with dark text
            var createBtn = UIBuilder.CreateButton(_hostTabContent.transform, "CreateLobbyBtn",
                "Create Lobby", 16f, OnCreateLobby,
                UITheme.SulfurYellow, UITheme.SulfurYellow);
            var createLabel = createBtn.GetComponentInChildren<TextMeshProUGUI>();
            if (createLabel != null)
                createLabel.color = DarkText;
            UIBuilder.SetPreferredHeight(createBtn.gameObject, 40);
        }

        private void BuildJoinTabContent(Transform parent)
        {
            _joinTabContent = new GameObject("JoinTabContent", typeof(RectTransform));
            _joinTabContent.transform.SetParent(parent, false);
            var layout = UIBuilder.AddVerticalLayout(_joinTabContent, 8, new RectOffset(0, 0, 8, 0));
            layout.childForceExpandHeight = false;
            var le = _joinTabContent.AddComponent<LayoutElement>();
            le.flexibleHeight = 1f;

            // Steam ID label + input
            UIBuilder.CreateText(_joinTabContent.transform, "IdLabel", "Steam ID",
                13f, TextAlignmentOptions.Left, UITheme.TextSecondary);

            _joinSteamIdInput = UIBuilder.CreateInputField(_joinTabContent.transform, "SteamIdInput",
                "Steam ID or Lobby ID", 14f);
            UIBuilder.SetPreferredHeight(_joinSteamIdInput.gameObject, 34);

            // Spacer
            var spacer = new GameObject("Spacer", typeof(RectTransform));
            spacer.transform.SetParent(_joinTabContent.transform, false);
            var spacerLe = spacer.AddComponent<LayoutElement>();
            spacerLe.preferredHeight = 8;

            // Join button — SulfurYellow bg with dark text
            var joinBtn = UIBuilder.CreateButton(_joinTabContent.transform, "JoinBtn", "Join",
                16f, OnJoinBySteamId,
                UITheme.SulfurYellow, UITheme.SulfurYellow);
            var joinLabel = joinBtn.GetComponentInChildren<TextMeshProUGUI>();
            if (joinLabel != null)
                joinLabel.color = DarkText;
            UIBuilder.SetPreferredHeight(joinBtn.gameObject, 40);
        }

        private void BuildConnectedSection(Transform parent)
        {
            UIBuilder.CreateText(parent, "ConnInfoLabel", "Connection Info",
                18f, TextAlignmentOptions.Left, UITheme.SulfurYellow);

            _connectionStatusText = UIBuilder.CreateText(parent, "ConnectionStatus", "",
                14f, TextAlignmentOptions.Left, UITheme.TextSecondary);

            // Player list header
            UIBuilder.CreateText(parent, "PlayersLabel", "Players:",
                14f, TextAlignmentOptions.Left, UITheme.TextPrimary);

            // Scrollable player list
            RectTransform playerContent;
            var playerScroll = UIBuilder.CreateScrollView(parent, "PlayerList", out playerContent);
            _playerListContent = playerContent;
            UIBuilder.SetPreferredHeight(playerScroll.gameObject, 150);
            var playerScrollLe = playerScroll.gameObject.GetComponent<LayoutElement>() ??
                                 playerScroll.gameObject.AddComponent<LayoutElement>();
            playerScrollLe.flexibleHeight = 1f;

            // Spacer
            var spacer = new GameObject("Spacer", typeof(RectTransform));
            spacer.transform.SetParent(parent, false);
            var spacerLe = spacer.AddComponent<LayoutElement>();
            spacerLe.flexibleHeight = 1f;

            // Disconnect button
            var disconnectBtn = UIBuilder.CreateButton(parent, "DisconnectBtn", "Disconnect",
                16f, OnDisconnect, UITheme.Danger, UITheme.Danger);
            UIBuilder.SetPreferredHeight(disconnectBtn.gameObject, 40);
        }

        private void BuildRightColumn(Transform parent)
        {
            // Header row
            var headerRow = new GameObject("BrowserHeader", typeof(RectTransform));
            headerRow.transform.SetParent(parent, false);
            var headerLayout = UIBuilder.AddHorizontalLayout(headerRow, 10);
            headerLayout.childForceExpandWidth = false;
            headerLayout.childAlignment = TextAnchor.MiddleLeft;
            UIBuilder.SetPreferredHeight(headerRow, 32);

            UIBuilder.CreateText(headerRow.transform, "BrowserLabel", "Lobby Browser",
                18f, TextAlignmentOptions.Left, UITheme.SulfurYellow);

            // Spacer to push refresh right
            var hSpacer = new GameObject("Spacer", typeof(RectTransform));
            hSpacer.transform.SetParent(headerRow.transform, false);
            UIBuilder.SetFlexibleWidth(hSpacer);

            _lobbyCountText = UIBuilder.CreateText(headerRow.transform, "LobbyCount", "",
                13f, TextAlignmentOptions.Right, UITheme.TextSecondary);
            UIBuilder.SetPreferredWidth(_lobbyCountText.gameObject, 80);

            _refreshButton = UIBuilder.CreateButton(headerRow.transform, "RefreshBtn", "Refresh",
                13f, OnRefreshLobbies);
            _refreshButtonLabel = _refreshButton.GetComponentInChildren<TextMeshProUGUI>();
            UIBuilder.SetPreferredWidth(_refreshButton.gameObject, 90);
            UIBuilder.SetPreferredHeight(_refreshButton.gameObject, 30);

            // Filter input
            _filterInput = UIBuilder.CreateInputField(parent, "FilterInput", "Search by host name...", 14f);
            UIBuilder.SetPreferredHeight(_filterInput.gameObject, 32);
            _filterInput.onValueChanged.AddListener((_) => ApplyFilter());

            // Column headers: Host | Players | Ping | (Join btn space)
            var colHeaders = new GameObject("ColumnHeaders", typeof(RectTransform));
            colHeaders.transform.SetParent(parent, false);
            var colLayout = UIBuilder.AddHorizontalLayout(colHeaders, 8, new RectOffset(12, 12, 0, 0));
            colLayout.childForceExpandWidth = false;
            UIBuilder.SetPreferredHeight(colHeaders, 22);

            var hHost = UIBuilder.CreateText(colHeaders.transform, "HHost", "Host",
                13f, TextAlignmentOptions.Left, UITheme.TextSecondary);
            UIBuilder.SetFlexibleWidth(hHost.gameObject);

            var hPlayers = UIBuilder.CreateText(colHeaders.transform, "HPlayers", "Players",
                13f, TextAlignmentOptions.Center, UITheme.TextSecondary);
            UIBuilder.SetPreferredWidth(hPlayers.gameObject, 70);

            var hPing = UIBuilder.CreateText(colHeaders.transform, "HPing", "Ping",
                13f, TextAlignmentOptions.Center, UITheme.TextSecondary);
            UIBuilder.SetPreferredWidth(hPing.gameObject, 60);

            var hAction = UIBuilder.CreateText(colHeaders.transform, "HAction", "",
                13f, TextAlignmentOptions.Center, UITheme.TextSecondary);
            UIBuilder.SetPreferredWidth(hAction.gameObject, 70);

            UIBuilder.CreateSeparator(parent);

            // Lobby list scroll view
            RectTransform lobbyContent;
            var lobbyScroll = UIBuilder.CreateScrollView(parent, "LobbyList", out lobbyContent);
            _lobbyListContent = lobbyContent;
            var lobbyScrollLe = lobbyScroll.gameObject.GetComponent<LayoutElement>() ??
                                lobbyScroll.gameObject.AddComponent<LayoutElement>();
            lobbyScrollLe.flexibleHeight = 1f;
        }

        private void BuildPasswordDialog()
        {
            // Overlay covers entire canvas
            _passwordDialog = UIBuilder.CreatePanel(_canvas.transform, "PasswordDialog",
                new Color(0, 0, 0, 0.7f));
            var overlayRt = _passwordDialog.GetComponent<RectTransform>();
            UIBuilder.SetStretch(overlayRt);

            // Dialog box centered
            var dialogBox = UIBuilder.CreatePanel(_passwordDialog.transform, "DialogBox",
                new Color(0.1f, 0.1f, 0.1f, 0.98f));
            var dialogRt = dialogBox.GetComponent<RectTransform>();
            dialogRt.anchorMin = new Vector2(0.5f, 0.5f);
            dialogRt.anchorMax = new Vector2(0.5f, 0.5f);
            dialogRt.pivot = new Vector2(0.5f, 0.5f);
            dialogRt.sizeDelta = new Vector2(400, 200);

            UIBuilder.AddVerticalLayout(dialogBox, 12, new RectOffset(24, 24, 20, 20));

            UIBuilder.CreateText(dialogBox.transform, "Title", "Enter Password",
                20f, TextAlignmentOptions.Center, UITheme.SulfurYellow);

            _passwordDialogInput = UIBuilder.CreateInputField(dialogBox.transform, "PasswordInput",
                "Password", 16f, true);
            UIBuilder.SetPreferredHeight(_passwordDialogInput.gameObject, 38);

            // Button row
            var btnRow = new GameObject("ButtonRow", typeof(RectTransform));
            btnRow.transform.SetParent(dialogBox.transform, false);
            var btnLayout = UIBuilder.AddHorizontalLayout(btnRow, 12);
            btnLayout.childForceExpandWidth = true;
            UIBuilder.SetPreferredHeight(btnRow, 40);

            UIBuilder.CreateButton(btnRow.transform, "CancelBtn", "Cancel", 15f,
                HidePasswordDialog);

            var confirmBtn = UIBuilder.CreateButton(btnRow.transform, "ConfirmBtn", "Join", 15f,
                OnPasswordDialogConfirm, UITheme.SulfurYellow, UITheme.SulfurYellow);
            var confirmLabel = confirmBtn.GetComponentInChildren<TextMeshProUGUI>();
            if (confirmLabel != null)
                confirmLabel.color = DarkText;

            _passwordDialog.SetActive(false);
        }

        #endregion

        #region View Refresh

        private void RefreshView()
        {
            var nm = NetworkManager.Instance;
            bool connected = nm != null && nm.IsConnected;

            if (_disconnectedSection != null)
                _disconnectedSection.SetActive(!connected);
            if (_connectedSection != null)
                _connectedSection.SetActive(connected);

            if (connected)
                RefreshConnectionInfo();
        }

        private void RefreshConnectionInfo()
        {
            var nm = NetworkManager.Instance;
            var lm = LobbyManager.Instance;
            if (nm == null || lm == null) return;

            if (_connectionStatusText != null)
            {
                string role = nm.IsHost ? "Host" : "Client";
                int memberCount = lm.InLobby ? SteamMatchmaking.GetNumLobbyMembers(lm.CurrentLobbyId) : 0;
                string pingStr = nm.PingMs > 0 ? $"  |  Ping: {nm.PingMs}ms" : "";
                string lobbyInfo = lm.InLobby ? $"\nLobby: {lm.CurrentLobbyId}" : "";
                _connectionStatusText.text = $"Role: {role}  |  Players: {memberCount}{pingStr}{lobbyInfo}";
            }

            // Rebuild player list from lobby membership
            if (_playerListContent != null)
            {
                // Clear existing entries
                for (int i = _playerListContent.childCount - 1; i >= 0; i--)
                    Destroy(_playerListContent.GetChild(i).gameObject);

                if (lm.InLobby)
                {
                    int count = SteamMatchmaking.GetNumLobbyMembers(lm.CurrentLobbyId);
                    var localId = SteamUser.GetSteamID();
                    for (int i = 0; i < count; i++)
                    {
                        var memberId = SteamMatchmaking.GetLobbyMemberByIndex(lm.CurrentLobbyId, i);
                        bool isLocal = memberId == localId;
                        string name = SteamFriends.GetFriendPersonaName(memberId);
                        string display = isLocal ? $"  {name} (You)" : $"  {name}";
                        var color = isLocal ? UITheme.SulfurYellow : UITheme.TextPrimary;
                        var text = UIBuilder.CreateText(_playerListContent, $"Player_{memberId}",
                            display, 14f, TextAlignmentOptions.Left, color);
                        UIBuilder.SetPreferredHeight(text.gameObject, 24);
                    }
                }
            }
        }

        #endregion

        #region Lobby Browser

        private void OnLobbySearchComplete(IReadOnlyList<CSteamID> lobbies)
        {
            _cachedLobbies.Clear();
            var lm = LobbyManager.Instance;
            if (lm == null) return;

            foreach (var lobbyId in lobbies)
            {
                var info = lm.GetLobbyInfo(lobbyId);
                _cachedLobbies.Add(info);
            }

            ApplyFilter();
            UpdateRefreshButton(false);
        }

        private void ApplyFilter()
        {
            if (_lobbyListContent == null) return;

            // Clear existing entries
            for (int i = _lobbyListContent.childCount - 1; i >= 0; i--)
                Destroy(_lobbyListContent.GetChild(i).gameObject);

            string filter = _filterInput != null ? _filterInput.text.Trim() : "";
            int shown = 0;

            foreach (var info in _cachedLobbies)
            {
                // Skip self
                if (LobbyManager.Instance != null &&
                    info.LobbyId == LobbyManager.Instance.CurrentLobbyId)
                    continue;

                // Apply text filter
                if (!string.IsNullOrEmpty(filter) &&
                    (info.HostName == null || info.HostName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0))
                    continue;

                CreateLobbyEntry(info);
                shown++;
            }

            if (_lobbyCountText != null)
                _lobbyCountText.text = $"{shown} found";
        }

        private void CreateLobbyEntry(LobbyInfo info)
        {
            var entryGo = new GameObject("LobbyEntry", typeof(RectTransform));
            entryGo.transform.SetParent(_lobbyListContent, false);

            var entryImg = entryGo.AddComponent<Image>();
            entryImg.sprite = UITheme.WhiteSprite;
            entryImg.color = UITheme.LobbyEntryBg;

            UIBuilder.SetPreferredHeight(entryGo, 36);

            var entryLayout = UIBuilder.AddHorizontalLayout(entryGo, 8, new RectOffset(12, 8, 4, 4));
            entryLayout.childForceExpandWidth = false;
            entryLayout.childAlignment = TextAnchor.MiddleLeft;

            // Lock icon + host name
            string hostDisplay = info.HasPassword ? "[PW] " : "";
            hostDisplay += string.IsNullOrEmpty(info.HostName) ? "Unknown" : info.HostName;

            var hostText = UIBuilder.CreateText(entryGo.transform, "HostName", hostDisplay,
                14f, TextAlignmentOptions.Left, UITheme.TextPrimary);
            UIBuilder.SetFlexibleWidth(hostText.gameObject);

            // Player count — colored green if open, red if full
            bool isFull = info.PlayerCount >= info.MaxPlayers;
            var countColor = isFull ? UITheme.PlayerCountFull : UITheme.PlayerCountOpen;
            var countText = UIBuilder.CreateText(entryGo.transform, "PlayerCount",
                $"{info.PlayerCount}/{info.MaxPlayers}", 14f, TextAlignmentOptions.Center, countColor);
            UIBuilder.SetPreferredWidth(countText.gameObject, 70);

            // Ping — estimated via Steam relay network
            string pingDisplay = info.EstimatedPingMs >= 0 ? $"{info.EstimatedPingMs}ms" : "\u2014";
            var pingText = UIBuilder.CreateText(entryGo.transform, "Ping",
                pingDisplay, 14f, TextAlignmentOptions.Center, UITheme.TextSecondary);
            UIBuilder.SetPreferredWidth(pingText.gameObject, 60);

            // Join button — SulfurYellow bg with dark text
            var lobbyId = info.LobbyId;
            bool hasPassword = info.HasPassword;
            var joinBtn = UIBuilder.CreateButton(entryGo.transform, "JoinBtn", "Join",
                13f, () => OnJoinLobbyEntry(lobbyId, hasPassword),
                UITheme.SulfurYellow, UITheme.SulfurYellow);
            var joinLabel = joinBtn.GetComponentInChildren<TextMeshProUGUI>();
            if (joinLabel != null)
                joinLabel.color = DarkText;
            UIBuilder.SetPreferredWidth(joinBtn.gameObject, 70);
            UIBuilder.SetPreferredHeight(joinBtn.gameObject, 28);
        }

        private void UpdateRefreshButton(bool searching)
        {
            if (_refreshButtonLabel != null)
                _refreshButtonLabel.text = searching ? "..." : "Refresh";
            if (_refreshButton != null)
                _refreshButton.interactable = !searching;
        }

        #endregion

        #region Password Dialog

        private void ShowPasswordDialog(CSteamID lobbyId)
        {
            _pendingJoinLobbyId = lobbyId;
            if (_passwordDialogInput != null)
                _passwordDialogInput.text = "";
            if (_passwordDialog != null)
                _passwordDialog.SetActive(true);
        }

        private void HidePasswordDialog()
        {
            if (_passwordDialog != null)
                _passwordDialog.SetActive(false);
            _pendingJoinLobbyId = CSteamID.Nil;
        }

        private void OnPasswordDialogConfirm()
        {
            var lm = LobbyManager.Instance;
            if (lm == null) return;

            string password = _passwordDialogInput != null ? _passwordDialogInput.text : "";
            lm.PendingJoinPassword = password;
            lm.JoinLobby(_pendingJoinLobbyId);
            SetStatus("Joining...");
            HidePasswordDialog();
        }

        #endregion

        #region Actions

        private void OnCreateLobby()
        {
            var lm = LobbyManager.Instance;
            if (lm == null) return;

            // Parse max players from input, clamp to valid range
            int maxPlayers = MultiplayerConfig.MaxPlayers.Value;
            if (_hostMaxPlayersInput != null && int.TryParse(_hostMaxPlayersInput.text.Trim(), out int parsed))
                maxPlayers = Mathf.Clamp(parsed, 2, 250);

            lm.LobbyPassword = _hostPasswordInput != null ? _hostPasswordInput.text.Trim() : "";
            lm.CreateLobby(maxPlayers);
            SetStatus($"Creating lobby ({maxPlayers} players)...");
        }

        private void OnJoinBySteamId()
        {
            var lm = LobbyManager.Instance;
            if (lm == null) return;

            string input = _joinSteamIdInput != null ? _joinSteamIdInput.text.Trim() : "";
            if (ulong.TryParse(input, out ulong steamId))
            {
                lm.PendingJoinPassword = "";
                lm.JoinLobby(new CSteamID(steamId));
                SetStatus("Joining...");
            }
            else
            {
                SetStatus("Invalid Steam ID");
            }
        }

        private void OnJoinLobbyEntry(CSteamID lobbyId, bool hasPassword)
        {
            if (hasPassword)
            {
                ShowPasswordDialog(lobbyId);
            }
            else
            {
                var lm = LobbyManager.Instance;
                if (lm == null) return;
                lm.PendingJoinPassword = "";
                lm.JoinLobby(lobbyId);
                SetStatus("Joining...");
            }
        }

        private void OnRefreshLobbies()
        {
            var lm = LobbyManager.Instance;
            if (lm == null || lm.IsSearching) return;

            UpdateRefreshButton(true);
            lm.SearchLobbies();
        }

        private void OnDisconnect()
        {
            LobbyManager.Instance?.LeaveLobby();
            SetStatus("Disconnected");
            RefreshView();
        }

        #endregion

        #region Event Handlers

        private void OnNetConnected()
        {
            SetStatus("Connected!");
            RefreshView();
        }

        private void OnNetDisconnected(string reason)
        {
            SetStatus($"Disconnected: {reason}");
            RefreshView();
        }

        #endregion

        #region Helpers

        private void SetStatus(string message)
        {
            if (_statusText != null)
                _statusText.text = message;
            _statusTimer = 5f;
        }

        #endregion
    }
}
