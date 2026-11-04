using System;
using System.Collections.Generic;
using System.Linq;
using GameNetcodeStuff;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace PlayerMimics;

internal enum Behaviour
{
    BlendIn,
    Follow,
    Stare,
    Wander,
    Lure,
    Attack,
    Flee,
    Emote,
    // Item and door tasks (MimicItems.cs): done like a player, then back to what it was doing.
    PickUp,
    Drop,
    Entrance,
    GoToShip,
}

// Host only. Decides what one Masked does. The LLM picks behaviours and lines; the code enforces the rules and decides
// when talking makes sense. Moving and looking are in MimicMotion.cs, monsters in MimicThreats.cs.
internal sealed partial class MimicBrain : MonoBehaviour
{
    internal static readonly List<MimicBrain> All = new();
    private static int pendingRequests;

    internal MaskedPlayerEnemy Masked = null!;
    internal MimicDisguise Disguise = null!;
    internal PlayerControllerB Copied => Disguise.Player;
    // The name it goes by: the copied player's, or Debug > MimicName. Renamed, it's its own player with their looks and voice.
    internal string Name => Disguise.Name;
    internal bool Renamed => Disguise.Renamed;

    internal Behaviour Current = Behaviour.BlendIn;
    internal PlayerControllerB? Target;
    private float behaviourStarted;
    private float behaviourUntil;
    private Behaviour afterEmote = Behaviour.BlendIn;
    private int emote;
    private string signText = "";

    // Blown once someone sees it attack. A kill nobody else saw leaves it intact.
    internal bool CoverBlown;
    private readonly HashSet<PlayerControllerB> attackWitnesses = new();

    private readonly Dictionary<int, float> suspicion = new();
    private readonly HashSet<int> knowsTruth = new(); // saw its player die; their suspicion never fades
    private readonly List<(float time, string text)> events = new();
    private readonly Dictionary<string, float> eventCooldowns = new();
    private readonly List<string> saidRecently = new();
    internal float SecondsWithGroup;
    private float threatenedUntil;

    // Talking: it answers what's said to it (once), speaks up when something happens, and otherwise only now and then.
    private PlayerControllerB? replyTo;
    private string replyText = "";
    private float replyAt = -100f;
    private string mustTalkWhy = "";
    private float mustTalkUntil;
    private float nextSmallTalk;
    private float lastHeardAt = -100f;
    private bool reassureOnContact;

    private bool requestPending;
    private bool pendingIsReply;
    private int requestSeq; // the newest /decide; answers to older ones (replaced by a reply) are ignored
    private bool redecide;
    private float nextDecision;
    private float lastDecisionAt = -100f;
    private bool urgent;
    private int eventSerial, decidedSerial; // nothing new since the last decision: the next one can wait longer
    private string earlyStream = "", earlyText = ""; // the server already started voicing this line
    private float lastLine = -100f;
    internal string LastReason = "";
    private string lastSay = "";

    private MimicVisualState sentState;
    private bool stateSent;
    private float lastOverlay;

    internal void Init(MaskedPlayerEnemy masked, MimicDisguise disguise)
    {
        Masked = masked;
        Disguise = disguise;
        All.Add(this);
        behaviourStarted = Time.time;
        nextDecision = Time.time + 1.5f;
        nextSmallTalk = Time.time + SmallTalkDelay();
        InitMotion();
        if (disguise.Revived) OnRevived(HostServices.Instance?.TakeWitnesses(masked) ?? Array.Empty<PlayerControllerB>());
    }

    private void OnDestroy() => All.Remove(this);

    // ---- helpers

    private static IEnumerable<PlayerControllerB> AlivePlayers() =>
        StartOfRound.Instance.allPlayerScripts.Where(p => p != null && p.isPlayerControlled && !p.isPlayerDead);

    internal Vector3 Head => Masked.eye != null ? Masked.eye.position : transform.position + Vector3.up * 2f;

    private bool SameSide(PlayerControllerB p) => p.isInsideFactory == !Masked.isOutside;

    private float Dist(PlayerControllerB p) => Vector3.Distance(transform.position, p.transform.position);

    private bool Sees(PlayerControllerB p, Vector3 point, float range, float angle)
    {
        Vector3 cam = p.gameplayCamera.transform.position;
        if (Vector3.Distance(cam, point) > range) return false;
        if (Vector3.Angle(p.gameplayCamera.transform.forward, point - cam) > angle) return false;
        return !Physics.Linecast(cam, point, StartOfRound.Instance.collidersAndRoomMaskAndDefault, QueryTriggerInteraction.Ignore);
    }

    private bool SeesMe(PlayerControllerB p, float range = 30f, float angle = 35f) => Sees(p, Head, range, angle);

    // Solo: the copied player is the only one around (or SoloMode is on), so they are the one to fool.
    private bool Solo => Cfg.SoloMode.Value || !AlivePlayers().Any(p => p != Copied);

    // Listeners that matter: people other than the copied player, unless solo.
    private bool IsAudience(PlayerControllerB p) => p != Copied || Solo;

    private IEnumerable<PlayerControllerB> Crew(float range) =>
        AlivePlayers().Where(p => IsAudience(p) && SameSide(p) && Dist(p) < range);

    internal float Suspicion(PlayerControllerB p) => suspicion.TryGetValue((int)p.playerClientId, out float s) ? s : 0f;

    private void AddSuspicion(PlayerControllerB p, float amount)
    {
        int id = (int)p.playerClientId;
        suspicion[id] = Mathf.Clamp01(Suspicion(p) + amount);
    }

    private float MaxSuspicion() => AlivePlayers().Where(IsAudience).Select(Suspicion).DefaultIfEmpty(0f).Max();

