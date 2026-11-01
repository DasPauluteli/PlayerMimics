using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;

namespace PlayerMimics;

[HarmonyPatch(typeof(MaskedPlayerEnemy))]
internal static class MaskedPatches
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(MaskedPlayerEnemy.Start))]
    private static void Start(MaskedPlayerEnemy __instance)
    {
        if (__instance.IsServer) HostServices.Instance?.OnMaskedStart(__instance);
    }

    // Our brain replaces the vanilla AI for disguised Masked. Kill animations and death stay vanilla.
    [HarmonyPrefix]
    [HarmonyPatch(nameof(MaskedPlayerEnemy.DoAIInterval))]
    private static bool DoAIInterval(MaskedPlayerEnemy __instance)
    {
        var brain = __instance.GetComponent<MimicBrain>();
        if (brain == null || __instance.isEnemyDead) return true;
        if (__instance.inSpecialAnimation) return false;
        brain.Tick();
        // What EnemyAI.DoAIInterval does: walk to the destination and sync the position.
        if (__instance.moveTowardsDestination && __instance.agent.enabled && __instance.agent.isOnNavMesh &&
            __instance.destination != __instance.prevDestination && __instance.agent.SetDestination(__instance.destination))
        {
            __instance.prevDestination = __instance.destination;
        }
        __instance.SyncPositionToClients();
        return false;
    }

    // Vanilla sets the Masked's speed every frame; ours walk and sprint like players.
    [HarmonyPostfix]
    [HarmonyPatch(nameof(MaskedPlayerEnemy.Update))]
    private static void Update(MaskedPlayerEnemy __instance)
    {
        if (__instance.IsOwner) __instance.GetComponent<MimicBrain>()?.ApplySpeed();
    }

    // Vanilla hides a Masked while its player's body lies around. Ours copy living players and hide bodies themselves.
    [HarmonyPrefix]
    [HarmonyPatch(nameof(MaskedPlayerEnemy.SetVisibilityOfMaskedEnemy))]
    private static bool SetVisibility(MaskedPlayerEnemy __instance)
    {
        if (MimicDisguise.Of(__instance) == null) return true;
        if (!__instance.enemyEnabled)
        {
            __instance.enemyEnabled = true;
            __instance.EnableEnemyMesh(enable: true);
        }
        return false;
    }

    // Disguised Masked look around like players (see MimicDisguise.LookAtFocused).
    [HarmonyPrefix]
    [HarmonyPatch(nameof(MaskedPlayerEnemy.LookAtFocusedPosition))]
    private static bool LookAtFocusedPosition(MaskedPlayerEnemy __instance)
    {
        var disguise = MimicDisguise.Of(__instance);
        if (disguise == null) return true;
        disguise.LookAtFocused();
        return false;
    }

    // A disguised Masked only kills when it means to.
    [HarmonyPrefix]
    [HarmonyPatch(nameof(MaskedPlayerEnemy.OnCollideWithPlayer))]
    private static bool OnCollideWithPlayer(MaskedPlayerEnemy __instance)
    {
        var disguise = MimicDisguise.Of(__instance);
        return disguise == null || disguise.State.Hostile;
    }

    // Players never walk around with their arms out, not even while attacking.
    [HarmonyPrefix]
    [HarmonyPatch(nameof(MaskedPlayerEnemy.SetHandsOutClientRpc))]
    private static void NoHandsOut(MaskedPlayerEnemy __instance, ref bool setOut)
    {
        if (MimicDisguise.Of(__instance) != null) setOut = false;
    }

    // Vanilla turns to every noise and logs each one. Disguised Masked glance at loud ones (MimicBrain.OnNoise).
    [HarmonyPrefix]
    [HarmonyPatch(nameof(MaskedPlayerEnemy.DetectNoise))]
    private static bool DetectNoise(MaskedPlayerEnemy __instance, Vector3 noisePosition, float noiseLoudness, int noiseID)
    {
        if (MimicDisguise.Of(__instance) == null) return true;
        if (__instance.IsOwner && !__instance.isEnemyDead && !__instance.inSpecialAnimation)
        {
            __instance.GetComponent<MimicBrain>()?.OnNoise(noisePosition, noiseLoudness, noiseID);
        }
        return false;
    }

    // No glowing eyes and no mask sound when it grabs someone.
    [HarmonyPrefix]
    [HarmonyPatch(nameof(MaskedPlayerEnemy.SetMaskGlow))]
    private static bool SetMaskGlow(MaskedPlayerEnemy __instance) => MimicDisguise.Of(__instance) == null;

    // Tell Coroner who the killer was pretending to be. Runs before Coroner's own "killed by a Masked" fallback.
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(nameof(MaskedPlayerEnemy.FinishKillAnimation))]
    private static void FinishKillAnimationPrefix(MaskedPlayerEnemy __instance, bool killedPlayer, out PlayerControllerB? __state)
    {
        __state = null;
        var victim = __instance.inSpecialAnimationWithPlayer;
        var disguise = MimicDisguise.Of(__instance);
        if (!killedPlayer || victim == null || disguise == null) return;
        __state = victim;
        if (victim.isPlayerDead) CoronerCompat.OnKill(disguise, victim);
    }

    // After a kill the brain checks who saw it; with no witnesses its cover holds.
    [HarmonyPostfix]
    [HarmonyPatch(nameof(MaskedPlayerEnemy.FinishKillAnimation))]
    private static void FinishKillAnimationPostfix(MaskedPlayerEnemy __instance, PlayerControllerB? __state)
    {
        if (__state != null && __instance.IsServer) __instance.GetComponent<MimicBrain>()?.OnKilled(__state);
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(MaskedPlayerEnemy.HitEnemy))]
    private static void HitEnemy(MaskedPlayerEnemy __instance, PlayerControllerB playerWhoHit)
    {
        if (MimicDisguise.Of(__instance) != null && !__instance.isEnemyDead)
        {
            __instance.creatureAnimator.SetTrigger("Damage"); // the player flinch
        }
        if (__instance.IsServer) __instance.GetComponent<MimicBrain>()?.OnHit(playerWhoHit);
    }
}

