using System;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace PlayerMimics;

// Asks each player once whether their voice may be cloned. The answer is saved in their own config file.
// Drawn with the game's own font and colours (orange on near-black, "> option" menu buttons) so it fits in.
internal sealed class ConsentPopup : MonoBehaviour
{
    internal static readonly Color Orange = new(1f, 0.42f, 0.11f);
    private static readonly Color dimColor = new(0.98f, 0.62f, 0.38f);
    private static readonly Color menuTint = new(0.42f, 0.15f, 0.05f, 0.75f); // the pause menu's orange-brown wash

    private bool showing;
    private bool lookWasDisabled;
    private bool moveWasDisabled;
    private CursorLockMode oldLock;
    private bool oldVisible;

    private GameObject? root;
    private TextMeshProUGUI? body, storageText, footer;
    private bool shownSaving, shownTraining;

    private bool NeedsAnswer()
    {
        if (Cfg.VoiceConsent.Value == ConsentChoice.Unset) return true;
        // Allowed before recordings could be played back: ask again so they know.
        if (Cfg.VoiceConsent.Value == ConsentChoice.Allow && !Cfg.ConsentIncludesReplay.Value) return true;
        // Allowed before, but this host keeps audio on disk: ask again so they know.
        if (Cfg.VoiceConsent.Value == ConsentChoice.Allow && ModNetwork.HostSavesAudio && !Cfg.ConsentIncludedSaving.Value) return true;
        // Allowed before, but this host collects training data: ask again so they know.
        return Cfg.VoiceConsent.Value == ConsentChoice.Allow && ModNetwork.HostCollectsText && !Cfg.ConsentIncludedTraining.Value;
    }

    private void Update()
    {
        var key = Cfg.ResetConsentKey.Value;
        if (key != Key.None && Keyboard.current != null && Keyboard.current[key].wasPressedThisFrame)
        {
            Cfg.VoiceConsent.Value = ConsentChoice.Unset;
            Cfg.ConsentIncludedSaving.Value = false;
            Cfg.ConsentIncludesReplay.Value = false;
            Cfg.ConsentIncludedTraining.Value = false;
            ModNetwork.Instance?.SendConsent();
            Plugin.Log.LogInfo("Voice cloning consent cleared");
        }

        var me = GameNetworkManager.Instance != null ? GameNetworkManager.Instance.localPlayerController : null;
        bool inLobby = me != null && ModNetwork.Instance != null && ModNetwork.HostStatusKnown;
        bool want = inLobby && NeedsAnswer();
        if (want && !showing) Open(me!);
        else if (!want && showing) Close();
        if (showing)
        {
            // Held every frame: the game clears the look block for everyone when it revives players, which happens
            // around joining, and the camera then turned with the cursor.
            if (me != null)
            {
                me.disableLookInput = true;
                me.disableMoveInput = true;
            }
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            // The host's saving setting can change while it's open.
            if (body != null && (shownSaving != ModNetwork.HostSavesAudio || shownTraining != ModNetwork.HostCollectsText)) SetBody();
        }
    }

    private void Open(GameNetcodeStuff.PlayerControllerB me)
    {
        showing = true;
        oldLock = Cursor.lockState;
        oldVisible = Cursor.visible;
        lookWasDisabled = me.disableLookInput;
        moveWasDisabled = me.disableMoveInput;
        me.disableLookInput = true;
        me.disableMoveInput = true;
        if (root == null) Build(me, onGameCanvas: true);
        SetBody();
        root!.SetActive(true);
        // Hidden under a menu object that's switched off: it would block looking while nobody can see it.
        if (!root.activeInHierarchy)
        {
            Destroy(root);
            Build(me, onGameCanvas: false);
            SetBody();
            root!.SetActive(true);
        }
    }

    private void Close()
    {
        showing = false;
        if (root != null) root.SetActive(false);
        var me = GameNetworkManager.Instance != null ? GameNetworkManager.Instance.localPlayerController : null;
        if (me != null)
        {
            me.disableLookInput = lookWasDisabled;
            me.disableMoveInput = moveWasDisabled;
        }
        bool menuOpen = me != null && me.quickMenuManager != null && me.quickMenuManager.isMenuOpen;
        Cursor.lockState = menuOpen ? CursorLockMode.None : oldLock;
        Cursor.visible = menuOpen || oldVisible;
    }

    private void Choose(bool allow)
    {
        Cfg.VoiceConsent.Value = allow ? ConsentChoice.Allow : ConsentChoice.Deny;
        Cfg.ConsentIncludedSaving.Value = allow && ModNetwork.HostSavesAudio;
        Cfg.ConsentIncludedTraining.Value = allow && ModNetwork.HostCollectsText;
        Cfg.ConsentIncludesReplay.Value = allow;
        ModNetwork.Instance?.SendConsent();
        Close();
    }

