#if (audience != "everyone")
#error Netcode spawns an object every player must know, so a netcode mod is for everyone in the lobby (audience "everyone").
#endif
using System;
using System.Reflection;
using MonoMod.RuntimeDetour;
using Unity.Netcode;
using UnityEngine;

namespace ModTemplate;

// One networked object per session. The host spawns it, and it carries the mod's RPCs.
internal sealed class ModNetwork : NetworkBehaviour
{
    public static ModNetwork? Instance { get; private set; }

    public override void OnNetworkSpawn()
    {
        Instance = this;
        base.OnNetworkSpawn();
    }

    public override void OnNetworkDespawn()
    {
        Instance = null;
        base.OnNetworkDespawn();
    }

    [ClientRpc]
    public void AnnounceClientRpc(string text) => Plugin.Log.LogInfo($"Host says: {text}");
}

internal static class ModNetworkSetup
{
    // Picked when the mod was created, so every player registers the same network prefab id.
    private const uint PrefabId = 2142302400;
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
