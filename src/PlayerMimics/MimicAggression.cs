using System.Linq;
using GameNetcodeStuff;
using UnityEngine;

namespace PlayerMimics;

// Host only. The predator side. Someone alone with it, with nobody else around to see, is a chance it takes even when
// they don't suspect a thing (Aggression). And an attack on someone who doesn't see it coming starts as a friend
// walking over: it closes in at walking speed, from behind when it can, and only lunges when it's close, when they
// turn to run, or after a while. Sprinting at someone from across the room was the giveaway.
internal sealed partial class MimicBrain
{
    private PlayerControllerB? aloneWith;
    private float aloneSince = -1f;
    private float nextAmbushRoll;
    private bool lunging;
    private float stalkUntil;

    // The one player who's alone with it: close, on its side, nobody else within 35 m of either of them and no one
    // else who can see it from further off.
    private PlayerControllerB? AloneWith()
    {
        var alive = AlivePlayers().ToList();
        var near = alive.Where(p => IsAudience(p) && SameSide(p) && Dist(p) < 20f).ToList();
        if (near.Count != 1) return null;
        var lone = near[0];
        // Everyone else is a possible witness, the real copied player too.
        foreach (var o in alive)
        {
            if (o == lone) continue;
            if (Dist(o) < 35f || Vector3.Distance(o.transform.position, lone.transform.position) < 35f) return null;
            if (o.HasLineOfSightToPosition(Head, 70f, 60)) return null;
        }
        return lone;
    }

    private bool AloneLongEnough =>
        aloneWith != null && Time.time - aloneSince >= 6f && SecondsWithGroup >= Cfg.AloneAttackSeconds.Value;

    // AI tick: keeps track of who's alone with it and, now and then, goes for them.
    private void Ambush()
    {
        var lone = AloneWith();
        if (lone != aloneWith)
        {
            aloneWith = lone;
            aloneSince = lone != null ? Time.time : -1f;
        }
        if (lone == null || !AloneLongEnough || Cfg.NoAttack.Value || Cfg.Aggression.Value <= 0f) return;
        if (Current is Behaviour.Attack or Behaviour.Flee || Fleeing || IsTask(Current) || Masked.inSpecialAnimation) return;
        if (Time.time < nextAmbushRoll) return;
        nextAmbushRoll = Time.time + 1f;
        // Aggression is the chance per 10 seconds alone; twice that when their back is turned, half when they're
        // watching it.
        float perSecond = 1f - Mathf.Pow(1f - Mathf.Clamp01(Cfg.Aggression.Value), 0.1f);
        perSecond *= SeesMe(lone, 30f, 60f) ? 0.5f : 2f;
        if (lone.isInsideFactory) perSecond *= 1.3f; // dark halls, nobody hears it
        if (Random.value > perSecond) return;
        Plugin.Log.LogInfo($"{Copied.playerUsername}'s Masked ambushes {lone.playerUsername} (alone with it for {Time.time - aloneSince:0}s)");
        AddEvent($"{lone.playerUsername} is alone with you and nobody would see, so you're going for them.");
        SetBehaviour(Behaviour.Attack, lone, 0f);
        PushOverlay();
    }

    // From SetBehaviour: every attack starts by sizing them up.
    private void StartAttackPhase()
    {
        lunging = false;
        stalkUntil = Time.time + Random.Range(8f, 14f);
    }

    // The point it's no longer pretending: cover gone, witnesses count from here.
    private void Lunge(string why)
    {
        if (lunging) return;
        lunging = true;
        if (!CoverBlown) Plugin.Log.LogInfo($"{Copied.playerUsername}'s Masked attacks {Target?.playerUsername} ({why})");
        CoverBlown = true;
        attackWitnesses.Clear();
    }

    private bool TargetUnaware(PlayerControllerB t) =>
        !CoverBlown && !FoundOutBy(t) && Suspicion(t) < 0.4f && Time.time >= threatenedUntil;

    // One attack step (AI tick).
    private void Attack()
    {
        if (Target == null || !CanAttack(out _))
        {
            SetBehaviour(Behaviour.BlendIn, null, 0f);
            return;
        }
        if (!lunging)
        {
            float d = Dist(Target);
            Vector3 v = VelocityOf(Target);
            Vector3 away = Target.transform.position - transform.position;
            away.y = 0f;
            bool running = v.magnitude > 4.5f && away.sqrMagnitude > 0.01f && Vector3.Dot(v.normalized, away.normalized) > 0.5f;
            if (!TargetUnaware(Target))
            {
                Lunge("they know");
            }
            else if (d < 2.8f)
            {
                Lunge("close enough");
            }
            else if (running && d < 12f)
            {
                Lunge("they started running");
            }
            else if (Time.time >= stalkUntil)
            {
                Lunge("done waiting");
            }
            else if (!SameSide(Target) || Witnessed(Target))
            {
                // Someone else showed up, or they went through a door: back to being a friend for now.
                nextAmbushRoll = Time.time + 15f;
                SetBehaviour(Behaviour.Follow, Target, 10f);
                return;
            }
            else
            {
                Stalk(Target, d);
                return;
            }
        }
        wantSprint = true;
        if (lookPlayer != Target) LookAtPlayer(Target, 30f);
        GoTo(Target.transform.position, Target.isInsideFactory);
    }

    // Walks up like it wants to show them something: walking speed (it jogs to catch up when far), aiming for their
    // back when they're turned away, looking around normally instead of staring.
    private void Stalk(PlayerControllerB t, float d)
    {
        wantSprint = d > 18f;
        faceLookWhileMoving = false;
        Vector3 goal = t.transform.position;
        if (d < 8f && !SeesMe(t, 20f, 70f))
        {
            Vector3 behind = goal - t.transform.forward * 1.2f;
            if (TryNavPoint(behind, 1.5f, out Vector3 nav)) goal = nav;
        }
        GoTo(goal, t.isInsideFactory);
    }

    // Anyone besides the target who'd see it now.
    private bool Witnessed(PlayerControllerB t) =>
        AlivePlayers().Any(o => o != t && ((SameSide(o) && Dist(o) < 25f) || o.HasLineOfSightToPosition(Head, 70f, 60)));

    private string AttackOverlay() =>
        Current == Behaviour.Attack ? (lunging ? "lunging" : $"stalking ({Mathf.Max(0f, stalkUntil - Time.time):0}s)")
        : aloneWith != null ? $"alone with {aloneWith.playerUsername} {Time.time - aloneSince:0}s" : "";
}