    internal void AddEvent(string text, string? cooldownKey = null, float cooldown = 0f, bool decideNow = false)
    {
        if (cooldownKey != null)
        {
            if (eventCooldowns.TryGetValue(cooldownKey, out float until) && Time.time < until) return;
            eventCooldowns[cooldownKey] = Time.time + cooldown;
        }
        events.Add((Time.time, text));
        if (events.Count > 12) events.RemoveAt(0);
        eventSerial++;
        if (decideNow) urgent = true;
    }

    private void MustTalk(string why, float seconds)
    {
        mustTalkWhy = why;
        mustTalkUntil = Time.time + seconds;
    }

    // ---- rules

    internal bool CanAttack(out string reason)
    {
        reason = "";
        if (Cfg.NoAttack.Value)
        {
            reason = "attacking is turned off";
            return false;
        }
        if (CoverBlown || Current == Behaviour.Attack) { reason = "already attacking"; return true; }
        if (Time.time < threatenedUntil) { reason = "you were attacked"; return true; }
        if (AliveFoundOut().Any()) { reason = "they figured out what you are"; return true; }
        if (MaxSuspicion() >= Cfg.CoverBlownSuspicion.Value) { reason = "your cover is about to blow"; return true; }
        if (AloneLongEnough) { reason = $"{aloneWith!.playerUsername} is alone with you and nobody else would see"; return true; }
        if (SecondsWithGroup >= Cfg.MinSecondsBeforeAttack.Value) { reason = "you've spent enough time with them"; return true; }
        reason = $"too early: blend in for {Mathf.CeilToInt(Cfg.MinSecondsBeforeAttack.Value - SecondsWithGroup)} more seconds";
        return false;
    }

    private (bool allowed, string why) TalkPolicy()
    {
        if (Time.time - lastLine < Cfg.MinSecondsBetweenLines.Value) return (false, "you just said something");
        if (!Crew(40f).Any()) return (false, "nobody is close enough to hear you");
        if (replyTo != null && Time.time - replyAt < 15f) return (true, $"answer {replyTo.playerUsername}");
        if (mustTalkWhy.Length > 0 && Time.time < mustTalkUntil) return (true, mustTalkWhy);
        // Small talk waits while another mimic nearby just spoke, so two of them don't chat over each other.
        bool otherJustSpoke = All.Any(b => b != this && b != null && Time.time - b.lastLine < 6f &&
                                           Vector3.Distance(b.transform.position, transform.position) < 40f);
        if (Time.time >= nextSmallTalk && Time.time - lastHeardAt > 8f && !otherJustSpoke)
        {
            return (true, "optional: one short natural remark, or stay quiet");
        }
        return (false, "nothing to answer");
    }

    // Without the AI server: play back something its player really said, now and then, like Mirage.
    private float nextClip;

    private void RandomClip()
    {
        var host = HostServices.Instance;
        if (host == null || !Cfg.RandomClips.Value || (Cfg.UseLlm.Value && host.AiOnline)) return;
        if (Time.time < nextClip) return;
        nextClip = Time.time + Cfg.RandomClipSeconds.Value * UnityEngine.Random.Range(0.6f, 1.6f);
        if (Time.time - lastLine < Cfg.MinSecondsBetweenLines.Value || !Crew(40f).Any() || !host.HasConsent(Copied)) return;
        // Not over another Masked that just spoke.
        bool otherJustSpoke = All.Any(b => b != this && b != null && Time.time - b.lastLine < 4f &&
                                           Vector3.Distance(b.transform.position, transform.position) < 40f);
        if (otherJustSpoke) return;
        var clip = MimicClips.Pick(HostServices.PlayerKey(Copied));
        if (clip == null) return;
        lastLine = Time.time;
        host.PlayClip(this, clip);
    }

    private float SmallTalkDelay() => Cfg.SmallTalkSeconds.Value * UnityEngine.Random.Range(0.7f, 1.5f);

    // ---- events from patches and host services

    // Got up where its player died. If someone was close enough to hear the death, it says it's fine right away.
    private void OnRevived(PlayerControllerB[] witnesses)
    {
        string name = Copied.playerUsername;
        // Revive > OnlyWhenUnobserved off: whoever saw the death knows this isn't really them.
        var saw = witnesses.Where(p => p != null && p != Copied && p.isPlayerControlled && !p.isPlayerDead).ToList();
        if (saw.Count > 0)
        {
            foreach (var p in saw)
            {
                knowsTruth.Add((int)p.playerClientId);
                AddSuspicion(p, 1f);
            }
            AddEvent($"You just got up where the real {name} died. {string.Join(" and ", saw.Take(3).Select(p => p.playerUsername))} " +
                     $"saw {name} die, so they know you're not really {name}.", decideNow: true);
            LookAtPlayer(saw.OrderBy(Dist).First(), 2f);
            return;
        }
        var near = Crew(45f).Where(p => p != Copied).OrderBy(Dist).ToList();
        if (near.Count == 0)
        {
            AddEvent($"You just got up where the real {name} died. Nobody saw it or was close.");
            reassureOnContact = true;
            return;
        }
        string who = string.Join(" and ", near.Take(2).Select(p => p.playerUsername));
        AddEvent($"You just got up where the real {name} died a moment ago. Nobody saw it, but {who} " +
                 $"{(near.Count == 1 ? "was" : "were")} close enough to hear it. Let them know you're fine the way {name} would " +
                 "after a close call, in your own words (not \"I'm good, that was close\"). " +
                 "Never say you died.", decideNow: true);
        MustTalk("you just had a close call nearby: let them know you're fine, in your own words", 25f);
        nextDecision = Time.time + 0.6f;
        LookAtPlayer(near[0], 2f);
    }

