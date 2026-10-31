using System;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PlayerMimics;

// Solo-testing helpers: spawn key, brain overlay and Masked markers.
internal sealed class DebugTools : MonoBehaviour
{
    private GUIStyle? style;

    private void Update()
    {
        var key = Cfg.SpawnMaskedKey.Value;
        if (key == Key.None || Keyboard.current == null || !Keyboard.current[key].wasPressedThisFrame) return;
        if (!HostServices.IsHost || HostServices.Instance == null || RoundManager.Instance == null) return;
        var me = GameNetworkManager.Instance.localPlayerController;
        if (me == null || me.isPlayerDead || StartOfRound.Instance.inShipPhase)
        {
            Plugin.Log.LogInfo("Spawn key: land on a moon first");
            return;
        }
        string name = Cfg.SpawnCopies.Value.Trim();
        var copy = name.Length == 0 ? me :
            StartOfRound.Instance.allPlayerScripts.FirstOrDefault(p => p.isPlayerControlled &&
                string.Equals(p.playerUsername, name, StringComparison.OrdinalIgnoreCase)) ?? me;
        Vector3 at = me.transform.position + me.transform.forward * 4f;
        HostServices.Instance.SpawnMasked(at, me.transform.eulerAngles.y + 180f, copy, revived: false);
        Plugin.Log.LogInfo($"Spawned a Masked copying {copy.playerUsername}");
    }

    private void OnGUI()
    {
        bool overlay = Cfg.ShowBrainOverlay.Value && HostServices.IsHost;
        bool reveal = Cfg.RevealMasked.Value;
        if (!overlay && !reveal) return;
        var cam = StartOfRound.Instance != null ? StartOfRound.Instance.activeCamera : null;
        if (cam == null) return;
        style ??= new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.LowerCenter, richText = true };
        foreach (var d in MimicDisguise.All)
        {
            if (d == null || d.Masked == null || d.Masked.isEnemyDead) continue;
            Vector3 world = d.transform.position + Vector3.up * 2.6f;
            Vector3 screen = cam.WorldToScreenPoint(world);
            if (screen.z <= 0f) continue;
            // The game camera renders to a smaller texture; scale to the screen.
            float sx = Screen.width / (float)cam.pixelWidth;
            float sy = Screen.height / (float)cam.pixelHeight;
            var pos = new Vector2(screen.x * sx, Screen.height - screen.y * sy);
            if (reveal)
            {
                GUI.color = Color.red;
                GUI.Label(new Rect(pos.x - 60, pos.y - 20, 120, 20), $"<b>MASKED ({d.Player.playerUsername})</b>", style);
            }
            if (overlay && d.DebugText.Length > 0)
            {
                GUI.color = Color.yellow;
                GUI.Label(new Rect(pos.x - 200, pos.y - 110, 400, 90), d.DebugText, style);
            }
            GUI.color = Color.white;
        }
    }
}
