using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace PlayerMimics;

// Host only. Monsters, the way a player sees them: what each one is called, and whether players ignore it, back
// away from it, sneak away from it or run.
internal sealed partial class MimicBrain
{
    private enum Reaction
    {
        Ignore,
        BackAway,
        Sneak,
        Flee,
    }

    private readonly struct Danger
    {
        public readonly string Name;
        public readonly Reaction Reaction;
        public readonly float Range; // how close before it reacts

        public Danger(string name, Reaction reaction, float range = 0f)
        {
            Name = name;
            Reaction = reaction;
            Range = range;
        }
    }

    private readonly HashSet<EnemyAI> monstersSeen = new();
    private float nextThreatScan;

    private static bool Hunting(EnemyAI e) => e.targetPlayer != null && e.movingTowardsTargetPlayer;

    // Unknown (modded) monsters: run when one gets close.
    private static Danger Judge(EnemyAI e)
    {
        int state = e.currentBehaviourStateIndex;
        return e.GetType().Name switch
        {
            "DoublewingAI" => new("manticoil", Reaction.Ignore),
            "DocileLocustBeesAI" => new("locusts", Reaction.Ignore),
            "DressGirlAI" => new("", Reaction.Ignore), // only the haunted player sees her
            "FlowerSnakeEnemy" => new("tulip snake", Reaction.Ignore),
            "SnowmanSimpleAI" => new("snowman", Reaction.Ignore),
            "HoarderBugAI" => Hunting(e) ? new("hoarding bug", Reaction.Flee, 12f) : new("hoarding bug", Reaction.BackAway, 3f),
            "ButlerEnemyAI" => new("butler", Reaction.BackAway, 3f),
            "PufferAI" => new("spore lizard", Reaction.BackAway, 5f),
            "BaboonBirdAI" => Hunting(e) ? new("baboon hawk", Reaction.Flee, 20f) : new("baboon hawk", Reaction.BackAway, 8f),
            "GiantKiwiAI" => Hunting(e) ? new("sapsucker", Reaction.Flee, 25f) : new("sapsucker", Reaction.BackAway, 12f),
            "SpringManAI" => new("coil-head", Reaction.BackAway, 10f),
            "FlowermanAI" => new("bracken", Reaction.BackAway, 12f),
            "BlobAI" => new("slime", Reaction.BackAway, 5f),
            "CentipedeAI" => new("snare flea", Reaction.BackAway, 5f),
            "MouthDogAI" => new("eyeless dog", Reaction.Sneak, 25f),
            "SandSpiderAI" => new("spider", Reaction.Flee, 15f),
            "CrawlerAI" => new("thumper", Reaction.Flee, 20f),
            "NutcrackerEnemyAI" => new("nutcracker", Reaction.Flee, state == 2 ? 40f : 15f),
            "ForestGiantAI" => new("giant", Reaction.Flee, 40f),
            "SandWormAI" => new("worm", Reaction.Flee, 20f),
            "RadMechAI" => new("old bird", Reaction.Flee, 45f),
            "ClaySurgeonAI" => new("barber", Reaction.Flee, 10f),
            "BushWolfEnemy" => new("kidnapper fox", Reaction.Flee, 20f),
            "CaveDwellerAI" => state == 0 ? new("maneater baby", Reaction.Ignore) : new("maneater", Reaction.Flee, 15f),
            "RedLocustBees" => state >= 1 ? new("bees", Reaction.Flee, 15f) : new("bees", Reaction.BackAway, 6f),
            "ButlerBeesEnemyAI" => new("hornets", Reaction.Flee, 15f),
            "JesterAI" => state >= 1 ? new("jester", Reaction.Flee, 40f) : new("jester box", Reaction.BackAway, 6f),
            // A vanilla one, wearing its mask. One of ours (or one still being set up, mask showing for a moment) is kin.
            "MaskedPlayerEnemy" => MimicDisguise.Of(e) != null || HostServices.Instance == null ||
                                   HostServices.Instance.SettingUp((MaskedPlayerEnemy)e)
                ? new("Masked", Reaction.Ignore)
                : new("Masked", Reaction.Flee, 15f),
            _ => new(e.enemyType != null ? e.enemyType.enemyName : "monster", Reaction.Flee, 8f),
        };
    }

    private bool IsMonster(EnemyAI? e) =>
        e != null && e != Masked && !e.isEnemyDead && e.gameObject.activeInHierarchy && e.isOutside == Masked.isOutside &&
        MimicDisguise.Of(e) == null; // another disguised Masked counts as a player

    private static Vector3 MonsterPoint(EnemyAI e) => e.eye != null ? e.eye.position : e.transform.position + Vector3.up;

    private bool CanSee(EnemyAI e) =>
        !Physics.Linecast(Head, MonsterPoint(e), StartOfRound.Instance.collidersAndRoomMaskAndDefault, QueryTriggerInteraction.Ignore);