    internal void OnHit(PlayerControllerB? by)
    {
        if (by == null) return;
        string item = by.currentlyHeldObjectServer != null ? by.currentlyHeldObjectServer.itemProperties.itemName : "their fists";
        AddEvent($"{by.playerUsername} hit you with {item}!", decideNow: true);
        AddSuspicion(by, 0.6f);
        threatenedUntil = Time.time + 40f;
        if (!CoverBlown) MustTalk($"{by.playerUsername} just hit you: react like a player who got hit by a friend", 8f);
        // Hit with a sign (stop sign, yield sign) while still disguised: run.
        if (item.IndexOf("sign", StringComparison.OrdinalIgnoreCase) >= 0 && !CoverBlown)
        {
            FleeFrom(by, 8f);
        }
        else if (CoverBlown && !Cfg.NoAttack.Value)
        {
            SetBehaviour(Behaviour.Attack, by, 20f); // switch to whoever angered it
        }
    }

    private static readonly string[] suspiciousWords =
    {
        "mimic", "masked", "fake", "impostor", "imposter", "monster", "not real", "are you real", "prove", "is that you",
        "not you", "who are you", "that's not", "thats not", "sus",
        // Sarcasm: "sure, you're totally real", "you don't have to tell me".
        "you're real", "youre real", "you are real", "totally real", "very real", "don't have to tell me",
        "dont have to tell me", "suspicious", "bist du echt", "du bist echt",
    };

    // How well placed it is to answer this line (lower is better), or -1 when it wouldn't. HostServices lets only the
    // best one answer, so two mimics never both reply to the same thing.
    internal float ReplyScore(PlayerControllerB speaker, string text)
    {
        if (Masked == null || Masked.isEnemyDead) return -1f;
        if (speaker == Copied && !Solo && !Renamed) return -1f;
        if (!SameSide(speaker) || Dist(speaker) > 25f) return -1f;
        string lower = text.ToLowerInvariant();
        bool named = NameTokens(Name).Any(lower.Contains);
        bool toSomeoneElse = !named && AlivePlayers().Any(p => p != speaker && (p != Copied || Renamed) &&
                                                              NameTokens(p.playerUsername).Any(lower.Contains));
        if (toSomeoneElse || (Dist(speaker) >= 15f && !named)) return -1f;
        return (named ? 0f : 100f) + Dist(speaker);
    }

    internal void OnSpeech(PlayerControllerB speaker, string text, bool mayReply = true)
    {
        if (speaker == Copied && !Solo && !Renamed)
        {
            if (Dist(speaker) < 30f && AlivePlayers().Any(p => p != speaker && Dist(p) < 25f))
            {
                AddEvent($"The real {Copied.playerUsername} is talking nearby: \"{text}\". People could notice two of you.",
                    "realtalk", 15f);
            }
            return;
        }
        if (!SameSide(speaker) || Dist(speaker) > 25f) return;
        lastHeardAt = Time.time;
        if (Dist(speaker) < 20f) GlanceAt(speaker);
        string lower = text.ToLowerInvariant();
        bool suspicious = suspiciousWords.Any(lower.Contains);
        bool named = NameTokens(Name).Any(lower.Contains);
        if (suspicious) AddSuspicion(speaker, named || Dist(speaker) < 10f ? 0.3f : 0.15f);
        HeardWarning(speaker, lower, suspicious);
        string who = speaker == Copied && !Renamed ? $"The real {speaker.playerUsername} (not you)" : speaker.playerUsername;
        AddEvent($"{who} said: \"{text}\"");
        // Worth an answer if it was said close by or to it by name, and not clearly to someone else.
        bool toSomeoneElse = !named && AlivePlayers().Any(p => p != speaker && (p != Copied || Renamed) &&
                                                              NameTokens(p.playerUsername).Any(lower.Contains));
        if (mayReply && !toSomeoneElse && (Dist(speaker) < 15f || named))
        {
            replyTo = speaker;
            replyText = text;
            replyAt = Time.time;
            DecideSoon();
        }
    }

    private static IEnumerable<string> NameTokens(string name) =>
        name.ToLowerInvariant().Split(new[] { ' ', '_', '-', '.' }, StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length >= 3);

    internal void OnPlayerDied(PlayerControllerB p)
    {
        if (Dist(p) < 40f) AddEvent($"{p.playerUsername} just died nearby.", decideNow: true);
        if (Dist(p) < 30f && p != Copied) MustTalk($"{p.playerUsername} just died near you: react", 6f);
    }

    // The vanilla kill finished. Nobody else saw it: the cover holds and it goes back to acting normal.
    internal void OnKilled(PlayerControllerB victim)
    {
        attackWitnesses.Remove(victim);
        attackWitnesses.RemoveWhere(p => p == null || p.isPlayerDead);
        if (attackWitnesses.Count == 0)
        {
            CoverBlown = false;
            SetBehaviour(Behaviour.BlendIn, null, 0f);
            AddEvent($"You killed {victim.playerUsername} with nobody else watching. Your cover is intact: act normal. If anyone " +
                     $"asks where {victim.playerUsername} is, you don't know.", decideNow: true);
        }
        else
        {
            foreach (var w in attackWitnesses) AddSuspicion(w, 1f);
            AddEvent($"You killed {victim.playerUsername}. {string.Join(", ", attackWitnesses.Select(w => w.playerUsername))} saw it.",
                decideNow: true);
        }
        Plugin.Log.LogInfo($"{Copied.playerUsername}'s Masked killed {victim.playerUsername}; " +
                           (CoverBlown ? $"seen by {string.Join(", ", attackWitnesses.Select(w => w.playerUsername))}" : "nobody saw it"));
    }

    // ---- the AI tick (DoAIInterval, about 5 times a second)

