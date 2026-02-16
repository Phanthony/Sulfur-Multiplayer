using TMPro;
using UnityEngine;

namespace SulfurMP.UI
{
    /// <summary>
    /// Color constants and cached resources for the multiplayer UGUI panel.
    /// </summary>
    public static class UITheme
    {
        public static readonly Color SulfurYellow = new Color(0.98f, 0.66f, 0.25f);
        public static readonly Color PanelBg = new Color(0.06f, 0.06f, 0.06f, 1f);
        public static readonly Color ButtonNormal = new Color(0.12f, 0.12f, 0.12f, 0.95f);
        public static readonly Color ButtonHover = new Color(0.22f, 0.19f, 0.12f, 0.95f);
        public static readonly Color TextPrimary = Color.white;
        public static readonly Color TextSecondary = new Color(0.7f, 0.7f, 0.7f);
        public static readonly Color Danger = new Color(1f, 0.4f, 0.4f);
        public static readonly Color Separator = new Color(0.3f, 0.3f, 0.3f, 0.5f);
        public static readonly Color InputBg = new Color(0.08f, 0.08f, 0.08f, 0.95f);
        public static readonly Color InputBorder = new Color(0.25f, 0.25f, 0.25f, 0.8f);
        public static readonly Color LobbyEntryBg = new Color(0.1f, 0.1f, 0.1f, 0.9f);
        public static readonly Color LobbyEntryHover = new Color(0.18f, 0.15f, 0.1f, 0.95f);
        public static readonly Color ScrollbarBg = new Color(0.08f, 0.08f, 0.08f, 0.5f);
        public static readonly Color ScrollbarHandle = new Color(0.3f, 0.3f, 0.3f, 0.8f);

        public static readonly Color TabActive = new Color(0.18f, 0.15f, 0.08f, 1f);
        public static readonly Color TabInactive = new Color(0.10f, 0.10f, 0.10f, 1f);
        public static readonly Color PlayerCountFull = new Color(1f, 0.4f, 0.4f);
        public static readonly Color PlayerCountOpen = new Color(0.4f, 0.8f, 0.4f);

        public static TMP_FontAsset Font { get; private set; }
        public static Sprite WhiteSprite { get; private set; }

        private static bool _initialized;

        public static void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;

            // Find a TMP font from the game's loaded assets
            var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            if (fonts.Length > 0)
            {
                Font = fonts[0];
                Plugin.Log.LogInfo($"UITheme: Found TMP font '{Font.name}'");
            }
            else
            {
                Plugin.Log.LogWarning("UITheme: No TMP_FontAsset found — text may not render");
            }

            // Create a simple white sprite for Image backgrounds
            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            var pixels = new Color[16];
            for (int i = 0; i < 16; i++) pixels[i] = Color.white;
            tex.SetPixels(pixels);
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            WhiteSprite = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f));
            WhiteSprite.hideFlags = HideFlags.HideAndDontSave;
        }
    }
}
