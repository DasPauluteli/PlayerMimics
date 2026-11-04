using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using GameNetcodeStuff;
using UnityEngine;

namespace PlayerMimics;

// Host only. A player has figured out what it is: they called it a mimic and then ran from it or told it to stay
// away, or their suspicion went past CoverBlownSuspicion. Pretending is pointless then, so it goes after them
// right away instead of waiting for the LLM, which kept playing along.
internal sealed partial class MimicBrain
{
    // Said after an accusation, these mean "I know what you are" (English and German, whole words).
    private static readonly string[] warningWords =
    {
        "stay away", "get away", "go away", "back off", "don't come", "dont come", "leave me alone",
        "it's following", "its following", "following me", "chasing me",
        // Sending it somewhere else: "go the other way", "you go first".
        "other way", "go first", "you first", "walk in front", "in front of me", "go ahead",
        "bleib weg", "geh weg", "hau ab", "lauf weg", "renn weg", "verfolgt mich", "komm nicht", "lass mich",
        "andere richtung", "geh vor", "du zuerst",
    };

    private readonly HashSet<int> foundOut = new(); // these players know; it hunts them
    private readonly Dictionary<int, float> accusedAt = new();
    private readonly Dictionary<int, float> runningSince = new();
    private readonly Dictionary<int, float> lastLookedAt = new();

    private bool FoundOutBy(PlayerControllerB p) => foundOut.Contains((int)p.playerClientId);

    private bool RecentlyAccused(PlayerControllerB p) =>
        accusedAt.TryGetValue((int)p.playerClientId, out float t) && Time.time - t < 90f;

    // Going after someone who knows: a routine answer from the LLM doesn't call that off.
    private bool HuntingKnower => Current == Behaviour.Attack && Target != null && !Target.isPlayerDead && FoundOutBy(Target);

    private IEnumerable<PlayerControllerB> AliveFoundOut() => AlivePlayers().Where(FoundOutBy);

    // From OnSpeech: "you're a mimic" and then "stay away" (or both at once) means they know.
    private void HeardWarning(PlayerControllerB p, string lower, bool accused)
    {
        if (accused) accusedAt[(int)p.playerClientId] = Time.time;
        bool warning = warningWords.Any(w => Regex.IsMatch(lower, $@"\b{Regex.Escape(w)}\b"));
        if (warning && (accused || RecentlyAccused(p) || Suspicion(p) >= 0.3f))
        {
            FoundOut(p, "called you out and told you to stay away");
        }
    }

    // From Perceive: someone sprinting away from it after looking at it. After an accusation (or when already
    // suspicious) that means they know; otherwise it only makes them a bit more suspicious.
    private void WatchRunners(List<PlayerControllerB> alive, float dt)
    {
        foreach (var p in alive)
        {
            int id = (int)p.playerClientId;
            if (!IsAudience(p) || !SameSide(p)) continue;
            if (SeesMe(p, 30f, 40f)) lastLookedAt[id] = Time.time;
            Vector3 away = p.transform.position - transform.position;
            away.y = 0f;
            Vector3 v = VelocityOf(p);
            bool runningAway = Dist(p) < 25f && away.sqrMagnitude > 0.01f && (v.magnitude > 5.2f || (p.isSprinting && v.magnitude > 3f)) &&
                               Vector3.Dot(v.normalized, away.normalized) > 0.6f &&
                               lastLookedAt.TryGetValue(id, out float looked) && Time.time - looked < 6f;
            if (!runningAway)
            {
                runningSince.Remove(id);
                continue;
            }
            if (!runningSince.TryGetValue(id, out float since)) runningSince[id] = since = Time.time;
            if (Time.time - since < 1.5f || FoundOutBy(p)) continue; // a quick dash isn't running away
            if (RecentlyAccused(p) || Suspicion(p) >= 0.4f)
            {
                FoundOut(p, "called you a mimic and are running away from you");
                continue;
            }
            AddSuspicion(p, 0.05f * dt);
            AddEvent($"{p.playerUsername} is running away from you", "runaway" + id, 15f);
        }
    }

    private void FoundOut(PlayerControllerB p, string how)
    {
        int id = (int)p.playerClientId;
        if (!foundOut.Add(id)) return;
        knowsTruth.Add(id); // their suspicion stays at 1
        suspicion[id] = 1f;
        Plugin.Log.LogInfo($"{Copied.playerUsername}'s Masked: {p.playerUsername} found it out ({how})");
        AddEvent($"{p.playerUsername} figured out what you are: they {how}. Pretending is pointless now.", decideNow: true);
    }

    // The reflex (AI tick): someone who knows is close and on its side of the door: go after them now.
    private void HuntIfFoundOut()
    {
        if (Cfg.NoAttack.Value || Current == Behaviour.Attack || Fleeing || Masked.inSpecialAnimation) return;
        var prey = AliveFoundOut().Where(p => SameSide(p) && Dist(p) < 25f).OrderBy(Dist).FirstOrDefault();
        if (prey == null) return;
        if (IsTask(Current)) FinishTask(TaskGaveUp());
        SetBehaviour(Behaviour.Attack, prey, 0f);
        PushOverlay();
    }
}