    internal void Tick()
    {
        if (Masked.isEnemyDead) return;
        float dt = Masked.AIIntervalTime;
        Perceive(dt);
        NoticeHazards();
        HuntIfFoundOut();
        GrabScrapInPassing();
        Ambush();
        RandomClip();

        if (Time.time >= nextDecision || (urgent && Time.time >= lastDecisionAt + 1.5f))
        {
            Decide();
        }
        if (Time.time >= behaviourUntil && Current != Behaviour.BlendIn && Current != Behaviour.Attack)
        {
            if (IsTask(Current)) FinishTask(TaskGaveUp());
            else SetBehaviour(Current == Behaviour.Emote ? afterEmote : Behaviour.BlendIn, Target, 0f);
        }
        Run();
        Gaze();
        SyncVisuals();
    }

    private void Perceive(float dt)
    {
        var alive = AlivePlayers().ToList();
        if (alive.Any(p => IsAudience(p) && SameSide(p) && Dist(p) < 25f)) SecondsWithGroup += dt;
        TrackPlayerMotion(alive);
        if (replyTo != null && Time.time - replyAt > 15f) replyTo = null;

        foreach (var key in suspicion.Keys.ToList())
        {
            suspicion[key] = knowsTruth.Contains(key) ? 1f : Mathf.Max(0f, suspicion[key] - 0.005f * dt);
        }

        foreach (var p in alive)
        {
            if (!SameSide(p) || p == Copied && !Solo) continue;
            float d = Dist(p);
            if (d > 30f) continue;
            if (p.performingEmote && SeesMe(p, 15f, 25f))
            {
                int n = p.playerBodyAnimator.GetInteger("emoteNumber") % 1000;
                if (n == BetterEmotesCompat.Sign)
                {
                    string text = SignText(p);
                    AddEvent($"{p.playerUsername} is holding up a sign at you" + (text.Length > 0 ? $" that says \"{text}\"" : ""),
                        "sign" + p.playerClientId, 8f, decideNow: true);
                    AddSuspicion(p, 0.15f * dt * 5f);
                }
                else if (n > 0 && n < BetterEmotesCompat.EmoteNames.Length)
                {
                    AddEvent($"{p.playerUsername} is doing the {BetterEmotesCompat.EmoteNames[n]} emote near you",
                        "emote" + p.playerClientId, 20f);
                }
                else if (TooManyEmotesCompat.PlayerEmote(p) is string tme)
                {
                    AddEvent($"{p.playerUsername} is doing the {tme} emote near you", "emote" + p.playerClientId, 20f);
                }
            }
            // Keeping their distance while watching you closely is suspicious.
            if (d > 6f && d < 14f && SeesMe(p, 14f, 10f) && Current != Behaviour.Flee) AddSuspicion(p, 0.03f * dt);
        }

        WatchRunners(alive, dt);

        // Being seen next to the real player is the worst giveaway.
        if (!Copied.isPlayerDead && Copied.isPlayerControlled && SameSide(Copied) && Dist(Copied) < 30f && !Solo && !Renamed)
        {
            Vector3 realHead = Copied.gameplayCamera.transform.position;
            foreach (var p in alive)
            {
                if (p == Copied) continue;
                if (SeesMe(p, 30f, 50f) && Sees(p, realHead, 30f, 50f))
                {
                    AddSuspicion(p, 0.5f);
                    AddEvent($"{p.playerUsername} can see you AND the real {Copied.playerUsername} at the same time!",
                        "seenwithreal" + p.playerClientId, 20f, decideNow: true);
                }
            }
        }

        // Another Masked copying the same player: being seen side by side is as bad as standing next to the real one.
        foreach (var twin in All)
        {
            if (twin == this || twin == null || twin.Masked == null || twin.Masked.isEnemyDead || twin.Copied != Copied) continue;
            if (twin.Masked.isOutside != Masked.isOutside || Vector3.Distance(twin.transform.position, transform.position) > 30f) continue;
            foreach (var p in alive)
            {
                if (!IsAudience(p) || !SeesMe(p, 30f, 50f) || !Sees(p, twin.Head, 30f, 50f)) continue;
                AddSuspicion(p, 0.4f);
                AddEvent($"{p.playerUsername} can see you AND another {Copied.playerUsername} at the same time! Get away from " +
                         "the other one.", "seenwithtwin" + p.playerClientId, 20f, decideNow: true);
            }
        }

        // Revived alone: the first time it finds the crew again, it says something like it got lost.
        if (reassureOnContact)
        {
            var found = alive.FirstOrDefault(p => IsAudience(p) && p != Copied && SameSide(p) && Dist(p) < 15f && SeesMe(p, 15f, 60f));
            if (found != null)
            {
                reassureOnContact = false;
                AddEvent($"You just found {found.playerUsername} again after getting separated (the real you died alone earlier; " +
                         "never say so).", decideNow: true);
                MustTalk("you just found the crew again: say something casual, like you got lost", 15f);
            }
        }

        if (Current == Behaviour.Attack && lunging)
        {
            foreach (var p in alive)
            {
                if (IsAudience(p) && SameSide(p) && SeesMe(p, 50f, 60f)) attackWitnesses.Add(p);
            }
        }
        PerceiveThreats();
    }

    private static string SignText(PlayerControllerB p)
    {
        var t = p.transform.Find("ScavengerModel/metarig/Sign/Text");
        var tmp = t != null ? t.GetComponent<TextMeshPro>() : null;
        return tmp != null ? tmp.text.Trim() : "";
    }

    // ---- deciding

    // Someone just talked to it: decide now instead of at the next tick. A request in flight that isn't an answer is
    // replaced (the server stops it); one that is an answer finishes first.
    private void DecideSoon()
    {
        if (requestPending && (replyTo == null || pendingIsReply))
        {
            redecide = true;
            return;
        }
        Decide();
    }