[HarmonyPatch(typeof(EnemyAI))]
internal static class EnemyPatches
{
    // Start caches the renderers, then MoreCompany swaps the cosmetics on a disguised Masked and the cached ones are
    // destroyed. Collect them again (the same way Start does) instead of throwing on the next show or hide.
    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyAI.EnableEnemyMesh))]
    private static void EnableEnemyMesh(EnemyAI __instance)
    {
        if (__instance is not MaskedPlayerEnemy || MimicDisguise.Of(__instance) == null) return;
        if (__instance.skinnedMeshRenderers.All(r => r != null) && __instance.meshRenderers.All(r => r != null)) return;
        __instance.skinnedMeshRenderers = __instance.GetComponentsInChildren<SkinnedMeshRenderer>().Where(Own).ToArray();
        __instance.meshRenderers = __instance.GetComponentsInChildren<MeshRenderer>().Where(Own).ToArray();
    }

    private static bool Own(Renderer r) =>
        r.GetComponentInParent<PlayerControllerB>() == null && r.GetComponentInParent<GrabbableObject>() == null;
}

[HarmonyPatch(typeof(PlayerControllerB))]
internal static class PlayerPatches
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(PlayerControllerB.SpawnDeadBody))]
    private static void SpawnDeadBody(PlayerControllerB deadPlayerController)
    {
        if (HostServices.IsHost && deadPlayerController != null)
        {
            HostServices.Instance?.OnPlayerDied(deadPlayerController);
        }
    }
}

// The Masked prefab carries the player's animation event receiver with no player behind it. With player animations
// on, every footstep threw a NullReferenceException there (thousands per round). MimicAnimationEvents plays the sounds.
[HarmonyPatch]
internal static class PlayerAnimationEventsPatch
{
    private static IEnumerable<MethodBase> TargetMethods() =>
        new[] { "PlayFootstepServer", "PlayFootstepLocal", "LimpForward", "LockArmsToCamera", "UnlockArmsFromCamera" }
            .Select(n => AccessTools.Method(typeof(PlayerAnimationEvents), n))
            .Where(m => m != null);

    private static bool Prefix(PlayerAnimationEvents __instance) => __instance.thisPlayerController != null;
}

[HarmonyPatch(typeof(StartOfRound))]
internal static class RoundPatches
{
    // Masked should react to the ship leaving like a player would.
    [HarmonyPostfix]
    [HarmonyPatch(nameof(StartOfRound.ShipLeave))]
    private static void ShipLeave()
    {
        foreach (var brain in MimicBrain.All)
        {
            brain.AddEvent("The ship is leaving!", decideNow: true);
        }
    }
}
