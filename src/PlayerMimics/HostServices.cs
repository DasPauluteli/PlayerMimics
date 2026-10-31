using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GameNetcodeStuff;
using Newtonsoft.Json.Linq;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Networking;

namespace PlayerMimics;

// Host-side services: the AI server connection, consent bookkeeping, voice from clients, cloned lines,
// Masked setup, and reviving unwatched deaths.
internal sealed class HostServices : MonoBehaviour
{
    internal static HostServices? Instance { get; private set; }

    internal bool AiOnline { get; private set; }
    private float nextHealthCheck;
    private readonly Dictionary<ulong, bool> consent = new();
    private readonly Dictionary<(ulong client, int utterance), (ChunkAssembly parts, int rate)> uploads = new();
    private readonly Dictionary<int, float> lastSeen = new();
    private float nextSightCheck;
    private int nextLineId;
    private readonly Dictionary<ulong, (int player, bool revived)> pendingCopies = new(); // Masked network id -> who it copies
    private EnemyType? maskedType;

    internal static bool IsHost => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;

    private void Awake() => Instance = this;

    internal void OnSessionStart()
    {
        consent.Clear();
        uploads.Clear();
        lastSeen.Clear();
        pendingCopies.Clear();
        AiOnline = false;
        nextHealthCheck = 0f;
    }

    // ---- players and consent

    internal static string PlayerKey(PlayerControllerB p) =>
        p.playerSteamId != 0 ? p.playerSteamId.ToString() : $"{p.playerUsername}#{p.playerClientId}";

    internal static PlayerControllerB? PlayerByClient(ulong clientId) =>
        StartOfRound.Instance == null ? null :
        StartOfRound.Instance.allPlayerScripts.FirstOrDefault(p => p.isPlayerControlled && p.actualClientId == clientId);

    internal void SetConsent(ulong clientId, bool allowed)
    {
        bool had = consent.TryGetValue(clientId, out bool before) && before;
        consent[clientId] = allowed;
        var player = PlayerByClient(clientId);
        Plugin.Log.LogInfo($"Voice cloning consent from {player?.playerUsername ?? "client " + clientId}: {(allowed ? "allowed" : "denied")}");
        if (had && !allowed && player != null)
        {
            Post("/forget", new JObject { ["key"] = PlayerKey(player) }, 10, _ => { });
        }
    }

    internal bool HasConsent(PlayerControllerB p) => consent.TryGetValue(p.actualClientId, out bool ok) && ok;

    // ---- per frame

    private void Update()
    {
        if (!IsHost || StartOfRound.Instance == null) return;
        if (Time.realtimeSinceStartup >= nextHealthCheck)
        {
            nextHealthCheck = Time.realtimeSinceStartup + 10f;
            StartCoroutine(CheckHealth());
        }
        if (Time.time >= nextSightCheck)
        {
            nextSightCheck = Time.time + 0.25f;
            TrackSightings();
        }
        foreach (var key in uploads.Where(u => Time.realtimeSinceStartup - u.Value.parts.Started > 30f).Select(u => u.Key).ToList())
        {
            uploads.Remove(key);
        }
    }

    private IEnumerator CheckHealth()
    {
        using var req = UnityWebRequest.Get(Cfg.ServerUrl.Value.TrimEnd('/') + "/health");
        req.timeout = 3;
        yield return req.SendWebRequest();
        bool online = req.result == UnityWebRequest.Result.Success;
        if (online != AiOnline)
        {
            AiOnline = online;
            Plugin.Log.LogInfo(online ? "AI server is online" : $"AI server is offline ({Cfg.ServerUrl.Value}); Masked use scripted behaviour");
            ModNetwork.Instance?.BroadcastHostStatus();
        }
        else if (ModNetwork.Instance != null && !ModNetwork.HostStatusKnown)
        {
            ModNetwork.Instance.BroadcastHostStatus();
        }
    }

    internal void Post(string path, JObject body, int timeoutSeconds, Action<JObject?> done) =>
        StartCoroutine(PostRoutine(path, body.ToString(Newtonsoft.Json.Formatting.None), timeoutSeconds, done));

