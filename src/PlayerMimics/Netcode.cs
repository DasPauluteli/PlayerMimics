using System;
using System.Reflection;
using GameNetcodeStuff;
using MonoMod.RuntimeDetour;
using Unity.Netcode;
using UnityEngine;

namespace PlayerMimics;

// One networked object per session. The host spawns it, and it carries all the mod's RPCs.
internal sealed class ModNetwork : NetworkBehaviour
{
    public static ModNetwork? Instance { get; private set; }

    // What the host told us. Clients use it for the consent popup and to decide whether to capture voice.
    internal static bool HostStatusKnown;
    internal static bool HostAiOnline;
    internal static bool HostSavesAudio;
    internal static float HostNameTagRange = 5f; // host's setting, so nobody can see Masked name tags from further away

    public override void OnNetworkSpawn()
    {
        Instance = this;
        HostStatusKnown = false;
        base.OnNetworkSpawn();
        if (IsServer)
        {
            HostServices.Instance?.OnSessionStart();
        }
        SendConsent();
    }

    public override void OnNetworkDespawn()
    {
        Instance = null;
        HostStatusKnown = false;
        base.OnNetworkDespawn();
    }

    // ---- consent and host status

    internal void SendConsent() =>
        ConsentServerRpc(Cfg.VoiceConsent.Value == ConsentChoice.Allow);

    [ServerRpc(RequireOwnership = false)]
    private void ConsentServerRpc(bool allowed, ServerRpcParams rpc = default)
    {
        HostServices.Instance?.SetConsent(rpc.Receive.SenderClientId, allowed);
        BroadcastHostStatus();
    }

    internal void BroadcastHostStatus()
    {
        if (!IsServer || HostServices.Instance == null) return;
        HostStatusClientRpc(HostServices.Instance.AiOnline, Cfg.SaveAudioToDisk.Value, Cfg.NameTagRange.Value);
    }

    [ClientRpc]
    private void HostStatusClientRpc(bool aiOnline, bool savesAudio, float nameTagRange)
    {
        HostStatusKnown = true;
        HostAiOnline = aiOnline;
        HostSavesAudio = savesAudio;
        HostNameTagRange = Mathf.Clamp(nameTagRange, 0f, 20f);
    }

    // ---- voice upload (client -> host). Mu-law, 16 kHz, cut into chunks.

    [ServerRpc(RequireOwnership = false)]
    internal void VoiceChunkServerRpc(int utterance, int index, int count, int sampleRate, byte[] data, ServerRpcParams rpc = default)
    {
        HostServices.Instance?.ReceiveVoiceChunk(rpc.Receive.SenderClientId, utterance, index, count, sampleRate, data);
    }

    // ---- mimic setup and state (host -> everyone)

    // name: what it goes by, empty for the copied player's name (Debug > MimicName, so everyone needs it).
    internal void SetupMimic(MaskedPlayerEnemy masked, PlayerControllerB player, bool revived, string name)
    {
        SetupMimicClientRpc(new NetworkObjectReference(masked.NetworkObject), (int)player.playerClientId, revived, name);
    }

    [ClientRpc]
    private void SetupMimicClientRpc(NetworkObjectReference masked, int playerId, bool revived, string name)
    {
        if (!masked.TryGet(out NetworkObject obj)) return;
        var enemy = obj.GetComponent<MaskedPlayerEnemy>();
        if (enemy == null || playerId < 0 || playerId >= StartOfRound.Instance.allPlayerScripts.Length) return;
        var player = StartOfRound.Instance.allPlayerScripts[playerId];
        var disguise = enemy.GetComponent<MimicDisguise>() ?? enemy.gameObject.AddComponent<MimicDisguise>();
        disguise.Apply(player, revived, name);
        if (IsServer && enemy.GetComponent<MimicBrain>() == null)
        {
            enemy.gameObject.AddComponent<MimicBrain>().Init(enemy, disguise);
        }
    }

    internal void SyncState(MaskedPlayerEnemy masked, MimicVisualState s)
    {
        SyncStateClientRpc(new NetworkObjectReference(masked.NetworkObject), s.Hostile, s.Sprinting, s.Crouching, s.Emote, s.SignText ?? "");
    }