    private void OnDestroy()
    {
        if (root != null) Destroy(root);
    }

    private void SetBody()
    {
        shownSaving = ModNetwork.HostSavesAudio;
        shownTraining = ModNetwork.HostCollectsText;
        string storage = shownSaving
            ? "<color=#ffffff>THE HOST HAS AUDIO SAVING ON (A DEBUG SETTING).</color> Your voice clips are stored as files on " +
              "the host's computer until they delete them."
            : shownTraining
                ? "Audio is never stored: clips stay in the host's memory only while the game runs."
                : "Nothing is stored. Clips stay in the host's memory only while the game runs.";
        if (shownTraining)
        {
            storage += "\n\n<color=#ffffff>THE HOST COLLECTS TRAINING DATA.</color> What you say near the AI copies is saved " +
                       "as text (no audio) on the host's computer, to train a better AI for this mod. Nothing is uploaded " +
                       "unless the host shares it.";
        }
        // Says what happens to their voice and nothing about what the mod does in game, so the surprise stays.
        body!.text =
            "A mod in this lobby asks to use your voice.\n\n" +
            "If you allow it:\n" +
            "- The voice chat you already send also goes to the host's computer.\n" +
            "- There it's turned into text, and used to make an AI copy of your voice that can speak in this game. " +
            "Short things you said can also be played back as they were. Other players can hear both.\n" +
            "- It all runs on the host's computer. Nothing goes to an online service.";
        storageText!.text = storage;
        footer!.text =
            "If you don't, your voice isn't sent and nothing speaks with your voice.\n" +
            "<size=80%>Change it later in the config: Voice > VoiceCloningConsent.</size>";
    }

    // ---- building the UI

    private static TMP_FontAsset? GameFont()
    {
        var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
        return fonts.FirstOrDefault(f => f.name.Contains("3270") && !f.name.Contains("Outline"))
               ?? fonts.FirstOrDefault(f => f.name.Contains("3270"))
               ?? TMP_Settings.defaultFontAsset;
    }

