using System.Collections.Generic;
using SulfurMP.Config;
using SulfurMP.Entities;
using SulfurMP.Networking;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SulfurMP.UI
{
    /// <summary>
    /// IMGUI debug overlay (toggle with F3).
    /// Shows connection state, peer list, network stats, and message log.
    /// </summary>
    public class DebugOverlay : MonoBehaviour
    {
        private bool _visible;
        private readonly List<string> _messageLog = new List<string>();
        private const int MaxLogEntries = 50;
        private Vector2 _logScrollPos;

        // Disconnect notification (always visible, independent of F9 toggle)
        private string _disconnectNotification;
        private float _disconnectNotificationTimer;

        private void Start()
        {
            _visible = MultiplayerConfig.ShowDebugOverlay.Value;
            NetworkEvents.OnMessageReceived += OnMessageReceived;
            NetworkEvents.OnConnected += () => LogMessage("[EVENT] Connected");
            NetworkEvents.OnDisconnected += reason =>
            {
                LogMessage($"[EVENT] Disconnected: {reason}");
                _disconnectNotification = $"DISCONNECTED: {reason}";
                _disconnectNotificationTimer = 5f;
            };
            NetworkEvents.OnPeerJoined += peer => LogMessage($"[EVENT] Peer joined: {peer}");
            NetworkEvents.OnPeerLeft += peer => LogMessage($"[EVENT] Peer left: {peer}");
        }

        private void OnDestroy()
        {
            NetworkEvents.OnMessageReceived -= OnMessageReceived;
        }

        private void Update()
        {
            if (Keyboard.current != null && Keyboard.current.f9Key.wasPressedThisFrame)
                _visible = !_visible;

            if (_disconnectNotificationTimer > 0f)
                _disconnectNotificationTimer -= Time.unscaledDeltaTime;
        }

        private void OnGUI()
        {
            // Disconnect notification banner — always visible, independent of F9 toggle
            if (_disconnectNotificationTimer > 0f && !string.IsNullOrEmpty(_disconnectNotification))
            {
                var bannerStyle = new GUIStyle(GUI.skin.box)
                {
                    fontSize = 20,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter,
                    normal = { textColor = Color.white, background = MakeTex(1, 1, new Color(0.8f, 0f, 0f, 0.9f)) },
                };
                float bannerHeight = 40f;
                float bannerWidth = 500f;
                GUI.Box(
                    new Rect((Screen.width - bannerWidth) / 2f, 10f, bannerWidth, bannerHeight),
                    _disconnectNotification, bannerStyle);
            }

            if (!_visible) return;

            float width = 320f;
            float x = Screen.width - width - 10f;
            float y = 10f;

            GUILayout.BeginArea(new Rect(x, y, width, Screen.height - 20f));

            var bgStyle = new GUIStyle(GUI.skin.box) { normal = { background = MakeTex(1, 1, new Color(0f, 0f, 0f, 0.8f)) } };
            GUILayout.BeginVertical(bgStyle);

            GUILayout.Label("<b>SulfurMP Debug</b>", RichLabel());

            DrawConnectionInfo();
            DrawNetworkStats();
            DrawPeerList();
            DrawEntityInfo();
            DrawMessageLog();

            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        private void DrawConnectionInfo()
        {
            var nm = NetworkManager.Instance;
            if (nm == null)
            {
                GUILayout.Label("NetworkManager: null");
                return;
            }

            GUILayout.Label($"State: <b>{nm.State}</b>", RichLabel());
            GUILayout.Label($"Role: {(nm.IsHost ? "HOST" : "CLIENT")}");
            GUILayout.Label($"SteamID: {nm.LocalSteamId}");

            var lm = LobbyManager.Instance;
            if (lm != null && lm.InLobby)
            {
                GUILayout.Label($"Lobby: {lm.CurrentLobbyId}");
            }
        }

        private void DrawNetworkStats()
        {
            var nm = NetworkManager.Instance;
            if (nm == null || !nm.IsConnected) return;

            GUILayout.Space(5);
            GUILayout.Label("<b>Network Stats</b>", RichLabel());
            GUILayout.Label($"Sent: {nm.MessagesSentPerSecond} msg/s ({FormatBytes(nm.BytesSentPerSecond)}/s)");
            GUILayout.Label($"Recv: {nm.MessagesReceivedPerSecond} msg/s ({FormatBytes(nm.BytesReceivedPerSecond)}/s)");
        }

        private void DrawPeerList()
        {
            var nm = NetworkManager.Instance;
            if (nm == null || nm.ConnectedPeers.Count == 0) return;

            GUILayout.Space(5);
            GUILayout.Label($"<b>Peers ({nm.ConnectedPeers.Count})</b>", RichLabel());
            foreach (var peer in nm.ConnectedPeers)
            {
                GUILayout.Label($"  {peer}");
            }
        }

        private void DrawEntityInfo()
        {
            var esm = EntitySyncManager.Instance;
            if (esm == null) return;

            var nm = NetworkManager.Instance;
            if (nm == null || !nm.IsConnected) return;

            GUILayout.Space(5);
            GUILayout.Label($"<b>Entities: {esm.Registry.Count}</b>", RichLabel());
        }

        private void DrawMessageLog()
        {
            if (_messageLog.Count == 0) return;

            GUILayout.Space(5);
            GUILayout.Label("<b>Message Log</b>", RichLabel());

            _logScrollPos = GUILayout.BeginScrollView(_logScrollPos, GUILayout.Height(150));
            for (int i = _messageLog.Count - 1; i >= 0; i--)
            {
                GUILayout.Label(_messageLog[i], GUI.skin.label);
            }
            GUILayout.EndScrollView();
        }

        private void OnMessageReceived(Steamworks.CSteamID sender, NetworkMessage message)
        {
            LogMessage($"[{message.Type}] from {sender}");
        }

        private void LogMessage(string msg)
        {
            _messageLog.Add($"[{Time.unscaledTime:F1}] {msg}");
            if (_messageLog.Count > MaxLogEntries)
                _messageLog.RemoveAt(0);
        }

        private static string FormatBytes(int bytes)
        {
            if (bytes < 1024) return $"{bytes}B";
            return $"{bytes / 1024f:F1}KB";
        }

        private static GUIStyle RichLabel()
        {
            var style = new GUIStyle(GUI.skin.label) { richText = true };
            return style;
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