    private IEnumerable<EnemyAI> Monsters(float range)
    {
        var round = RoundManager.Instance;
        if (round == null) return Enumerable.Empty<EnemyAI>();
        return round.SpawnedEnemies.Where(IsMonster).Where(e => Vector3.Distance(e.transform.position, transform.position) < range);
    }

    private EnemyAI? NearestMonster() =>
        Monsters(30f).Where(e => Judge(e).Reaction != Reaction.Ignore)
            .OrderBy(e => Vector3.Distance(e.transform.position, transform.position)).FirstOrDefault();

    private readonly Dictionary<EnemyAI, float> reactAt = new();

    private void PerceiveThreats()
    {
        if (Time.time < nextThreatScan) return;
        nextThreatScan = Time.time + 0.4f;
        monstersSeen.RemoveWhere(e => e == null || e.isEnemyDead);
        EnemyAI? worst = null;
        Danger worstDanger = default;
        float worstDistance = 0f;
        foreach (var e in Monsters(50f))
        {
            var danger = Judge(e);
            if (danger.Reaction == Reaction.Ignore) continue;
            float d = Vector3.Distance(e.transform.position, transform.position);
            if (d > 4f && !CanSee(e)) continue; // closer than that it can hear it
            if (monstersSeen.Add(e) && d < 30f)
            {
                AddEvent($"You spot a {danger.Name} {d:0} m away.");
                if (Current is not (Behaviour.Attack or Behaviour.Flee)) LookAtPoint(MonsterPoint(e), UnityEngine.Random.Range(1f, 2f));
            }
            if (d > danger.Range) continue;
            if (worst == null || danger.Reaction > worstDanger.Reaction || (danger.Reaction == worstDanger.Reaction && d < worstDistance))
            {
                worst = e;
                worstDanger = danger;
                worstDistance = d;
            }
        }
        if (worst == null || (Current == Behaviour.Attack && lunging)) return; // in the middle of a kill it keeps going
        // A player needs a moment to react to something new (less when it's right on them).
        if (!(Fleeing && fleeMonster == worst))
        {
            if (!reactAt.TryGetValue(worst, out float at) || Time.time - at > 8f)
            {
                reactAt[worst] = at = Time.time + UnityEngine.Random.Range(0.2f, 0.55f) * (worstDistance < 4f ? 0.6f : 1f);
            }
            if (Time.time < at) return;
        }
        switch (worstDanger.Reaction)
        {
            case Reaction.Flee: StartFlee(worst, worstDanger, FleeStyle.Run, UnityEngine.Random.Range(4f, 7f), worstDistance); break;
            case Reaction.Sneak: StartFlee(worst, worstDanger, FleeStyle.Sneak, UnityEngine.Random.Range(4f, 6f), worstDistance); break;
            case Reaction.BackAway: StartFlee(worst, worstDanger, FleeStyle.BackAway, UnityEngine.Random.Range(2f, 3.5f), worstDistance); break;
        }
    }

    private void StartFlee(EnemyAI e, Danger danger, FleeStyle style, float seconds, float distance)
    {
        bool fresh = !Fleeing || fleeMonster != e || fleeStyle != style;
        fleeMonster = e;
        fleeFrom = null;
        fleeOrigin = e.transform.position;
        fleeStyle = style;
        fleeUntil = Mathf.Max(fleeUntil, Time.time + seconds);
        if (!fresh)
        {
            behaviourUntil = fleeUntil;
            return;
        }
        SetBehaviour(Behaviour.Flee, null, fleeUntil - Time.time);
        if (style == FleeStyle.Run)
        {
            AddEvent($"A {danger.Name} is {distance:0} m from you! You're running.", "flee" + e.GetInstanceID(), 15f, decideNow: true);
            if (Crew(30f).Any()) MustTalk($"warn the others about the {danger.Name}, short, like a player shouting on voice chat", 6f);
        }
        else
        {
            AddEvent($"You're {(style == FleeStyle.Sneak ? "sneaking" : "backing")} away from a {danger.Name}.",
                "away" + e.GetInstanceID(), 30f);
            if (danger.Name is "coil-head" or "bracken" or "eyeless dog" && Crew(25f).Any())
            {
                MustTalk($"tell the others there's a {danger.Name}, the way players call it out", 6f);
            }
        }
        Plugin.Log.LogDebug($"{Copied.playerUsername}'s Masked: {style} from {danger.Name} at {distance:0} m");
    }

    private JArray MonstersJson()
    {
        var list = new JArray();
        foreach (var e in Monsters(40f).OrderBy(e => Vector3.Distance(e.transform.position, transform.position)).Take(4))
        {
            var danger = Judge(e);
            float d = Vector3.Distance(e.transform.position, transform.position);
            if (danger.Name.Length == 0 || (d > 10f && !CanSee(e))) continue; // only what a player there would know about
            list.Add(new JObject
            {
                ["name"] = danger.Name,
                ["distance_m"] = Mathf.RoundToInt(d),
                ["chasing"] = Hunting(e) ? e.targetPlayer.playerUsername : "",
            });
        }
        return list;
    }

