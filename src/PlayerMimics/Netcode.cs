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
        HostStatusClientRpc(HostServices.Instance.AiOnline, Cfg.SaveAudioToDisk.Value);
    }

    [ClientRpc]
    private void HostStatusClientRpc(bool aiOnline, bool savesAudio)
    {
        HostStatusKnown = true;
        HostAiOnline = aiOnline;
        HostSavesAudio = savesAudio;
    }

    // ---- voice upload (client -> host). Mu-law, 16 kHz, cut into chunks.

    [ServerRpc(RequireOwnership = false)]
    internal void VoiceChunkServerRpc(int utterance, int index, int count, int sampleRate, byte[] data, ServerRpcParams rpc = default)
    {
        HostServices.Instance?.ReceiveVoiceChunk(rpc.Receive.SenderClientId, utterance, index, count, sampleRate, data);
    }

    // ---- mimic setup and state (host -> everyone)

    internal void SetupMimic(MaskedPlayerEnemy masked, PlayerControllerB player, bool revived)
    {
        SetupMimicClientRpc(new NetworkObjectReference(masked.NetworkObject), (int)player.playerClientId, revived);
    }

    [ClientRpc]
    private void SetupMimicClientRpc(NetworkObjectReference masked, int playerId, bool revived)
    {
        if (!masked.TryGet(out NetworkObject obj)) return;
        var enemy = obj.GetComponent<MaskedPlayerEnemy>();
        if (enemy == null || playerId < 0 || playerId >= StartOfRound.Instance.allPlayerScripts.Length) return;
        var player = StartOfRound.Instance.allPlayerScripts[playerId];
        var disguise = enemy.GetComponent<MimicDisguise>() ?? enemy.gameObject.AddComponent<MimicDisguise>();
        disguise.Apply(player, revived);
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

    [ClientRpc]
    internal void VoiceLineClientRpc(NetworkObjectReference masked, int line, int index, int count, int sampleRate, byte[] data)
    {
        if (!masked.TryGet(out NetworkObject obj)) return;
        var voice = obj.GetComponent<MimicVoice>() ?? obj.gameObject.AddComponent<MimicVoice>();
        voice.ReceiveChunk(line, index, count, sampleRate, data);
    }

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
