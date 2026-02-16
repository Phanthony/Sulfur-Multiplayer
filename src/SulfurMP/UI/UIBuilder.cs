using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SulfurMP.UI
{
    /// <summary>
    /// Static helpers for programmatic UGUI construction — reduces boilerplate.
    /// </summary>
    public static class UIBuilder
    {
        /// <summary>
        /// Create a ScreenSpace-Overlay Canvas with CanvasScaler (1920x1080 ref) + GraphicRaycaster.
        /// </summary>
        public static Canvas CreateCanvas(string name, int sortOrder)
        {
            var go = new GameObject(name);
            go.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(go);

            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortOrder;

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            go.AddComponent<GraphicRaycaster>();

            return canvas;
        }

        /// <summary>
        /// Create a panel with Image background and CanvasGroup.
        /// </summary>
        public static GameObject CreatePanel(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var img = go.AddComponent<Image>();
            img.sprite = UITheme.WhiteSprite;
            img.color = color;
            img.type = Image.Type.Sliced;

            go.AddComponent<CanvasGroup>();
            return go;
        }

        /// <summary>
        /// Create a TextMeshProUGUI element.
        /// </summary>
        public static TextMeshProUGUI CreateText(Transform parent, string name, string text,
            float fontSize = 16f, TextAlignmentOptions align = TextAlignmentOptions.Left,
            Color? color = null)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.alignment = align;
            tmp.color = color ?? UITheme.TextPrimary;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            tmp.enableWordWrapping = true;

            if (UITheme.Font != null)
                tmp.font = UITheme.Font;

            return tmp;
        }

        /// <summary>
        /// Create a Button with Image background and TMP label.
        /// </summary>
        public static Button CreateButton(Transform parent, string name, string label,
            float fontSize = 16f, Action onClick = null, Color? normalColor = null, Color? hoverColor = null)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var img = go.AddComponent<Image>();
            img.sprite = UITheme.WhiteSprite;
            img.color = normalColor ?? UITheme.ButtonNormal;
            img.type = Image.Type.Sliced;

            var btn = go.AddComponent<Button>();
            var colors = btn.colors;
            colors.normalColor = normalColor ?? UITheme.ButtonNormal;
            colors.highlightedColor = hoverColor ?? UITheme.ButtonHover;
            colors.pressedColor = hoverColor ?? UITheme.ButtonHover;
            colors.selectedColor = normalColor ?? UITheme.ButtonNormal;
            colors.fadeDuration = 0.1f;
            btn.colors = colors;
            btn.targetGraphic = img;

            if (onClick != null)
                btn.onClick.AddListener(() => onClick());

            // Label
            var textGo = new GameObject("Label", typeof(RectTransform));
            textGo.transform.SetParent(go.transform, false);
            SetStretch(textGo.GetComponent<RectTransform>());

            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = fontSize;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = UITheme.TextPrimary;
            if (UITheme.Font != null)
                tmp.font = UITheme.Font;

            return btn;
        }

        /// <summary>
        /// Create a TMP_InputField with placeholder and text area.
        /// </summary>
        public static TMP_InputField CreateInputField(Transform parent, string name,
            string placeholder = "", float fontSize = 16f, bool isPassword = false)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var bgImg = go.AddComponent<Image>();
            bgImg.sprite = UITheme.WhiteSprite;
            bgImg.color = UITheme.InputBg;
            bgImg.type = Image.Type.Sliced;

            // Text Area (viewport with mask)
            var textAreaGo = new GameObject("Text Area", typeof(RectTransform));
            textAreaGo.transform.SetParent(go.transform, false);
            var textAreaRt = textAreaGo.GetComponent<RectTransform>();
            SetStretch(textAreaRt);
            textAreaRt.offsetMin = new Vector2(10, 2);
            textAreaRt.offsetMax = new Vector2(-10, -2);
            textAreaGo.AddComponent<RectMask2D>();

            // Placeholder text
            var phGo = new GameObject("Placeholder", typeof(RectTransform));
            phGo.transform.SetParent(textAreaGo.transform, false);
            SetStretch(phGo.GetComponent<RectTransform>());
            var phTmp = phGo.AddComponent<TextMeshProUGUI>();
            phTmp.text = placeholder;
            phTmp.fontSize = fontSize;
            phTmp.fontStyle = FontStyles.Italic;
            phTmp.color = new Color(0.5f, 0.5f, 0.5f, 0.6f);
            phTmp.enableWordWrapping = false;
            phTmp.overflowMode = TextOverflowModes.Ellipsis;
            if (UITheme.Font != null)
                phTmp.font = UITheme.Font;

            // Actual text
            var txtGo = new GameObject("Text", typeof(RectTransform));
            txtGo.transform.SetParent(textAreaGo.transform, false);
            SetStretch(txtGo.GetComponent<RectTransform>());
            var txtTmp = txtGo.AddComponent<TextMeshProUGUI>();
            txtTmp.text = "";
            txtTmp.fontSize = fontSize;
            txtTmp.color = UITheme.TextPrimary;
            txtTmp.enableWordWrapping = false;
            txtTmp.overflowMode = TextOverflowModes.Ellipsis;
            if (UITheme.Font != null)
                txtTmp.font = UITheme.Font;

            // Input field component
            var inputField = go.AddComponent<TMP_InputField>();
            inputField.textViewport = textAreaRt;
            inputField.textComponent = txtTmp;
            inputField.placeholder = phTmp;
            inputField.fontAsset = UITheme.Font;
            inputField.pointSize = fontSize;
            inputField.caretColor = UITheme.SulfurYellow;
            inputField.selectionColor = new Color(0.98f, 0.66f, 0.25f, 0.3f);

            if (isPassword)
            {
                inputField.contentType = TMP_InputField.ContentType.Password;
                inputField.inputType = TMP_InputField.InputType.Password;
            }

            return inputField;
        }

        /// <summary>
        /// Create a ScrollRect with viewport (RectMask2D) and content panel (VerticalLayout).
        /// Returns the content Transform (add children to it).
        /// </summary>
        public static ScrollRect CreateScrollView(Transform parent, string name, out RectTransform content)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            // Viewport
            var viewportGo = new GameObject("Viewport", typeof(RectTransform));
            viewportGo.transform.SetParent(go.transform, false);
            var viewportRt = viewportGo.GetComponent<RectTransform>();
            SetStretch(viewportRt);
            viewportGo.AddComponent<RectMask2D>();
            var viewportImg = viewportGo.AddComponent<Image>();
            viewportImg.color = Color.clear;

            // Content
            var contentGo = new GameObject("Content", typeof(RectTransform));
            contentGo.transform.SetParent(viewportGo.transform, false);
            var contentRt = contentGo.GetComponent<RectTransform>();
            contentRt.anchorMin = new Vector2(0, 1);
            contentRt.anchorMax = new Vector2(1, 1);
            contentRt.pivot = new Vector2(0.5f, 1);
            contentRt.sizeDelta = new Vector2(0, 0);

            var vlg = contentGo.AddComponent<VerticalLayoutGroup>();
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.spacing = 2;

            var csf = contentGo.AddComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // Scrollbar
            var scrollbarGo = new GameObject("Scrollbar", typeof(RectTransform));
            scrollbarGo.transform.SetParent(go.transform, false);
            var scrollbarRt = scrollbarGo.GetComponent<RectTransform>();
            scrollbarRt.anchorMin = new Vector2(1, 0);
            scrollbarRt.anchorMax = new Vector2(1, 1);
            scrollbarRt.pivot = new Vector2(1, 0.5f);
            scrollbarRt.sizeDelta = new Vector2(8, 0);

            var scrollbarBg = scrollbarGo.AddComponent<Image>();
            scrollbarBg.sprite = UITheme.WhiteSprite;
            scrollbarBg.color = UITheme.ScrollbarBg;

            // Scrollbar handle
            var handleGo = new GameObject("Handle", typeof(RectTransform));
            handleGo.transform.SetParent(scrollbarGo.transform, false);
            SetStretch(handleGo.GetComponent<RectTransform>());
            var handleImg = handleGo.AddComponent<Image>();
            handleImg.sprite = UITheme.WhiteSprite;
            handleImg.color = UITheme.ScrollbarHandle;

            var scrollbar = scrollbarGo.AddComponent<Scrollbar>();
            scrollbar.handleRect = handleGo.GetComponent<RectTransform>();
            scrollbar.targetGraphic = handleImg;
            scrollbar.direction = Scrollbar.Direction.BottomToTop;

            // Adjust viewport to leave room for scrollbar
            viewportRt.offsetMax = new Vector2(-12, 0);

            // ScrollRect
            var scrollRect = go.AddComponent<ScrollRect>();
            scrollRect.viewport = viewportRt;
            scrollRect.content = contentRt;
            scrollRect.verticalScrollbar = scrollbar;
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.scrollSensitivity = 30f;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;

            content = contentRt;
            return scrollRect;
        }

        /// <summary>
        /// Add a VerticalLayoutGroup to a GameObject.
        /// </summary>
        public static VerticalLayoutGroup AddVerticalLayout(GameObject go, float spacing = 0,
            RectOffset padding = null)
        {
            var vlg = go.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = spacing;
            vlg.padding = padding ?? new RectOffset(0, 0, 0, 0);
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            return vlg;
        }

        /// <summary>
        /// Add a HorizontalLayoutGroup to a GameObject.
        /// </summary>
        public static HorizontalLayoutGroup AddHorizontalLayout(GameObject go, float spacing = 0,
            RectOffset padding = null)
        {
            var hlg = go.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = spacing;
            hlg.padding = padding ?? new RectOffset(0, 0, 0, 0);
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            return hlg;
        }

        /// <summary>
        /// Set RectTransform to stretch-fill parent.
        /// </summary>
        public static void SetStretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        /// <summary>
        /// Add a LayoutElement with preferred height.
        /// </summary>
        public static LayoutElement SetPreferredHeight(GameObject go, float height)
        {
            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            if (le.flexibleHeight < 0)
                le.flexibleHeight = 0;
            return le;
        }

        /// <summary>
        /// Add a LayoutElement with preferred width.
        /// </summary>
        public static LayoutElement SetPreferredWidth(GameObject go, float width)
        {
            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.preferredWidth = width;
            return le;
        }

        /// <summary>
        /// Add a LayoutElement with flexible width.
        /// </summary>
        public static LayoutElement SetFlexibleWidth(GameObject go, float flex = 1f)
        {
            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.flexibleWidth = flex;
            return le;
        }

        /// <summary>
        /// Create a horizontal separator line.
        /// </summary>
        public static GameObject CreateSeparator(Transform parent, float height = 1f)
        {
            var go = new GameObject("Separator", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.sprite = UITheme.WhiteSprite;
            img.color = UITheme.Separator;
            SetPreferredHeight(go, height);
            return go;
        }
    }
}
