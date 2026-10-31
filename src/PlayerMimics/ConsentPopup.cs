using UnityEngine;
using UnityEngine.InputSystem;

namespace PlayerMimics;

// Asks each player once whether their voice may be cloned. The answer is saved in their own config file.
internal sealed class ConsentPopup : MonoBehaviour
{
    private bool showing;
    private bool lookWasDisabled;
    private CursorLockMode oldLock;
    private bool oldVisible;
    private Rect window;
    private GUIStyle? text;

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
        }
    }

    private void Open(GameNetcodeStuff.PlayerControllerB me)
    {
        showing = true;
        oldLock = Cursor.lockState;
        oldVisible = Cursor.visible;
        lookWasDisabled = me.disableLookInput;
        me.disableLookInput = true;
    }

    private void Close()
    {
        showing = false;
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

    private void OnGUI()
    {
        if (!showing) return;
        float w = Mathf.Min(620f, Screen.width - 40f);
        float h = Mathf.Min(430f, Screen.height - 40f);
        window = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = Color.white;
        GUI.ModalWindow(0x504D43, window, Draw, "PlayerMimics: voice cloning");
    }

    private void Draw(int id)
    {
        text ??= new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 15, richText = true };
        string storage = ModNetwork.HostSavesAudio
            ? "<b><color=#ffb347>The host has audio saving turned on (a debug setting).</color></b> Your voice clips are " +
              "stored as files on the host's computer until they delete them."
            : "Nothing is stored. Clips are kept in the host's memory only while the AI server runs, and are gone when it stops.";
        string body =
            "This lobby uses <b>PlayerMimics</b>: Masked enemies disguise themselves as players and talk with a copy of their voice.\n\n" +
            "If you allow it:\n" +
            "• The voice chat you already send is also sent to the host's computer.\n" +
            "• There it's turned into text, so Masked can follow the conversation, and used to clone your voice.\n" +
            "• Masked copying you then speak new lines in your voice. Other players hear them.\n" +
            "• Everything runs on the host's own computer; nothing is sent to an online service.\n\n" +
            storage + "\n\n" +
            "If you don't allow it, your voice never leaves your computer and Masked copying you stay silent.\n\n" +
            "You can change this later in the PlayerMimics config (Voice > VoiceCloningConsent).";
        GUILayout.BeginVertical();
        GUILayout.Label(body, text);
        GUILayout.FlexibleSpace();
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Allow voice cloning", GUILayout.Height(36))) Choose(true);
        if (GUILayout.Button("Don't allow", GUILayout.Height(36))) Choose(false);
        GUILayout.EndHorizontal();
        GUILayout.EndVertical();
    }
}
