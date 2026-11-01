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
    private readonly Dictionary<(int seen, int by), float> lastSeen = new(); // when a player last saw another
    private float nextSightCheck;
    private int nextLineId;
    private readonly Dictionary<ulong, (int player, bool revived, PlayerControllerB[] witnesses)> pendingCopies = new(); // Masked network id -> who it copies
    private readonly Dictionary<ulong, PlayerControllerB[]> reviveWitnesses = new(); // who saw a revived Masked's player die
    private EnemyType? maskedType;
    private bool? wasInShipPhase;

    internal static bool IsHost => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;

    private void Awake() => Instance = this;

    internal void OnSessionStart()
    {
        consent.Clear();
        uploads.Clear();
        lastSeen.Clear();
        pendingCopies.Clear();
        reviveWitnesses.Clear();
        AiOnline = false;
        nextHealthCheck = 0f;
        wasInShipPhase = null;
        HabitRecorder.Clear();
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
        HabitRecorder.Sample();
        bool inShip = StartOfRound.Instance.inShipPhase;
        if (wasInShipPhase != inShip)
        {
            bool first = wasInShipPhase == null;
            wasInShipPhase = inShip;
            if (!first || !inShip) OnRoundChanged(started: !inShip);
        }
        foreach (var key in uploads.Where(u => Time.realtimeSinceStartup - u.Value.parts.Started > 30f).Select(u => u.Key).ToList())
        {
            uploads.Remove(key);
        }
    }

    // Landing starts a round, reaching orbit ends it. The server then forgets what Masked said last round and only
    // keeps chat from around this one, so nothing from an old round leaks into new lines.
    private void OnRoundChanged(bool started)
    {
        MonsterLure.Reset();
        lastSeen.Clear();
        var level = StartOfRound.Instance.currentLevel;
        Plugin.Log.LogInfo(started ? $"Round started on {(level != null ? level.PlanetName : "?")}" : "Round ended");
        if (!AiOnline) return;
        Post("/round", new JObject { ["state"] = started ? "started" : "ended", ["moon"] = level != null ? level.PlanetName : "" }, 10, _ => { });
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
            // Everyone nearby hears it, but only one mimic answers: the one it was said to by name, else the closest.
            var brains = MimicBrain.All.Where(b => b != null).ToList();
            var replier = brains.Select(b => (brain: b, score: b.ReplyScore(player, text))).Where(x => x.score >= 0f)
                .OrderBy(x => x.score).Select(x => x.brain).FirstOrDefault();
            foreach (var brain in brains) brain.OnSpeech(player, text, brain == replier);
        });
    }

    private static string MakeSafe(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_'));

    // ---- voice out

    // started: a line /decide already began voicing (it wrote "say" first), so only the playback is left.
    internal void Speak(MimicBrain brain, string text, string started = "")
    {
        if (Cfg.StreamLines.Value)
        {
            StartCoroutine(StreamLine(brain, text, started));
            return;
        }
        var copied = brain.Copied;
        Post("/speak_line", LineRequest(brain, text), 60, result =>
        {
            if (result == null || !Alive(brain)) return;
            if (!(result.Value<bool?>("ready") ?? false))
            {
                Plugin.Log.LogDebug($"No cloned voice for {copied.playerUsername} yet");
                return;
            }
            var samples = ReadPcm(result);
            if (samples.Length == 0) return;
            MimicVoice.Fade(samples, AudioCodec.NetRate / 100);
            int seq = 0;
            SendPiece(brain, nextLineId++, ref seq, samples, start: true, final: true);
        });
    }

    // Plays a line while the server is still making it. Everyone gets the audio as it comes; playback starts once the
    // server says enough is ready that the rest will arrive before it's needed (StreamLeadSeconds plus whatever slow
    // generation needs on top), so it never stutters. Old servers without that answer wait for the whole line.
    private IEnumerator StreamLine(MimicBrain brain, string text, string startedStream = "")
    {
        var copied = brain.Copied;
        JObject? reply = null;
        bool answered = false;
        string stream = startedStream;
        if (stream.Length == 0)
        {
            Post("/speak_stream", LineRequest(brain, text), 15, r => { reply = r; answered = true; });
            yield return new WaitUntil(() => answered);
            stream = reply?.Value<string>("stream") ?? "";
            if (!(reply?.Value<bool?>("ready") ?? false) || stream.Length == 0)
            {
                Plugin.Log.LogDebug($"No cloned voice for {copied.playerUsername} yet");
                yield break;
            }
        }

        int line = nextLineId++, seq = 0, offset = 0, failures = 0;
        bool started = false;
        float since = Time.realtimeSinceStartup;
        while (true)
        {
            if (!Alive(brain)) yield break;
            reply = null;
            answered = false;
            var poll = new JObject { ["stream"] = stream, ["offset"] = offset, ["lead"] = Cfg.StreamLeadSeconds.Value };
            Post("/speak_poll", poll, 10, r => { reply = r; answered = true; });
            yield return new WaitUntil(() => answered);
            if (!Alive(brain)) yield break;
            var r = reply;
            bool timedOut = Time.realtimeSinceStartup - since > 30f;
            if (r == null)
            {
                if (++failures < 5 && !timedOut)
                {
                    yield return new WaitForSecondsRealtime(0.2f);
                    continue;
                }
                // Ends the line: a playing one finishes what it has, one that never started is dropped.
                SendPiece(brain, line, ref seq, Array.Empty<float>(), start: false, final: true);
                yield break;
            }
            failures = 0;
            bool done = r.Value<bool?>("done") ?? true;
            if (timedOut && !done)
            {
                Plugin.Log.LogWarning($"Line for {copied.playerUsername} took too long; dropped");
                SendPiece(brain, line, ref seq, Array.Empty<float>(), start: false, final: true);
                yield break;
            }
            var samples = ReadPcm(r);
            offset = r.Value<int?>("offset") ?? offset;
            float speed = r.Value<float?>("speed") ?? 0f;
            float ready = offset / (float)AudioCodec.NetRate;
            bool startNow = !started && (done || (r.Value<bool?>("start_ok") ?? false));
            if (samples.Length > 0 || startNow || done)
            {
                SendPiece(brain, line, ref seq, samples, startNow, done);
            }
            if (startNow)
            {
                started = true;
                Plugin.Log.LogDebug($"Line for {copied.playerUsername}: playing after {Time.realtimeSinceStartup - since:0.00}s " +
                                    $"({(done ? "complete" : $"streaming at {speed:0.00}x with {ready:0.0}s ready")})");
            }
            if (done)
            {
                string error = r.Value<string>("error") ?? "";
                if (error.Length > 0) Plugin.Log.LogWarning($"Line for {copied.playerUsername} failed: {error}");
                yield break;
            }
            yield return new WaitForSecondsRealtime(0.1f);
        }
    }

    // What /decide needs to start voicing its line right away (only for streamed lines and with consent).
    internal JObject? VoiceRequest(MimicBrain brain)
    {
        if (!Cfg.StreamLines.Value || !HasConsent(brain.Copied)) return null;
        var r = LineRequest(brain, "");
        r.Remove("text");
        return r;
    }

    // Drops a line /decide started that the mod didn't say.
    internal void CancelLine(string stream) => Post("/speak_cancel", new JObject { ["stream"] = stream }, 5, _ => { });

    // The voice is always the copied player's; "as_name" is the name the Masked goes by (Debug > MimicName).
    private static JObject LineRequest(MimicBrain brain, string text) => new()
    {
        ["key"] = PlayerKey(brain.Copied),
        ["name"] = brain.Copied.playerUsername,
        ["as_name"] = brain.Renamed ? brain.Name : "",
        ["text"] = text,
        ["sample_rate"] = AudioCodec.NetRate,
    };

    private static bool Alive(MimicBrain brain) =>
        brain != null && brain.Masked != null && !brain.Masked.isEnemyDead && ModNetwork.Instance != null;

    private static float[] ReadPcm(JObject r)
    {
        string pcm = r.Value<string>("pcm16") ?? "";
        if (pcm.Length == 0) return Array.Empty<float>();
        int rate = r.Value<int?>("sample_rate") ?? AudioCodec.NetRate;
        return AudioCodec.Resample(AudioCodec.FromPcm16Base64(pcm), rate, AudioCodec.NetRate);
    }

    // Sends samples to everyone in RPC-sized pieces; the start/final flags ride on the last piece.
    private static void SendPiece(MimicBrain brain, int line, ref int seq, float[] samples, bool start, bool final)
    {
        var target = new NetworkObjectReference(brain.Masked.NetworkObject);
        var chunks = AudioCodec.Chunk(AudioCodec.MuLawEncode(samples));
        for (int i = 0; i < chunks.Length; i++)
        {
            bool last = i == chunks.Length - 1;
            ModNetwork.Instance!.VoiceStreamClientRpc(target, line, seq++, AudioCodec.NetRate, chunks[i], start && last, final && last);
        }
    }

    // ---- Masked setup

    // Masked being set up (host): their vanilla AI waits meanwhile, or it may spot a player, switch to its chase state
    // and play the Masked laugh for everyone before the disguise is on.
    private readonly Dictionary<MaskedPlayerEnemy, float> settingUp = new();

    // Masked we left vanilla on purpose (nobody to copy): only these may make their Masked noises.
    private readonly HashSet<MaskedPlayerEnemy> leftVanilla = new();

    internal bool LeftVanilla(MaskedPlayerEnemy masked) => leftVanilla.Contains(masked);

    internal bool SettingUp(MaskedPlayerEnemy masked) =>
        settingUp.TryGetValue(masked, out float since) && Time.time - since < 5f;

    internal void OnMaskedStart(MaskedPlayerEnemy masked)
    {
        settingUp[masked] = Time.time;
        StartCoroutine(SetUp(masked));
    }

    private IEnumerator SetUp(MaskedPlayerEnemy masked)
    {
        yield return StartCoroutine(SetupWhenReady(masked));
        settingUp.Remove(masked);
    }

    private IEnumerator SetupWhenReady(MaskedPlayerEnemy masked)
    {
        // Give the vanilla kill flow and our revive time to say who it copies.
        yield return new WaitForSeconds(0.5f);
        if (masked == null || masked.isEnemyDead || ModNetwork.Instance == null || masked.GetComponent<MimicDisguise>() != null) yield break;
        PlayerControllerB? player = null;
        bool revived = false;
        bool natural = false; // the game spawned it, not a revive or the spawn key
        if (pendingCopies.TryGetValue(masked.NetworkObjectId, out var pending))
        {
            pendingCopies.Remove(masked.NetworkObjectId);
            player = StartOfRound.Instance.allPlayerScripts[pending.player];
            revived = pending.revived;
            if (pending.witnesses.Length > 0) reviveWitnesses[masked.NetworkObjectId] = pending.witnesses;
        }
        else if (masked.mimickingPlayer != null)
        {
            player = masked.mimickingPlayer;
            revived = player.isPlayerDead; // killed by a Masked and came back
        }
        else
        {
            player = ChooseCopy(masked);
            natural = true;
        }
        if (player == null)
        {
            Plugin.Log.LogInfo("No player to copy; leaving this Masked vanilla");
            leftVanilla.RemoveWhere(m => m == null);
            leftVanilla.Add(masked);
            yield break;
        }
        ModNetwork.Instance.SetupMimic(masked, player, revived, Cfg.MimicName.Value.Trim());
        if (natural && Cfg.SpawnWithScrap.Value) StartCoroutine(GiveStartingScrap(masked));
    }

    // A piece of this moon's scrap (one-handed and not too heavy, picked by the moon's own rarity), spawned like the
    // level's scrap and put straight into the new Masked's hand.
    private IEnumerator GiveStartingScrap(MaskedPlayerEnemy masked)
    {
        var level = StartOfRound.Instance.currentLevel;
        var round = RoundManager.Instance;
        if (level == null || round == null || level.spawnableScrap == null) yield break;
        var options = level.spawnableScrap.Where(s => s.rarity > 0 && s.spawnableItem != null && s.spawnableItem.spawnPrefab != null &&
                                                      s.spawnableItem.isScrap && !s.spawnableItem.twoHanded && s.spawnableItem.weight <= 1.25f)
            .ToList();
        if (options.Count == 0) yield break;
        int roll = UnityEngine.Random.Range(0, options.Sum(s => s.rarity));
        var pick = options.First(s => (roll -= s.rarity) < 0).spawnableItem;

        Transform parent = round.spawnedScrapContainer != null ? round.spawnedScrapContainer : StartOfRound.Instance.propsContainer;
        var obj = Instantiate(pick.spawnPrefab, masked.transform.position + Vector3.up, Quaternion.identity, parent);
        var item = obj.GetComponent<GrabbableObject>();
        var net = obj.GetComponent<NetworkObject>();
        if (item == null || net == null)
        {
            Destroy(obj);
            yield break;
        }
        int value = Mathf.Max(1, (int)(UnityEngine.Random.Range(pick.minValue, pick.maxValue) * round.scrapValueMultiplier));
        item.scrapValue = value;
        item.isInFactory = !masked.isOutside;
        net.Spawn();
        yield return new WaitForSeconds(0.3f); // the item reaches everyone before the grab that names it
        if (item == null || masked == null || masked.isEnemyDead) yield break;
        round.SyncScrapValuesClientRpc(new[] { new NetworkObjectReference(net) }, new[] { value });
        var brain = masked.GetComponent<MimicBrain>();
        if (brain == null) yield break;
        brain.GiveStartingItem(item);
        Plugin.Log.LogInfo($"{brain.Copied.playerUsername}'s Masked starts out holding a {pick.itemName} (${value})");
    }

    // Who saw the player a revived Masked copies die (Revive > OnlyWhenUnobserved off). Asked once, by its brain.
    internal PlayerControllerB[] TakeWitnesses(MaskedPlayerEnemy masked)
    {
        if (!reviveWitnesses.TryGetValue(masked.NetworkObjectId, out var witnesses)) return Array.Empty<PlayerControllerB>();
        reviveWitnesses.Remove(masked.NetworkObjectId);
        return witnesses;
    }

    // Prefer players who consented (they get a voice), then anyone alive.
    private PlayerControllerB? ChooseCopy(MaskedPlayerEnemy masked)
    {
        var alive = StartOfRound.Instance.allPlayerScripts.Where(p => p.isPlayerControlled && !p.isPlayerDead).ToList();
        if (alive.Count == 0) return null;
        // Two copies of one player are a giveaway: only copy someone twice when everyone is copied already.
        int Copies(PlayerControllerB p) => MimicBrain.All.Count(b => b != null && b.Masked != null && !b.Masked.isEnemyDead && b.Copied == p);
        int fewest = alive.Min(Copies);
        alive = alive.Where(p => Copies(p) == fewest).ToList();
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

    internal MaskedPlayerEnemy? SpawnMasked(Vector3 position, float yRot, PlayerControllerB? copy, bool revived,
        PlayerControllerB[]? witnesses = null)
    {
        var type = MaskedType;
        if (type == null)
        {
            Plugin.Log.LogWarning("Couldn't find the Masked enemy type");
            return null;
        }
        // Only on walkable ground: a Masked spawned with no navmesh nearby (in orbit, say) breaks and spams errors.
        Vector3 at = RoundManager.Instance.GetNavMeshPosition(position, default, 5f);
        if (!RoundManager.Instance.GotNavMeshPositionResult) at = RoundManager.Instance.GetNavMeshPosition(position, default, 12f);
        if (!RoundManager.Instance.GotNavMeshPositionResult)
        {
            Plugin.Log.LogWarning("No walkable ground near there; no Masked spawned");
            return null;
        }
        var reference = RoundManager.Instance.SpawnEnemyGameObject(at, yRot, -1, type);
        if (!reference.TryGet(out NetworkObject obj)) return null;
        var masked = obj.GetComponent<MaskedPlayerEnemy>();
        if (copy != null) pendingCopies[obj.NetworkObjectId] = ((int)copy.playerClientId, revived, witnesses ?? Array.Empty<PlayerControllerB>());
        return masked;
    }

    // ---- revive deaths

    private void TrackSightings()
    {
        var players = StartOfRound.Instance.allPlayerScripts.Where(p => p.isPlayerControlled && !p.isPlayerDead).ToList();
        float range = Cfg.WatchedRange.Value;
        foreach (var p in players)
        {
            Vector3 point = p.transform.position + Vector3.up * 1.2f;
            foreach (var q in players)
            {
                if (q != p && q.HasLineOfSightToPosition(point, 70f, (int)range))
                {
                    lastSeen[((int)p.playerClientId, (int)q.playerClientId)] = Time.time;
                }
            }
        }
    }

    internal void OnPlayerDied(PlayerControllerB player)
    {
        foreach (var brain in MimicBrain.All.ToList()) brain.OnPlayerDied(player);
        if (!Cfg.ReviveEnabled.Value || player.deadBody == null) return;
        if (StartOfRound.Instance.inShipPhase || StartOfRound.Instance.shipIsLeaving || player.isInHangarShipRoom) return;
        if (StartOfRound.Instance.currentLevel != null && StartOfRound.Instance.currentLevel.levelID == 3) return; // the Company
        var witnesses = Witnesses(player);
        string seenBy = witnesses.Length > 0 ? $"while {string.Join(", ", witnesses.Select(w => w.playerUsername))} watched" : "with nobody watching";
        if (witnesses.Length > 0 && Cfg.ReviveOnlyUnobserved.Value)
        {
            Plugin.Log.LogInfo($"{player.playerUsername} died {seenBy}; no revive");
            return;
        }
        if (UnityEngine.Random.Range(0, 100) >= Cfg.ReviveChancePercent.Value)
        {
            Plugin.Log.LogInfo($"{player.playerUsername} died {seenBy}; no revive this time ({Cfg.ReviveChancePercent.Value}% chance)");
            return;
        }
        Plugin.Log.LogInfo($"{player.playerUsername} died {seenBy}; reviving as a Masked in {Cfg.ReviveDelay.Value:0}s");
        StartCoroutine(Revive(player, witnesses));
    }

    // Living players who had the dead player in sight during the last two seconds.
    private PlayerControllerB[] Witnesses(PlayerControllerB dead) =>
        StartOfRound.Instance.allPlayerScripts.Where(q => q != dead && q.isPlayerControlled && !q.isPlayerDead &&
            lastSeen.TryGetValue(((int)dead.playerClientId, (int)q.playerClientId), out float seen) && Time.time - seen < 2f).ToArray();

    private IEnumerator Revive(PlayerControllerB player, PlayerControllerB[] witnesses)
    {
        yield return new WaitForSeconds(Cfg.ReviveDelay.Value);
        var body = player.deadBody;
        if (body == null || body.deactivated || body.isInShip || StartOfRound.Instance.shipIsLeaving || StartOfRound.Instance.inShipPhase) yield break;
        if (body.grabBodyObject != null && body.grabBodyObject.isHeld) yield break; // someone's carrying it
        if (!player.isPlayerDead) yield break;
        Vector3 at = body.transform.position;
        var masked = SpawnMasked(at, UnityEngine.Random.Range(0f, 360f), player, revived: true, witnesses);
        if (masked == null) yield break;
        ModNetwork.Instance?.HideDeadBodyClientRpc((int)player.playerClientId);
        player.redirectToEnemy = masked;
    }
}
