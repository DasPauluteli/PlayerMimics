using GameNetcodeStuff;
using HarmonyLib;

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
        if (__instance.GetComponent<MimicDisguise>() == null) return true;
        if (!__instance.enemyEnabled)
        {
            __instance.enemyEnabled = true;
            __instance.EnableEnemyMesh(enable: true);
        }
        return false;
    }

    // A disguised Masked only kills when it means to.
    [HarmonyPrefix]
    [HarmonyPatch(nameof(MaskedPlayerEnemy.OnCollideWithPlayer))]
    private static bool OnCollideWithPlayer(MaskedPlayerEnemy __instance)
    {
        var disguise = __instance.GetComponent<MimicDisguise>();
        return disguise == null || disguise.State.Hostile;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(MaskedPlayerEnemy.SetHandsOutClientRpc))]
    private static void NoHandsOut(MaskedPlayerEnemy __instance, ref bool setOut)
    {
        var disguise = __instance.GetComponent<MimicDisguise>();
        if (disguise != null && !disguise.Revealed) setOut = false;
    }

    // Tell Coroner who the killer was pretending to be. Runs before Coroner's own "killed by a Masked" fallback.
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(nameof(MaskedPlayerEnemy.FinishKillAnimation))]
    private static void FinishKillAnimation(MaskedPlayerEnemy __instance, bool killedPlayer)
    {
        var victim = __instance.inSpecialAnimationWithPlayer;
        var disguise = __instance.GetComponent<MimicDisguise>();
        if (killedPlayer && victim != null && victim.isPlayerDead && disguise != null)
        {
            CoronerCompat.OnKill(disguise, victim);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(MaskedPlayerEnemy.HitEnemy))]
    private static void HitEnemy(MaskedPlayerEnemy __instance, PlayerControllerB playerWhoHit)
    {
        var disguise = __instance.GetComponent<MimicDisguise>();
        if (disguise != null && !disguise.Revealed && !__instance.isEnemyDead)
        {
            __instance.creatureAnimator.SetTrigger("Damage"); // the player flinch
        }
        if (__instance.IsServer) __instance.GetComponent<MimicBrain>()?.OnHit(playerWhoHit);
    }
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