    private void Decide()
    {
        bool wasUrgent = urgent;
        urgent = false;
        // Nothing new happened, nobody to answer, nothing it has to say: no need to rethink as often.
        bool quiet = !wasUrgent && eventSerial == decidedSerial && replyTo == null && Time.time >= mustTalkUntil;
        nextDecision = Time.time + Cfg.DecisionInterval.Value * (quiet ? 2.5f : 1f) * UnityEngine.Random.Range(0.8f, 1.2f);
        if (Current == Behaviour.Emote && Time.time < behaviourUntil) return;
        var host = HostServices.Instance;
        if (quiet && NothingToThinkAbout()) return;
        if (Cfg.UseLlm.Value && host != null && host.AiOnline)
        {
            bool answering = replyTo != null;
            bool replace = requestPending && answering && !pendingIsReply;
            if (requestPending && !replace) return;
            if (!replace && pendingRequests >= 3) return;
            requestPending = true;
            pendingIsReply = answering;
            pendingRequests++;
            int seq = ++requestSeq;
            lastDecisionAt = Time.time;
            lastThoughtAt = Time.time;
            decidedSerial = eventSerial;
            var body = BuildContext(out var view);
            body["mimic"] = Masked.NetworkObjectId.ToString();
            if (Cfg.CollectTrainingData.Value) body["collect"] = true; // the server saves the prompt and answer as text
            var voice = host.VoiceRequest(this);
            if (voice != null) body["voice"] = voice;
            host.Post("/decide", body, 30, result =>
            {
                pendingRequests--;
                string stream = result?.Value<string>("stream") ?? "";
                if (seq != requestSeq) // replaced by a newer one while it was thinking
                {
                    if (stream.Length > 0) host.CancelLine(stream);
                    return;
                }
                requestPending = false;
                if (this == null || Masked == null || Masked.isEnemyDead)
                {
                    if (stream.Length > 0) host.CancelLine(stream);
                    return;
                }
                if (result != null && (result.Value<bool?>("stale") ?? false))
                {
                    // The server dropped it for a newer one we never sent (shouldn't happen); just think again soon.
                }
                else if (result == null)
                {
                    Scripted();
                }
                else
                {
                    if (Cfg.LogPrompts.Value)
                    {
                        Plugin.Log.LogInfo($"[decide] prompt:\n{result.Value<string>("prompt")}\n[decide] answer: {result.Value<string>("raw")}");
                    }
                    string say = result.Value<string>("say") ?? "";
                    earlyStream = stream;
                    earlyText = say.Trim();
                    OntoMe(result.Value<string>("onto_you") ?? "");
                    Apply(result.Value<string>("action") ?? "", result.Value<string>("target") ?? "", result.Value<string>("item") ?? "",
                        say, result.Value<string>("emote") ?? "", result.Value<string>("sign_text") ?? "",
                        result.Value<string>("reason") ?? "", view, answering);
                    if (earlyStream.Length > 0) host.CancelLine(earlyStream); // started but not used
                    earlyStream = earlyText = "";
                }
                if (redecide)
                {
                    redecide = false;
                    if (replyTo != null) Decide(); // something was said while it was thinking
                }
            });
        }
        else
        {
            Scripted();
        }
    }

    // Most routine decisions change nothing (no line, same behaviour), but they keep the GPU busy, so answers to
    // players come late. Those are skipped while it's just walking with the group and has nothing it may say or do,
    // and while another Masked nearby is answering someone.
    private float lastThoughtAt;

    private bool NothingToThinkAbout()
    {
        if (Cfg.UseLlm.Value && HostServices.Instance is { AiOnline: true } && !requestPending &&
            All.Any(b => b != this && b != null && b.requestPending && b.pendingIsReply))
        {
            return true;
        }
        if (Time.time - lastThoughtAt > 25f || IsTask(Current) || CanAttack(out _) || TalkPolicy().allowed) return false;
        return Current is Behaviour.Follow or Behaviour.BlendIn && Crew(40f).Any();
    }

    // The AI read what someone said as them being onto it (sarcasm, sending it the other way, avoiding it). Twice, or
    // after an accusation, means they know.
    private void OntoMe(string name)
    {
        var p = AlivePlayers().FirstOrDefault(x => x.playerUsername == name);
        if (p == null || !IsAudience(p)) return;
        if (Suspicion(p) >= 0.3f || RecentlyAccused(p)) FoundOut(p, "made it clear they know");
        else AddSuspicion(p, 0.35f);
    }

    // Debug > MimicName: it plays its own player who happens to have the copied player's looks and voice.
    private string RenamedContext()
    {
        string other = Copied.playerUsername;
        if (Copied.isPlayerDead || !Copied.isPlayerControlled) return $"you have {other}'s looks and voice; {other} is dead";
        if (Solo)
        {
            return $"{other} is the only other player here, {Dist(Copied):0} m away. They're the friend you're playing with " +
                   "this round: stay with them, talk to them and answer them like a friend would";
        }
        return $"you have {other}'s looks and voice; {other} is alive, {GameContext.Where(Copied)}";
    }

