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
    private static readonly Color panelColor = new(0.035f, 0.025f, 0.02f, 0.97f);

    private bool showing;
    private bool lookWasDisabled;
    private CursorLockMode oldLock;
    private bool oldVisible;

    private GameObject? root;
    private TextMeshProUGUI? body;
    private bool shownSaving;

    private bool NeedsAnswer()
    {
        if (Cfg.VoiceConsent.Value == ConsentChoice.Unset) return true;
        // Allowed before, but this host keeps audio on disk: ask again so they know.
        return Cfg.VoiceConsent.Value == ConsentChoice.Allow && ModNetwork.HostSavesAudio && !Cfg.ConsentIncludedSaving.Value;
    }

    private void Update()
    {
        var key = Cfg.ResetConsentKey.Value;
        if (key != Key.None && Keyboard.current != null && Keyboard.current[key].wasPressedThisFrame)
        {
            Cfg.VoiceConsent.Value = ConsentChoice.Unset;
            Cfg.ConsentIncludedSaving.Value = false;
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
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            // The host's saving setting can change while it's open.
            if (body != null && shownSaving != ModNetwork.HostSavesAudio) SetBody();
        }
    }

    private void Open(GameNetcodeStuff.PlayerControllerB me)
    {
        showing = true;
        oldLock = Cursor.lockState;
        oldVisible = Cursor.visible;
        lookWasDisabled = me.disableLookInput;
        me.disableLookInput = true;
        if (root == null) Build();
        SetBody();
        root!.SetActive(true);
    }

    private void Close()
    {
        showing = false;
        if (root != null) root.SetActive(false);
        var me = GameNetworkManager.Instance != null ? GameNetworkManager.Instance.localPlayerController : null;
        if (me != null) me.disableLookInput = lookWasDisabled;
        bool menuOpen = me != null && me.quickMenuManager != null && me.quickMenuManager.isMenuOpen;
        Cursor.lockState = menuOpen ? CursorLockMode.None : oldLock;
        Cursor.visible = menuOpen || oldVisible;
    }

    private void Choose(bool allow)
    {
        Cfg.VoiceConsent.Value = allow ? ConsentChoice.Allow : ConsentChoice.Deny;
        Cfg.ConsentIncludedSaving.Value = allow && ModNetwork.HostSavesAudio;
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
        string storage = shownSaving
            ? "<color=#ffffff>THE HOST HAS AUDIO SAVING ON (A DEBUG SETTING).</color> Your voice clips are stored as files on " +
              "the host's computer until they delete them."
            : "Nothing is stored. Clips stay in the host's memory only while the AI server runs.";
        // Says what happens to their voice and nothing about what the mod does in game, so the surprise stays.
        body!.text =
            "A mod in this lobby asks to use your voice.\n\n" +
            "If you allow it:\n" +
            "- The voice chat you already send also goes to the host's computer.\n" +
            "- There it's turned into text, and used to make an AI copy of your voice that can speak in this game. " +
            "Other players can hear it.\n" +
            "- It all runs on the host's computer. Nothing goes to an online service.\n\n" +
            storage + "\n\n" +
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

    private void Build()
    {
        root = new GameObject("PlayerMimicsConsent");
        DontDestroyOnLoad(root);
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 5000;
        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        root.AddComponent<GraphicRaycaster>();

        var font = GameFont();
        Plugin.Log.LogDebug($"Consent popup font: {(font != null ? font.name : "none")}");
        Box(root.transform, "Shade", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, new Color(0f, 0f, 0f, 0.72f));

        // Orange frame with the dark panel inset into it.
        var frame = Box(root.transform, "Frame", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-470f, -330f),
            new Vector2(470f, 330f), Orange);
        var panel = Box(frame, "Panel", Vector2.zero, Vector2.one, new Vector2(3f, 3f), new Vector2(-3f, -3f), panelColor);

        var title = Text(panel, "Title", font, 40f, Orange, TextAlignmentOptions.TopLeft);
        Place(title.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(36f, -88f), new Vector2(-36f, -26f));
        title.text = "YOUR VOICE";
        title.fontStyle = FontStyles.Bold;

        var rule = Box(panel, "Rule", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(36f, -94f), new Vector2(-36f, -91f), Orange);
        rule.GetComponent<Image>().raycastTarget = false;

        body = Text(panel, "Body", font, 23f, dimColor, TextAlignmentOptions.TopLeft);
        Place(body.rectTransform, Vector2.zero, Vector2.one, new Vector2(36f, 110f), new Vector2(-36f, -112f));
        body.enableAutoSizing = true;
        body.fontSizeMin = 14f;
        body.fontSizeMax = 23f;
        body.lineSpacing = 8f;

        MenuButton(panel, font, "> Allow", new Vector2(36f, 28f), () => Choose(true));
        MenuButton(panel, font, "> Don't allow", new Vector2(490f, 28f), () => Choose(false));
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

    private static void Place(RectTransform rt, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
    {
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = offsetMin;
        rt.offsetMax = offsetMax;
    }

    private static TextMeshProUGUI Text(Transform parent, string name, TMP_FontAsset? font, float size, Color color,
        TextAlignmentOptions align)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<TextMeshProUGUI>();
        if (font != null) t.font = font;
        t.fontSize = size;
        t.color = color;
        t.alignment = align;
        t.enableWordWrapping = true;
        t.richText = true;
        t.raycastTarget = false;
        return t;
    }

    // Like the game's menu buttons: orange text, a solid orange bar with dark text on hover.
    private static void MenuButton(Transform parent, TMP_FontAsset? font, string label, Vector2 bottomLeft,
        UnityEngine.Events.UnityAction onClick)
    {
        var bg = Box(parent, label, Vector2.zero, Vector2.zero, bottomLeft, bottomLeft + new Vector2(410f, 58f), Orange);
        var image = bg.GetComponent<Image>();
        var button = bg.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        var colors = button.colors;
        colors.normalColor = new Color(1f, 1f, 1f, 0.1f);
        colors.highlightedColor = Color.white;
        colors.selectedColor = new Color(1f, 1f, 1f, 0.1f);
        colors.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
        colors.fadeDuration = 0.05f;
        button.colors = colors;
        button.onClick.AddListener(onClick);

        var text = Text(bg, "Label", font, 26f, Orange, TextAlignmentOptions.MidlineLeft);
        Place(text.rectTransform, Vector2.zero, Vector2.one, new Vector2(16f, 0f), new Vector2(-10f, 0f));
        text.text = label;
        bg.gameObject.AddComponent<HoverLabel>().Label = text;
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