    private static IEnumerator PostRoutine(string path, string json, int timeoutSeconds, Action<JObject?> done)
    {
        using var req = new UnityWebRequest(Cfg.ServerUrl.Value.TrimEnd('/') + path, "POST");
        req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = timeoutSeconds;
        yield return req.SendWebRequest();
        JObject? result = null;
        if (req.result == UnityWebRequest.Result.Success)
        {
            try
            {
                result = JObject.Parse(req.downloadHandler.text);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{path}: bad answer: {e.Message}");
            }
        }
        else
        {
            Plugin.Log.LogDebug($"{path} failed: {req.error} {req.downloadHandler?.text}");
        }
        try
        {
            done(result);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"{path} handler failed: {e}");
        }
    }

    // ---- voice in

    internal void ReceiveVoiceChunk(ulong sender, int utterance, int index, int count, int rate, byte[] data)
    {
        if (count <= 0 || count > 200 || rate < 8000 || rate > 48000) return;
        var player = PlayerByClient(sender);
        if (player == null || !HasConsent(player) || !AiOnline) return; // never accept voice without consent
        var key = (sender, utterance);
        if (!uploads.TryGetValue(key, out var upload))
        {
            upload = (new ChunkAssembly(count, Time.realtimeSinceStartup), rate);
            uploads[key] = upload;
        }
        if (upload.parts.Count != count) return;
        if (upload.parts.Add(index, data))
        {
            uploads.Remove(key);
            Ingest(player, AudioCodec.MuLawDecode(upload.parts.Join()), rate);
        }
    }

    internal void Ingest(PlayerControllerB player, float[] samples, int rate)
    {
        if (Cfg.SaveAudioToDisk.Value)
        {
            string file = Path.Combine(Plugin.AudioDumpDir, MakeSafe(player.playerUsername), $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.wav");
            try
            {
                AudioCodec.WriteWav(file, samples, rate);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't save {file}: {e.Message}");
            }
        }
        var body = new JObject
        {
            ["key"] = PlayerKey(player),
            ["name"] = player.playerUsername,
            ["sample_rate"] = rate,
            ["pcm16"] = AudioCodec.ToPcm16Base64(samples),
            ["source"] = "voice",
        };
        Post("/ingest", body, 30, result =>
        {
            string text = result?.Value<string>("text") ?? "";
            if (text.Length == 0 || player == null || player.isPlayerDead) return;
            foreach (var brain in MimicBrain.All.ToList()) brain.OnSpeech(player, text);
        });
    }

    private static string MakeSafe(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_'));

    // ---- voice out

    internal void Speak(MimicBrain brain, string text)
    {
        var copied = brain.Copied;
        var body = new JObject
        {
            ["key"] = PlayerKey(copied),
            ["name"] = copied.playerUsername,
            ["text"] = text,
            ["sample_rate"] = AudioCodec.NetRate,
        };
        Post("/speak_line", body, 60, result =>
        {
            if (result == null || brain == null || brain.Masked == null || brain.Masked.isEnemyDead) return;
            if (!(result.Value<bool?>("ready") ?? false))
            {
                Plugin.Log.LogDebug($"No cloned voice for {copied.playerUsername} yet");
                return;
            }
            string pcm = result.Value<string>("pcm16") ?? "";
            int rate = result.Value<int?>("sample_rate") ?? AudioCodec.NetRate;
            if (pcm.Length == 0 || ModNetwork.Instance == null) return;
            var samples = AudioCodec.Resample(AudioCodec.FromPcm16Base64(pcm), rate, AudioCodec.NetRate);
            var chunks = AudioCodec.Chunk(AudioCodec.MuLawEncode(samples));
            int line = nextLineId++;
            var target = new NetworkObjectReference(brain.Masked.NetworkObject);
            for (int i = 0; i < chunks.Length; i++)
            {
                ModNetwork.Instance.VoiceLineClientRpc(target, line, i, chunks.Length, AudioCodec.NetRate, chunks[i]);
            }
        });
    }

    // ---- Masked setup

    internal void OnMaskedStart(MaskedPlayerEnemy masked) => StartCoroutine(SetupWhenReady(masked));

    private IEnumerator SetupWhenReady(MaskedPlayerEnemy masked)
    {
        // Give the vanilla kill flow and our revive time to say who it copies.
        yield return new WaitForSeconds(0.5f);
        if (masked == null || masked.isEnemyDead || ModNetwork.Instance == null || masked.GetComponent<MimicDisguise>() != null) yield break;
        PlayerControllerB? player = null;
        bool revived = false;
        if (pendingCopies.TryGetValue(masked.NetworkObjectId, out var pending))
        {
            pendingCopies.Remove(masked.NetworkObjectId);
            player = StartOfRound.Instance.allPlayerScripts[pending.player];
            revived = pending.revived;
        }
        else if (masked.mimickingPlayer != null)
        {
            player = masked.mimickingPlayer;
            revived = player.isPlayerDead; // killed by a Masked and came back
        }
        else
        {
            player = ChooseCopy(masked);
        }
        if (player == null)
        {
            Plugin.Log.LogInfo("No player to copy; leaving this Masked vanilla");
            yield break;
        }
        ModNetwork.Instance.SetupMimic(masked, player, revived);
    }

    // Prefer players who consented (they get a voice), then anyone alive.
    private PlayerControllerB? ChooseCopy(MaskedPlayerEnemy masked)
    {
        var alive = StartOfRound.Instance.allPlayerScripts.Where(p => p.isPlayerControlled && !p.isPlayerDead).ToList();
        if (alive.Count == 0) return null;
        var voiced = alive.Where(HasConsent).ToList();
        var pool = voiced.Count > 0 && UnityEngine.Random.value < 0.75f ? voiced : alive;
        // Copying someone standing right there is an instant giveaway; prefer someone far away.
        return pool.OrderByDescending(p => Vector3.Distance(p.transform.position, masked.transform.position) + UnityEngine.Random.Range(0f, 30f)).First();
    }

    internal EnemyType? MaskedType
    {
        get
        {
            if (maskedType != null) return maskedType;
            foreach (var level in StartOfRound.Instance.levels)
            {
                foreach (var list in new[] { level.Enemies, level.OutsideEnemies, level.DaytimeEnemies })
                {
                    var found = list?.FirstOrDefault(e => e.enemyType != null && e.enemyType.enemyName == "Masked");
                    if (found != null) return maskedType = found.enemyType;
                }
            }
            return null;
        }
    }

    internal MaskedPlayerEnemy? SpawnMasked(Vector3 position, float yRot, PlayerControllerB? copy, bool revived)
    {
        var type = MaskedType;
        if (type == null)
        {
            Plugin.Log.LogWarning("Couldn't find the Masked enemy type");
            return null;
        }
        var reference = RoundManager.Instance.SpawnEnemyGameObject(RoundManager.Instance.GetNavMeshPosition(position, default, 5f), yRot, -1, type);
        if (!reference.TryGet(out NetworkObject obj)) return null;
        var masked = obj.GetComponent<MaskedPlayerEnemy>();
        if (copy != null) pendingCopies[obj.NetworkObjectId] = ((int)copy.playerClientId, revived);
        return masked;
    }

    // ---- revive unwatched deaths

    private void TrackSightings()
    {
        var players = StartOfRound.Instance.allPlayerScripts.Where(p => p.isPlayerControlled && !p.isPlayerDead).ToList();
        float range = Cfg.WatchedRange.Value;
        foreach (var p in players)
        {
            Vector3 point = p.transform.position + Vector3.up * 1.2f;
            foreach (var q in players)
            {
                if (q == p) continue;
                if (q.HasLineOfSightToPosition(point, 70f, (int)range))
                {
                    lastSeen[(int)p.playerClientId] = Time.time;
                    break;
                }
            }
        }
    }

    internal void OnPlayerDied(PlayerControllerB player)
    {
        foreach (var brain in MimicBrain.All.ToList()) brain.OnPlayerDied(player);
        if (!Cfg.ReviveUnwatched.Value || player.deadBody == null) return;
        if (StartOfRound.Instance.inShipPhase || StartOfRound.Instance.shipIsLeaving || player.isInHangarShipRoom) return;
        if (StartOfRound.Instance.currentLevel != null && StartOfRound.Instance.currentLevel.levelID == 3) return; // the Company
        bool watched = lastSeen.TryGetValue((int)player.playerClientId, out float seen) && Time.time - seen < 2f;
        if (watched)
        {
            Plugin.Log.LogInfo($"{player.playerUsername} died while someone was watching; no revive");
            return;
        }
        if (UnityEngine.Random.value > Cfg.ReviveChance.Value) return;
        Plugin.Log.LogInfo($"{player.playerUsername} died with nobody watching; reviving as a Masked in {Cfg.ReviveDelay.Value:0}s");
        StartCoroutine(Revive(player));
    }

    private IEnumerator Revive(PlayerControllerB player)
    {
        yield return new WaitForSeconds(Cfg.ReviveDelay.Value);
        var body = player.deadBody;
        if (body == null || body.deactivated || body.isInShip || StartOfRound.Instance.shipIsLeaving || StartOfRound.Instance.inShipPhase) yield break;
        if (body.grabBodyObject != null && body.grabBodyObject.isHeld) yield break; // someone's carrying it
        if (!player.isPlayerDead) yield break;
        Vector3 at = body.transform.position;
        var masked = SpawnMasked(at, UnityEngine.Random.Range(0f, 360f), player, revived: true);
        if (masked == null) yield break;
        ModNetwork.Instance?.HideDeadBodyClientRpc((int)player.playerClientId);
        player.redirectToEnemy = masked;
    }
}