    // view: the items it was told about, by the ids the LLM can answer with.
    private JObject BuildContext(out Dictionary<string, GrabbableObject> view)
    {
        bool canAttack = CanAttack(out string attackReason);
        string location = GameContext.Where(transform.position, !Masked.isOutside);
        string real = Renamed ? RenamedContext()
            : Copied.isPlayerDead || !Copied.isPlayerControlled
            ? $"the real {Copied.playerUsername} is dead (only you know); never mention dying or coming back"
            : !SameSide(Copied) ? $"the real {Copied.playerUsername} is alive, {GameContext.Where(Copied)}"
            : Solo && FoundOutBy(Copied) ? $"the real {Copied.playerUsername} is the only other player here, {Dist(Copied):0} m " +
                                           "away, and they figured out you're a Masked"
            : Solo ? $"the real {Copied.playerUsername} is the only other player here, {Dist(Copied):0} m away. They're the friend you're " +
                     "playing with this round: stay with them, talk to them and answer them like a friend would"
            : $"the real {Copied.playerUsername} is alive, {Dist(Copied):0} m away from you";

        var players = new JArray();
        foreach (var p in AlivePlayers().OrderBy(Dist))
        {
            if (Dist(p) > 60f) continue;
            if (p == Copied && !Solo && !Renamed) continue;
            bool aloneWithMe = AlivePlayers().All(o => o == p || o == Copied || Vector3.Distance(o.transform.position, p.transform.position) > 20f);
            players.Add(new JObject
            {
                ["name"] = p.playerUsername,
                ["distance_m"] = Mathf.RoundToInt(Dist(p)),
                ["location"] = GameContext.Where(p),
                ["looking_at_you"] = SeesMe(p, 40f, 20f),
                ["holding"] = p.currentlyHeldObjectServer != null ? p.currentlyHeldObjectServer.itemProperties.itemName : "nothing",
                ["health"] = p.health,
                ["sprinting"] = p.isSprinting,
                ["suspicion_of_you"] = Mathf.RoundToInt(Suspicion(p) * 100f),
                ["alone_with_you"] = aloneWithMe && Dist(p) < 25f,
            });
            // Another Masked is already with them: pick someone else to follow, so the crew doesn't get a conga line.
            if (OthersWith(p) > 0) ((JObject)players.Last!)["another_masked_is_with_them"] = true;
        }
        var (talkAllowed, talkWhy) = TalkPolicy();
        var evts = new JArray(events.Select(e => $"{Mathf.RoundToInt(Time.time - e.time)}s ago: {e.text}"));
        var allowed = new JArray("blend_in", "follow", "stare", "wander", "lure", "flee", "emote");
        if (canAttack) allowed.Add("attack");
        float hour = TimeOfDay.Instance != null ? 6f + TimeOfDay.Instance.normalizedTimeOfDay * 18f : 12f;
        var request = new JObject
        {
            ["key"] = HostServices.PlayerKey(Copied),
            ["name"] = Copied.playerUsername,
            ["as_name"] = Renamed ? Name : "",
            ["you"] = new JObject
            {
                ["copying"] = Renamed ? $"nobody: you're a player called {Name}" : Copied.playerUsername,
                ["location"] = location,
                ["holding"] = "nothing", // AddItemContext fills in the items
                ["inventory"] = "",
                ["real_player"] = real,
                ["revived"] = Disguise.Revived,
                ["health"] = Masked.enemyHP,
                ["cover"] = CoverBlown ? "blown: someone saw you attack, they know what you are"
                    : AliveFoundOut().Any() ? $"blown: {string.Join(", ", AliveFoundOut().Select(p => p.playerUsername))} figured out you're a Masked"
                    : "intact",
                ["out_of_breath"] = exhausted,
                ["seconds_with_group"] = Mathf.RoundToInt(SecondsWithGroup),
            },
            ["situation"] = $"you are {location}; {GameContext.Clock(hour)}" +
                            (StartOfRound.Instance.shipIsLeaving ? "; the ship is leaving right now!" : ""),
            ["round"] = GameContext.Round(),
            ["players"] = players,
            ["monsters"] = MonstersJson(),
            ["events"] = evts,
            ["can_attack"] = canAttack,
            ["attack_reason"] = attackReason,
            ["current"] = new JObject
            {
                ["action"] = CurrentAction(),
                ["target"] = Target != null ? Target.playerUsername : "",
                ["for_seconds"] = Mathf.RoundToInt(Time.time - behaviourStarted),
                ["heading_to"] = Current is Behaviour.Wander or Behaviour.BlendIn ? heading : "",
            },
            ["talk"] = new JObject
            {
                ["allowed"] = talkAllowed,
                ["why"] = talkWhy,
                ["reply_to"] = talkAllowed && replyTo != null ? $"{replyTo.playerUsername}: {replyText}" : "",
            },
            ["said_recently"] = new JArray(saidRecently),
            ["allowed"] = allowed,
            ["emotes"] = new JArray(BetterEmotesCompat.Available().Concat(TooManyEmotesCompat.Available(Copied))),
            ["log_prompt"] = Cfg.LogPrompts.Value,
            ["max_tokens"] = 120, // room for the item field next to a line
        };
        AddItemContext((JObject)request["you"]!, request, allowed, out view);
        string doing = Doing();
        if (doing.Length > 0)
        {
            var current = (JObject)request["current"]!;
            current["doing"] = doing;
            string item = TaskItemRef(view);
            if (item.Length > 0) current["item"] = item;
        }
        return request;
    }

    internal static string ActionName(Behaviour b) => b switch
    {
        Behaviour.BlendIn => "blend_in",
        Behaviour.Follow => "follow",
        Behaviour.Stare => "stare",
        Behaviour.Wander => "wander",
        Behaviour.Lure => "lure",
        Behaviour.Attack => "attack",
        Behaviour.Flee => "flee",
        Behaviour.PickUp => "pick_up",
        Behaviour.Drop => "drop",
        Behaviour.Entrance => "go_through_door",
        Behaviour.GoToShip => "go_to_ship",
        _ => "emote",
    };

    private static Behaviour? ParseAction(string a) => a switch
    {
        "blend_in" => Behaviour.BlendIn,
        "follow" => Behaviour.Follow,
        "stare" => Behaviour.Stare,
        "wander" => Behaviour.Wander,
        "lure" => Behaviour.Lure,
        "attack" => Behaviour.Attack,
        "flee" => Behaviour.Flee,
        "emote" => Behaviour.Emote,
        _ => null,
    };