    // ---- for MonsterLure

    internal bool HasCrewWithin(float range) => Crew(range).Any();

    internal Vector3 HeadPoint => Head;

    internal void OnMonsterClose(EnemyAI e)
    {
        string name = Judge(e).Name;
        AddEvent($"The {name} almost got you!", decideNow: true);
        if (Crew(30f).Any()) MustTalk($"react to the {name} almost getting you", 6f);
    }
}

// Monsters that roam looking for players go for a Masked standing near people too, so nobody notices a "player" the
// monsters ignore. The Masked sees it coming and runs like a player would. Enemy AI only runs on the host.
[HarmonyPatch]
internal static class MonsterLure
{
    private sealed class Lure
    {
        public MimicBrain Mimic = null!;
        public float Until;
    }

    private static readonly Dictionary<EnemyAI, Lure> active = new();
    private static readonly Dictionary<EnemyAI, float> nextTry = new();
    private static bool failed;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(SandSpiderAI), nameof(EnemyAI.DoAIInterval));
        yield return AccessTools.Method(typeof(CrawlerAI), nameof(EnemyAI.DoAIInterval));
        yield return AccessTools.Method(typeof(BlobAI), nameof(EnemyAI.DoAIInterval));
        yield return AccessTools.Method(typeof(ForestGiantAI), nameof(EnemyAI.DoAIInterval));
    }

    internal static void Reset()
    {
        active.Clear();
        nextTry.Clear();
    }

    private static void Postfix(EnemyAI __instance)
    {
        if (failed) return;
        try
        {
            Step(__instance);
        }
        catch (Exception e)
        {
            failed = true;
            Plugin.Log.LogWarning($"Turned off monsters noticing Masked after an error: {e}");
        }
    }

    private static void Step(EnemyAI m)
    {
        if (!Cfg.MonstersNoticeMimics.Value || MimicBrain.All.Count == 0 || m.isEnemyDead || !m.IsOwner)
        {
            active.Remove(m);
            return;
        }
        float now = Time.time;
        if (active.TryGetValue(m, out var lure))
        {
            var brain = lure.Mimic;
            bool keep = brain != null && brain.Masked != null && !brain.Masked.isEnemyDead && now < lure.Until && Idle(m) &&
                        brain.Masked.isOutside == m.isOutside;
            float d = keep ? Vector3.Distance(m.transform.position, brain!.transform.position) : 0f;
            float reach = m is ForestGiantAI ? 6f : 2f;
            if (!keep || d < reach)
            {
                active.Remove(m);
                nextTry[m] = now + 20f;
                if (keep) brain!.OnMonsterClose(m);
                return;
            }
            Steer(m, brain!.transform.position);
            return;
        }
        if (nextTry.TryGetValue(m, out float next) && now < next) return;
        nextTry[m] = now + 1f;
        if (!Idle(m)) return;
        float range = m is ForestGiantAI ? 30f : m is BlobAI ? 6f : 15f;
        Vector3 eye = m.eye != null ? m.eye.position : m.transform.position + Vector3.up;
        foreach (var brain in MimicBrain.All)
        {
            if (brain == null || brain.Masked == null || brain.Masked.isEnemyDead || brain.Masked.isOutside != m.isOutside) continue;
            if (Vector3.Distance(m.transform.position, brain.transform.position) > range || !brain.HasCrewWithin(30f)) continue;
            if (Physics.Linecast(eye, brain.HeadPoint, StartOfRound.Instance.collidersAndRoomMaskAndDefault, QueryTriggerInteraction.Ignore)) continue;
            active[m] = new Lure { Mimic = brain, Until = now + 10f };
            Plugin.Log.LogInfo($"{m.enemyType.enemyName} goes for {brain.Copied.playerUsername}'s Masked");
            Steer(m, brain.transform.position);
            return;
        }
    }

    // Roaming, not after anyone.
    private static bool Idle(EnemyAI m) =>
        m.currentBehaviourStateIndex == 0 && m.targetPlayer == null && !(m is ForestGiantAI giant && giant.chasingPlayer != null);

    private static void Steer(EnemyAI m, Vector3 target)
    {
        if (m is ForestGiantAI giant)
        {
            // Giants have their own "go and look at that" mode.
            giant.investigating = true;
            giant.hasBegunInvestigating = false;
            giant.investigatePosition = target;
            return;
        }
        // The others roam with a search routine that would pick its own spot again.
        if (m.currentSearch != null && m.currentSearch.inProgress) m.StopSearch(m.currentSearch, clear: false);
        m.movingTowardsTargetPlayer = false;
        m.SetDestinationToPosition(target);
    }
}