    [ClientRpc]
    private void SyncStateClientRpc(NetworkObjectReference masked, bool hostile, bool sprinting, bool crouching, int emote, string signText)
    {
        if (!masked.TryGet(out NetworkObject obj)) return;
        var disguise = obj.GetComponent<MimicDisguise>();
        if (disguise == null) return;
        disguise.SetState(new MimicVisualState
        {
            Hostile = hostile,
            Sprinting = sprinting,
            Crouching = crouching,
            Emote = emote,
            SignText = signText,
        });
    }

    // A Masked walked into a spider web (the struggle sound) or tore through it. Vanilla only does this for players,
    // and its version makes the spider chase the player who did it.
    [ClientRpc]
    internal void WebClientRpc(NetworkObjectReference spider, int trapID, bool broke)
    {
        if (!spider.TryGet(out NetworkObject obj)) return;
        var s = obj.GetComponent<SandSpiderAI>();
        if (s == null || trapID < 0 || trapID >= s.webTraps.Count || s.webTraps[trapID] == null) return;
        var web = s.webTraps[trapID];
        if (broke)
        {
            AudioSource.PlayClipAtPoint(s.breakWebSFX, web.centerOfWeb.position);
            s.RemoveWeb(trapID);
        }
        else
        {
            web.webAudio.PlayOneShot(s.hitWebSFX);
            WalkieTalkie.TransmitOneShotAudio(web.webAudio, s.hitWebSFX);
        }
    }

    [ClientRpc]
    internal void HideDeadBodyClientRpc(int playerId)
    {
        if (playerId < 0 || playerId >= StartOfRound.Instance.allPlayerScripts.Length) return;
        var player = StartOfRound.Instance.allPlayerScripts[playerId];
        if (player.deadBody != null)
        {
            player.deadBody.DeactivateBody(setActive: false);
        }
    }

    // ---- mimic voice lines (host -> everyone)

    // A line arrives in pieces (mu-law, in order). 'start' says when to begin playing: on the first pieces when the
    // server makes audio faster than real time, otherwise only with the last piece. 'final' ends the line.
    [ClientRpc]
    internal void VoiceStreamClientRpc(NetworkObjectReference masked, int line, int seq, int sampleRate, byte[] data,
        bool start, bool final)
    {
        if (!masked.TryGet(out NetworkObject obj)) return;
        var voice = obj.GetComponent<MimicVoice>() ?? obj.gameObject.AddComponent<MimicVoice>();
        voice.ReceiveStream(line, seq, sampleRate, data, start, final);
    }

    // ---- mimic items and doors (host -> everyone). The host checks everything first; clients just do the step.

    internal void MimicGrab(MaskedPlayerEnemy masked, GrabbableObject item, int slot) =>
        MimicGrabClientRpc(new NetworkObjectReference(masked.NetworkObject), new NetworkObjectReference(item.NetworkObject), slot);

    [ClientRpc]
    private void MimicGrabClientRpc(NetworkObjectReference masked, NetworkObjectReference item, int slot)
    {
        var inventory = InventoryOf(masked);
        if (inventory == null || !item.TryGet(out NetworkObject obj)) return;
        var grabbable = obj.GetComponent<GrabbableObject>();
        if (grabbable != null) inventory.Grab(grabbable, slot);
    }

    internal void MimicSwitch(MaskedPlayerEnemy masked, int slot) =>
        MimicSwitchClientRpc(new NetworkObjectReference(masked.NetworkObject), slot);

    [ClientRpc]
    private void MimicSwitchClientRpc(NetworkObjectReference masked, int slot) => InventoryOf(masked)?.Switch(slot);

    internal void MimicDrop(MaskedPlayerEnemy masked, GrabbableObject item, Vector3 target, bool inElevator, bool inShipRoom, int floorYRot) =>
        MimicDropClientRpc(new NetworkObjectReference(masked.NetworkObject), new NetworkObjectReference(item.NetworkObject), target,
            inElevator, inShipRoom, floorYRot);

    [ClientRpc]
    private void MimicDropClientRpc(NetworkObjectReference masked, NetworkObjectReference item, Vector3 target, bool inElevator,
        bool inShipRoom, int floorYRot)
    {
        var inventory = InventoryOf(masked);
        if (inventory == null || !item.TryGet(out NetworkObject obj)) return;
        var grabbable = obj.GetComponent<GrabbableObject>();
        if (grabbable != null) inventory.Drop(grabbable, target, inElevator, inShipRoom, floorYRot);
    }