    // Drawn on the game's own menu canvas when it can be found, so it goes through the same low-resolution UI camera
    // as the pause menu and gets the same chunky look, with the pause menu's font, buttons and panel. Anything missing
    // falls back to a look-alike on a canvas of its own.
    private void Build(GameNetcodeStuff.PlayerControllerB me, bool onGameCanvas)
    {
        var menu = me.quickMenuManager;
        var gameCanvas = onGameCanvas && menu != null && menu.menuContainer != null
            ? menu.menuContainer.transform.parent as RectTransform
            : null;
        root = new GameObject("PlayerMimicsConsent", typeof(RectTransform));
        var rootRect = (RectTransform)root.transform;
        float scale = 1f;
        if (gameCanvas != null)
        {
            root.transform.SetParent(gameCanvas, false);
            Place(rootRect, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            root.transform.SetAsLastSibling();
            if (gameCanvas.rect.height > 1f) scale = gameCanvas.rect.height / 1080f; // sizes below are for 1080 lines
        }
        else
        {
            DontDestroyOnLoad(root);
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 5000;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            root.AddComponent<GraphicRaycaster>();
        }

        // The pause menu's button label, for the font.
        var sampleButton = menu != null && menu.mainButtonsPanel != null
            ? menu.mainButtonsPanel.GetComponentsInChildren<Button>(true)
                .FirstOrDefault(b => b.GetComponentInChildren<TextMeshProUGUI>(true) != null)
            : null;
        var sampleLabel = sampleButton != null ? sampleButton.GetComponentInChildren<TextMeshProUGUI>(true) : null;
        var font = sampleLabel != null ? sampleLabel.font : GameFont();
        var material = sampleLabel != null ? sampleLabel.fontSharedMaterial : null;
        var textColor = sampleLabel != null ? sampleLabel.color : Orange;
        Plugin.Log.LogDebug($"Consent popup: {(gameCanvas != null ? "game menu canvas" : "own canvas")}, " +
                            $"font {(font != null ? font.name : "none")}");

        // The pause menu's see-through orange wash over the game, copied from it when it's there.
        var wash = menu != null && menu.BGPanelTransparency != null ? menu.BGPanelTransparency.GetComponent<Image>() : null;
        var shade = Box(root.transform, "Shade", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero,
            wash != null ? wash.color : menuTint);
        if (wash != null && wash.sprite != null)
        {
            shade.GetComponent<Image>().sprite = wash.sprite;
            shade.GetComponent<Image>().type = wash.type;
        }
        shade.GetComponent<Image>().raycastTarget = true; // nothing behind it gets clicked

        // The pause menu's dark edges: its own vignette image when there is one, else a drawn one.
        var vignette = Box(root.transform, "Vignette", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, Color.black);
        var vignetteImage = vignette.GetComponent<Image>();
        vignetteImage.raycastTarget = false;
        var menuVignette = menu != null && menu.menuContainer != null
            ? menu.menuContainer.GetComponentsInChildren<Image>(true)
                .FirstOrDefault(i => i.sprite != null && i.sprite.name.IndexOf("vignette", StringComparison.OrdinalIgnoreCase) >= 0)
            : null;
        if (menuVignette != null)
        {
            vignetteImage.sprite = menuVignette.sprite;
            vignetteImage.type = menuVignette.type;
            vignetteImage.color = menuVignette.color;
        }
        else
        {
            vignetteImage.sprite = VignetteSprite();
        }

        // Like the pause menu: no box, just the wash and thin corner brackets around the text.
        var center = new Vector2(0.5f, 0.5f);
        var panel = Box(root.transform, "Panel", center, center, new Vector2(-470f, -300f) * scale, new Vector2(470f, 300f) * scale,
            Color.clear);
        panel.GetComponent<Image>().raycastTarget = false;
        Brackets(panel, textColor, scale);

        float pad = 40f * scale;
        var title = Text(panel, "Title", font, material, 34f * scale, textColor, TextAlignmentOptions.TopLeft);
        Place(title.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(pad, -84f * scale), new Vector2(-pad, -28f * scale));
        title.text = "YOUR VOICE";
        title.characterSpacing = 8f;

        var rule = Box(panel, "Rule", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(pad, -92f * scale),
            new Vector2(-pad, -90f * scale), textColor);
        rule.GetComponent<Image>().raycastTarget = false;

        // The text as a column: what happens, then the storage note in a white box (it's what people want to know),
        // then what not allowing means.
        var column = new GameObject("Column", typeof(RectTransform), typeof(VerticalLayoutGroup));
        column.transform.SetParent(panel, false);
        Place((RectTransform)column.transform, Vector2.zero, Vector2.one, new Vector2(pad, 112f * scale), new Vector2(-pad, -112f * scale));
        var layout = column.GetComponent<VerticalLayoutGroup>();
        layout.spacing = 18f * scale;
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = true;

        float size = 20f * scale;
        body = Text(column.transform, "Body", font, material, size, textColor, TextAlignmentOptions.TopLeft);
        body.lineSpacing = 6f;

        var note = new GameObject("StorageNote", typeof(RectTransform), typeof(VerticalLayoutGroup));
        note.transform.SetParent(column.transform, false);
        var noteLayout = note.GetComponent<VerticalLayoutGroup>();
        int inset = Mathf.RoundToInt(12f * scale);
        noteLayout.padding = new RectOffset(inset, inset, inset, inset);
        noteLayout.childControlHeight = true;
        noteLayout.childControlWidth = true;
        noteLayout.childForceExpandHeight = false;
        Border((RectTransform)note.transform, Color.white, Mathf.Max(1f, 2f * scale));
        storageText = Text(note.transform, "Storage", font, material, size, Color.white, TextAlignmentOptions.TopLeft);

        footer = Text(column.transform, "Footer", font, material, size, textColor, TextAlignmentOptions.TopLeft);

        // Two buttons side by side along the bottom, inside the padding.
        float gap = 30f * scale;
        var buttonSize = new Vector2((940f * scale - 2f * pad - gap) / 2f, 56f * scale);
        MenuButton(panel, font, material, textColor, "> Allow", new Vector2(pad, 34f * scale), buttonSize, scale, () => Choose(true));
        MenuButton(panel, font, material, textColor, "> Don't allow", new Vector2(pad + buttonSize.x + gap, 34f * scale), buttonSize,
            scale, () => Choose(false));
        root.SetActive(false);
    }

    private static RectTransform Box(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin,
        Vector2 offsetMax, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        Place(rt, anchorMin, anchorMax, offsetMin, offsetMax);
        go.GetComponent<Image>().color = color;
        return rt;
    }

    private static Sprite? vignetteSprite;

    // Clear in the middle, darkening toward the edges and most in the corners.
    private static Sprite VignetteSprite()
    {
        if (vignetteSprite != null) return vignetteSprite;
        const int w = 128, h = 72;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var pixels = new Color32[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float dx = (x + 0.5f) / w * 2f - 1f, dy = (y + 0.5f) / h * 2f - 1f;
                float d = Mathf.Sqrt(dx * dx + dy * dy) / Mathf.Sqrt(2f);
                float a = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.55f, 1f, d)) * 0.85f;
                pixels[y * w + x] = new Color32(0, 0, 0, (byte)(a * 255f));
            }
        }
        tex.SetPixels32(pixels);
        tex.Apply();
        vignetteSprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f));
        return vignetteSprite;
    }

    // The pause menu's corners: a thin L in each corner, each arm followed by a short detached dash.
    private static void Brackets(RectTransform box, Color color, float scale)
    {
        float w = 4f * scale, arm = 36f * scale, gap = 10f * scale, dash = 13f * scale;
        foreach (var corner in new[] { Vector2.zero, Vector2.up, Vector2.right, Vector2.one })
        {
            var dir = new Vector2(corner.x > 0f ? -1f : 1f, corner.y > 0f ? -1f : 1f); // into the box
            Bar(box, corner, color, dir, new Vector2(arm, w));
            Bar(box, corner, color, dir, new Vector2(w, arm));
            Bar(box, corner, color, dir, new Vector2(dash, w), new Vector2(arm + gap, 0f));
            Bar(box, corner, color, dir, new Vector2(w, dash * 0.6f), new Vector2(0f, arm + gap));
        }
    }

    // A bar anchored at a corner, offset and sized along the directions pointing into the box.
    private static void Bar(RectTransform box, Vector2 corner, Color color, Vector2 dir, Vector2 size, Vector2 offset = default)
    {
        var a = new Vector2(offset.x * dir.x, offset.y * dir.y);
        var b = a + new Vector2(size.x * dir.x, size.y * dir.y);
        var bar = Box(box, "Bracket", corner, corner, Vector2.Min(a, b), Vector2.Max(a, b), color);
        bar.GetComponent<Image>().raycastTarget = false;
    }

    // A thin outline drawn as four edge bars; they don't take part in layout.
    private static void Border(RectTransform box, Color color, float width)
    {
        Edge(box, new Vector2(0f, 1f), Vector2.one, new Vector2(0f, -width), Vector2.zero, color);
        Edge(box, Vector2.zero, new Vector2(1f, 0f), Vector2.zero, new Vector2(0f, width), color);
        Edge(box, Vector2.zero, new Vector2(0f, 1f), Vector2.zero, new Vector2(width, 0f), color);
        Edge(box, new Vector2(1f, 0f), Vector2.one, new Vector2(-width, 0f), Vector2.zero, color);
    }

    private static void Edge(RectTransform box, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax, Color color)
    {
        var edge = Box(box, "Edge", anchorMin, anchorMax, offsetMin, offsetMax, color);
        edge.GetComponent<Image>().raycastTarget = false;
        edge.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
    }

    private static void Place(RectTransform rt, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
    {
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = offsetMin;
        rt.offsetMax = offsetMax;
    }

    private static TextMeshProUGUI Text(Transform parent, string name, TMP_FontAsset? font, Material? material, float size,
        Color color, TextAlignmentOptions align)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<TextMeshProUGUI>();
        if (font != null) t.font = font;
        if (material != null) t.fontSharedMaterial = material;
        t.fontSize = size;
        t.color = color;
        t.alignment = align;
        t.enableWordWrapping = true;
        t.richText = true;
        t.raycastTarget = false;
        return t;
    }

    // Like the pause menu's buttons: "> label" in the menu font, a solid orange bar with dark text on hover.
    private static void MenuButton(Transform parent, TMP_FontAsset? font, Material? material, Color color, string label,
        Vector2 bottomLeft, Vector2 size, float scale, UnityEngine.Events.UnityAction onClick)
    {
        var bg = Box(parent, label, Vector2.zero, Vector2.zero, bottomLeft, bottomLeft + size, Orange);
        var image = bg.GetComponent<Image>();
        var own = bg.gameObject.AddComponent<Button>();
        own.targetGraphic = image;
        var colors = own.colors;
        colors.normalColor = new Color(1f, 1f, 1f, 0f);
        colors.highlightedColor = Color.white;
        colors.selectedColor = new Color(1f, 1f, 1f, 0f);
        colors.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
        colors.fadeDuration = 0.02f;
        own.colors = colors;
        own.onClick.AddListener(onClick);

        var ownText = Text(bg, "Label", font, material, 26f * scale, color, TextAlignmentOptions.MidlineLeft);
        ownText.enableWordWrapping = false;
        Place(ownText.rectTransform, Vector2.zero, Vector2.one, new Vector2(14f * scale, 0f), new Vector2(-10f * scale, 0f));
        ownText.text = label;
        bg.gameObject.AddComponent<HoverLabel>().Label = ownText;
    }
}

// Swaps a menu button's text to dark while the orange hover bar is behind it.
internal sealed class HoverLabel : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    internal TextMeshProUGUI? Label;

    public void OnPointerEnter(PointerEventData e) => SetHover(true);

    public void OnPointerExit(PointerEventData e) => SetHover(false);

    private void OnDisable() => SetHover(false);

    private void SetHover(bool on)
    {
        if (Label != null) Label.color = on ? new Color(0.05f, 0.03f, 0.02f) : ConsentPopup.Orange;
    }
}