    private PlayerControllerB? FindPlayer(string name) =>
        string.IsNullOrEmpty(name) ? null :
        AlivePlayers().FirstOrDefault(p => string.Equals(p.playerUsername, name, StringComparison.OrdinalIgnoreCase));

    // The crewmate to stick with: the closest, but one that other mimics are already with counts as 12 m further, so
    // several of them spread over the crew instead of all trailing one person.
    private PlayerControllerB? Nearest()
    {
        var here = AlivePlayers().Where(p => IsAudience(p) && SameSide(p)).ToList();
        var pool = here.Count > 0 ? here : AlivePlayers().Where(IsAudience).ToList();
        return pool.OrderBy(p => Dist(p) + 12f * OthersWith(p)).FirstOrDefault();
    }

    private int OthersWith(PlayerControllerB p) =>
        All.Count(b => b != this && b != null && b.Masked != null && !b.Masked.isEnemyDead && b.Anchor == p);

    // Who it's with right now (BlendIn's crewmate or who it follows), for other mimics to spread out.
    internal PlayerControllerB? Anchor;
    private float anchorUntil;

    // BlendIn keeps its crewmate for a while instead of hopping between people as everyone moves.
    private PlayerControllerB? StickWith()
    {
        if (Anchor != null && !Anchor.isPlayerDead && Anchor.isPlayerControlled && SameSide(Anchor) && Time.time < anchorUntil) return Anchor;
        Anchor = Nearest();
        anchorUntil = Time.time + UnityEngine.Random.Range(10f, 20f);
        return Anchor;
    }

    // answering: someone had just talked to it when it asked, so the answer may be "stop that".
    private void Apply(string action, string targetName, string itemRef, string say, string emoteName, string sign, string reason,
        Dictionary<string, GrabbableObject> view, bool answering)
    {
        action = action.Trim().ToLowerInvariant();
        LastReason = $"{action} {targetName}{(itemRef.Length > 0 ? $" [{itemRef}]" : "")} ({reason})";
        var target = FindPlayer(targetName);
        var forced = ParseAction(Cfg.ForceBehaviour.Value.Trim().ToLowerInvariant());

        // Items and doors (MimicItems.cs).
        if (!Fleeing && forced == null && !HuntingKnower && StartTask(action, itemRef, target, view))
        {
            if (!string.IsNullOrWhiteSpace(say)) TrySay(say);
            PushOverlay();
            return;
        }

        var chosen = ParseAction(action);
        Behaviour b = forced ?? chosen ?? Behaviour.BlendIn;
        if (b == Behaviour.Attack && !CanAttack(out _)) b = Behaviour.Follow;
        if (Fleeing)
        {
            b = Behaviour.Flee; // a reflex (monster, sign) beats the LLM
            target = fleeFrom;
        }
        else if (HuntingKnower && forced == null)
        {
            b = Behaviour.Attack; // going after someone who knows what it is
            target = Target;
        }
        if (target == null && b is Behaviour.Follow or Behaviour.Stare or Behaviour.Lure or Behaviour.Attack)
        {
            target = Nearest();
        }

        int emoteNumber = BetterEmotesCompat.EmoteNumber(emoteName);
        if (emoteNumber == 0) emoteNumber = TooManyEmotesCompat.EmoteNumber(emoteName);
        if (b == Behaviour.Emote && emoteNumber == 0) emoteNumber = BetterEmotesCompat.EmoteNumber("point");

        // Halfway through picking something up or going through a door, a routine decision doesn't cut it short: it
        // says what to do after. An answer to someone (they may have said stop), attacking and fleeing do. Emotes wait.
        if (IsTask(Current) && !taskDone && b is not Behaviour.Attack and not Behaviour.Flee &&
            (!answering || emoteNumber > 0 || b == Behaviour.Emote))
        {
            if (chosen.HasValue && b != Behaviour.Emote)
            {
                resumeBehaviour = b;
                resumeTarget = target;
            }
            if (!string.IsNullOrWhiteSpace(say)) TrySay(say);
            PushOverlay();
            return;
        }

        if (emoteNumber > 0 && b is not Behaviour.Attack and not Behaviour.Flee)
        {
            afterEmote = b == Behaviour.Emote ? Current : b;
            emote = emoteNumber;
            signText = emoteNumber == BetterEmotesCompat.Sign ? (sign.Length > 0 ? sign : "hi") : "";
            SetBehaviour(Behaviour.Emote, target ?? Target, emoteNumber == BetterEmotesCompat.Sign ? 6f : 4.5f);
        }
        else if (b != Current || target != Target)
        {
            SetBehaviour(b == Behaviour.Emote ? Behaviour.BlendIn : b, target, DefaultDuration(b));
        }
        if (!string.IsNullOrWhiteSpace(say)) TrySay(say);
        PushOverlay();
    }

    private static float DefaultDuration(Behaviour b) => b switch
    {
        Behaviour.Stare => UnityEngine.Random.Range(3f, 7f),
        Behaviour.Flee => UnityEngine.Random.Range(6f, 10f),
        Behaviour.Wander => UnityEngine.Random.Range(20f, 40f),
        Behaviour.Lure => 30f,
        Behaviour.Follow => 25f,
        // Time limits for the tasks, in case the way is blocked.
        Behaviour.PickUp => 25f,
        Behaviour.Drop => 20f,
        Behaviour.Entrance => 60f,
        Behaviour.GoToShip => 150f,
        _ => 0f,
    };