    // The door is named by its id and side; every client has the same entrances.
    internal void MimicEntrance(MaskedPlayerEnemy masked, EntranceTeleport door, Vector3 pos, float yaw) =>
        MimicEntranceClientRpc(new NetworkObjectReference(masked.NetworkObject), door.entranceId, door.isEntranceToBuilding, pos, yaw);

    [ClientRpc]
    private void MimicEntranceClientRpc(NetworkObjectReference masked, int entranceId, bool fromOutside, Vector3 pos, float yaw)
    {
        if (!masked.TryGet(out NetworkObject obj)) return;
        var disguise = obj.GetComponent<MimicDisguise>();
        if (disguise == null) return;
        EntranceTeleport? door = null;
        foreach (var e in UnityEngine.Object.FindObjectsOfType<EntranceTeleport>())
        {
            if (e.entranceId == entranceId && e.isEntranceToBuilding == fromOutside) door = e;
        }
        disguise.UsedEntrance(door, pos, yaw, outside: !fromOutside);
    }

    private static MimicInventory? InventoryOf(NetworkObjectReference masked) =>
        masked.TryGet(out NetworkObject obj) ? obj.GetComponent<MimicInventory>() : null;

    // ---- debug

    [ClientRpc]
    internal void DebugTextClientRpc(NetworkObjectReference masked, string text)
    {
        if (!masked.TryGet(out NetworkObject obj)) return;
        var disguise = obj.GetComponent<MimicDisguise>();
        if (disguise != null) disguise.DebugText = text;
    }
}

internal struct MimicVisualState
{
    public bool Hostile;
    public bool Sprinting;
    public bool Crouching;
    public int Emote;
    public string? SignText;

    public readonly bool SameAs(MimicVisualState o) =>
        Hostile == o.Hostile && Sprinting == o.Sprinting && Crouching == o.Crouching && Emote == o.Emote &&
        (SignText ?? "") == (o.SignText ?? "");
}

internal static class ModNetworkSetup
{
    // Picked when the mod was created, so every player registers the same network prefab id.
    private const uint PrefabId = 2041191113;
    private static GameObject? prefab;
    private static Hook? registerHook;
    private static Hook? spawnHook;

    // NetcodePatcher adds [RuntimeInitializeOnLoadMethod] methods (NetworkVariable serialization setup), but Unity only
    // runs those for its own assemblies, so the plugin runs them once itself.
    internal static void Install()
    {
        foreach (Type type in LoadableTypes())
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Static | BindingFlags.NonPublic))
            {
                if (method.GetCustomAttributes(typeof(RuntimeInitializeOnLoadMethodAttribute), false).Length > 0)
                {
                    method.Invoke(null, null);
                }
            }
        }
        // Both are private in the game. The publicized reference only makes them look public.
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        registerHook = new Hook(typeof(GameNetworkManager).GetMethod(nameof(GameNetworkManager.Start), hidden), Register);
        spawnHook = new Hook(typeof(StartOfRound).GetMethod(nameof(StartOfRound.Awake), hidden), Spawn);
    }

    // A type that references an optional mod that isn't installed can't load. Skip it; it has none of this mod's RPCs.
    private static Type[] LoadableTypes()
    {
        try
        {
            return Assembly.GetExecutingAssembly().GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return Array.FindAll(e.Types, t => t != null)!;
        }
    }

    // Register the prefab before the game starts hosting or joining.
    private static void Register(Action<GameNetworkManager> orig, GameNetworkManager self)
    {
        orig(self);
        prefab = new GameObject(nameof(ModNetwork)) { hideFlags = HideFlags.HideAndDontSave };
        NetworkObject networkObject = prefab.AddComponent<NetworkObject>();
        networkObject.GlobalObjectIdHash = PrefabId;
        prefab.AddComponent<ModNetwork>();
        NetworkManager.Singleton.AddNetworkPrefab(prefab);
    }

    private static void Spawn(Action<StartOfRound> orig, StartOfRound self)
    {
        orig(self);
        if (prefab != null && NetworkManager.Singleton.IsServer)
        {
            UnityEngine.Object.Instantiate(prefab).GetComponent<NetworkObject>().Spawn();
        }
    }
}