    // Used when the LLM is off, offline or slow.
    private void Scripted()
    {
        var nearest = Nearest();
        if (Fleeing || IsTask(Current)) return; // a failed request doesn't stop what it's doing
        bool canAttack = CanAttack(out string why);
        if (canAttack && nearest != null && Dist(nearest) < 20f &&
            AlivePlayers().All(o => o == nearest || o == Copied || Vector3.Distance(o.transform.position, nearest.transform.position) > 20f) &&
            UnityEngine.Random.value < 0.35f)
        {
            LastReason = $"scripted: attack ({why})";
            SetBehaviour(Behaviour.Attack, nearest, 0f);
            return;
        }
        float r = UnityEngine.Random.value;
        var b = r < 0.45f ? Behaviour.BlendIn : r < 0.85f ? Behaviour.Follow : r < 0.95f ? Behaviour.Stare : Behaviour.Wander;
        if (ParseAction(Cfg.ForceBehaviour.Value.Trim().ToLowerInvariant()) is Behaviour forced) b = forced;
        if (b == Behaviour.Attack && !canAttack) b = Behaviour.Follow;
        LastReason = "scripted: " + ActionName(b);
        if (b != Current) SetBehaviour(b, nearest, DefaultDuration(b));
        PushOverlay();
    }

    internal void SetBehaviour(Behaviour b, PlayerControllerB? target, float duration)
    {
        Behaviour previous = Current;
        ClearTask();
        // Going in first and waiting there ends when it's told to go somewhere else.
        if (b is Behaviour.Attack or Behaviour.Flee or Behaviour.Lure or Behaviour.Entrance or Behaviour.GoToShip) sideLockUntil = 0f;
        Current = b;
        Target = target;
        behaviourStarted = Time.time;
        behaviourUntil = duration > 0f ? Time.time + duration : float.MaxValue;
        if (b != Behaviour.Emote) emote = 0;
        ResetMovementPlan();
        if (b == Behaviour.Lure && target != null) lurePoint = PickLurePoint(target);
        if (b == Behaviour.Attack && previous != Behaviour.Attack) StartAttackPhase(); // the cover goes at the lunge (MimicAggression)
        if (b is Behaviour.Stare or Behaviour.Emote && target != null)
        {
            LookAtPlayer(target, duration > 0f ? duration : 5f);
        }
        else if (previous is Behaviour.Stare or Behaviour.Emote || b is Behaviour.Flee or Behaviour.Attack)
        {
            ClearLook();
        }
    }

    private void TrySay(string text)
    {
        text = text.Trim();
        var (allowed, _) = TalkPolicy();
        if (!allowed || text.Length == 0 || IsRepeat(text)) return;
        var host = HostServices.Instance;
        if (host == null || !host.HasConsent(Copied)) return; // no consent, no cloned voice
        lastLine = Time.time;
        lastSay = text;
        saidRecently.Add(text);
        if (saidRecently.Count > 8) saidRecently.RemoveAt(0);
        // Players look at who they're talking to.
        if (replyTo != null && SameSide(replyTo) && Dist(replyTo) < 20f && !Disguise.Moving) LookAtPlayer(replyTo, 2.5f);
        replyTo = null;
        mustTalkWhy = "";
        nextSmallTalk = Time.time + SmallTalkDelay();
        string started = earlyStream.Length > 0 && text == earlyText ? earlyStream : "";
        if (started.Length > 0) earlyStream = "";
        host.Speak(this, text, started);
    }

    // Answers may sound like earlier lines ("yeah I got it" after "yeah I'm good"); only near-copies count there.
    private bool IsRepeat(string text) =>
        saidRecently.Any(s => Similarity(s, text) >= (replyTo != null ? 0.85f : 0.6f));

    // Share of words two lines have in common (0-1).
    private static float Similarity(string a, string b)
    {
        static HashSet<string> Words(string s) =>
            new(s.ToLowerInvariant().Split(new[] { ' ', ',', '.', '!', '?', '\'' }, StringSplitOptions.RemoveEmptyEntries));
        var wa = Words(a);
        var wb = Words(b);
        if (wa.Count == 0 || wb.Count == 0) return 0f;
        return wa.Intersect(wb).Count() / (float)Math.Min(wa.Count, wb.Count);
    }

    // ---- sync to clients

    private void SyncVisuals()
    {
        var s = new MimicVisualState
        {
            Hostile = Current == Behaviour.Attack,
            Sprinting = isSprinting,
            Crouching = crouching,
            Emote = Current == Behaviour.Emote ? emote : 0,
            SignText = Current == Behaviour.Emote ? signText : "",
        };
        if (!stateSent || !s.SameAs(sentState))
        {
            stateSent = true;
            sentState = s;
            ModNetwork.Instance?.SyncState(Masked, s);
        }
        if (Cfg.ShowBrainOverlay.Value && Time.time - lastOverlay > 1f) PushOverlay();
    }

    private string AttackLine() { string a = AttackOverlay(); return a.Length > 0 ? a + "\n" : ""; }

    private void PushOverlay()
    {
        if (!Cfg.ShowBrainOverlay.Value) return;
        lastOverlay = Time.time;
        CanAttack(out string why);
        var (talk, talkWhy) = TalkPolicy();
        var sus = string.Join(" ", AlivePlayers().Where(p => Suspicion(p) > 0.01f).Select(p => $"{p.playerUsername}:{Suspicion(p) * 100f:0}"));
        Disguise.DebugText =
            $"{(Renamed ? $"{Name} ({Copied.playerUsername})" : Copied.playerUsername)} | {CurrentAction()} {Target?.playerUsername}{(heading.Length > 0 ? " -> " + heading : "")}\n" +
            $"with group {SecondsWithGroup:0}s | {why}\nstamina {stamina * 100f:0}%{(exhausted ? " (out of breath)" : "")} | " +
            $"talk: {(talk ? "yes" : "no")} ({talkWhy}) | sus {sus}\n{HabitOverlay()}{AttackLine()}{ItemsOverlay()}{LastReason}\n{(lastSay.Length > 0 ? "\"" + lastSay + "\"" : "")}";
    }
}
