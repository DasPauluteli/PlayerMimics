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
}

// Host only. Decides what one Masked does and drives it. The LLM picks behaviours; the code enforces the rules.
internal sealed class MimicBrain : MonoBehaviour
{
    internal static readonly List<MimicBrain> All = new();
    private static int pendingRequests;

    internal MaskedPlayerEnemy Masked = null!;
    internal MimicDisguise Disguise = null!;
    internal PlayerControllerB Copied => Disguise.Player;

    internal Behaviour Current = Behaviour.BlendIn;
    internal PlayerControllerB? Target;
    private float behaviourStarted;
    private float behaviourUntil;
    private Behaviour afterEmote = Behaviour.BlendIn;
    private int emote;
    private string signText = "";

    private readonly Dictionary<int, float> suspicion = new();
    private readonly List<(float time, string text)> events = new();
    private readonly Dictionary<string, float> eventCooldowns = new();
    private readonly List<string> saidRecently = new();
    internal float SecondsWithGroup;
    private float threatenedUntil;
    private float forcedFleeUntil;
    private PlayerControllerB? fleeFrom;

    private bool requestPending;
    private float nextDecision;
    private bool urgent;
    private float lastLine = -100f;
    internal string LastReason = "";
    private string lastSay = "";

    private bool sprinting;
    private bool crouching;
    private Vector3 wanderPoint;
    private float nextWanderPick;
    private Vector3 lurePoint;
    private float nextLook;
    private MimicVisualState sentState;
    private bool stateSent;
    private float lastOverlay;

    internal void Init(MaskedPlayerEnemy masked, MimicDisguise disguise)
    {
        Masked = masked;
        Disguise = disguise;
        All.Add(this);
        behaviourStarted = Time.time;
        nextDecision = Time.time + 2f;
        if (disguise.Revived) AddEvent("You just got up where the real " + Copied.playerUsername + " died. Nobody saw it.");
    }

    private void OnDestroy() => All.Remove(this);

    // ---- helpers

    private static IEnumerable<PlayerControllerB> AlivePlayers() =>
        StartOfRound.Instance.allPlayerScripts.Where(p => p != null && p.isPlayerControlled && !p.isPlayerDead);

    private Vector3 Head => Masked.eye != null ? Masked.eye.position : transform.position + Vector3.up * 2f;

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

    // Listeners that matter: people other than the copied player, unless solo testing.
    private bool IsAudience(PlayerControllerB p) => p != Copied || Cfg.SoloMode.Value;

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
        if (decideNow) urgent = true;
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
        if (Disguise.Revealed || Disguise.State.Hostile) { reason = "already attacking"; return true; }
        if (Time.time < threatenedUntil) { reason = "you were attacked"; return true; }
        if (MaxSuspicion() >= Cfg.CoverBlownSuspicion.Value) { reason = "your cover is about to blow"; return true; }
        if (SecondsWithGroup >= Cfg.MinSecondsBeforeAttack.Value) { reason = "you've spent enough time with them"; return true; }
        reason = $"too early: blend in for {Mathf.CeilToInt(Cfg.MinSecondsBeforeAttack.Value - SecondsWithGroup)} more seconds";
        return false;
    }

    // ---- events from patches and host services

    internal void OnHit(PlayerControllerB? by)
    {
        if (by == null) return;
        string item = by.currentlyHeldObjectServer != null ? by.currentlyHeldObjectServer.itemProperties.itemName : "their fists";
        AddEvent($"{by.playerUsername} hit you with {item}!", decideNow: true);
        AddSuspicion(by, 0.6f);
        threatenedUntil = Time.time + 40f;
        // Hit with a sign (stop sign, yield sign) while still disguised: run.
        if (item.IndexOf("sign", StringComparison.OrdinalIgnoreCase) >= 0 && !Disguise.Revealed)
        {
            forcedFleeUntil = Time.time + 8f;
            fleeFrom = by;
            SetBehaviour(Behaviour.Flee, by, 8f);
        }
        else if (Disguise.Revealed && !Cfg.NoAttack.Value)
        {
            SetBehaviour(Behaviour.Attack, by, 20f); // switch to whoever angered it
        }
    }

    private static readonly string[] SuspiciousWords =
    {
        "mimic", "masked", "fake", "impostor", "imposter", "monster", "not real", "are you real", "prove", "is that you",
        "not you", "who are you", "that's not", "thats not", "sus",
    };

    internal void OnSpeech(PlayerControllerB speaker, string text)
    {
        if (speaker == Copied && !Cfg.SoloMode.Value)
        {
            if (Dist(speaker) < 30f && AlivePlayers().Any(p => p != speaker && Dist(p) < 25f))
            {
                AddEvent($"The real {Copied.playerUsername} is talking nearby: \"{text}\". People could notice two of you.",
                    "realtalk", 15f);
            }
            return;
        }
        if (!SameSide(speaker) || Dist(speaker) > 25f) return;
        string lower = text.ToLowerInvariant();
        bool suspicious = SuspiciousWords.Any(lower.Contains);
        bool named = NameTokens(Copied.playerUsername).Any(lower.Contains);
        if (suspicious) AddSuspicion(speaker, named || Dist(speaker) < 10f ? 0.3f : 0.15f);
        AddEvent($"{speaker.playerUsername} said: \"{text}\"", decideNow: Dist(speaker) < 15f || named);
    }

    private static IEnumerable<string> NameTokens(string name) =>
        name.ToLowerInvariant().Split(new[] { ' ', '_', '-', '.' }, StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length >= 3);

    internal void OnPlayerDied(PlayerControllerB p)
    {
        if (Dist(p) < 40f) AddEvent($"{p.playerUsername} just died nearby.", decideNow: true);
    }

    // ---- the AI tick (DoAIInterval, about 5 times a second)

    internal void Tick()
    {
        if (Masked.isEnemyDead) return;
        float dt = Masked.AIIntervalTime;
        Perceive(dt);

        if (Time.time >= nextDecision || (urgent && Time.time >= nextDecision - Cfg.DecisionInterval.Value + 1.5f))
        {
            Decide();
        }
        if (Time.time >= behaviourUntil && Current != Behaviour.BlendIn && Current != Behaviour.Attack)
        {
            SetBehaviour(Current == Behaviour.Emote ? afterEmote : Behaviour.BlendIn, Target, 0f);
        }
        Run();
        SyncVisuals();
    }

    private void Perceive(float dt)
    {
        var alive = AlivePlayers().ToList();
        if (alive.Any(p => IsAudience(p) && SameSide(p) && Dist(p) < 25f)) SecondsWithGroup += dt;

        foreach (var key in suspicion.Keys.ToList()) suspicion[key] = Mathf.Max(0f, suspicion[key] - 0.015f * dt);

        foreach (var p in alive)
        {
            if (!SameSide(p) || p == Copied && !Cfg.SoloMode.Value) continue;
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
            }
            // Keeping their distance while watching you closely is suspicious.
            if (d > 6f && d < 14f && SeesMe(p, 14f, 10f) && Current != Behaviour.Flee) AddSuspicion(p, 0.01f * dt);
        }

        // Being seen next to the real player is the worst giveaway.
        if (!Copied.isPlayerDead && Copied.isPlayerControlled && SameSide(Copied) && Dist(Copied) < 30f && !Cfg.SoloMode.Value)
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
    }

    private static string SignText(PlayerControllerB p)
    {
        var t = p.transform.Find("ScavengerModel/metarig/Sign/Text");
        var tmp = t != null ? t.GetComponent<TextMeshPro>() : null;
        return tmp != null ? tmp.text.Trim() : "";
    }

    // ---- deciding

    private void Decide()
    {
        urgent = false;
        nextDecision = Time.time + Cfg.DecisionInterval.Value * UnityEngine.Random.Range(0.8f, 1.2f);
        if (Current == Behaviour.Emote && Time.time < behaviourUntil) return;
        var host = HostServices.Instance;
        if (Cfg.UseLlm.Value && host != null && host.AiOnline)
        {
            if (requestPending || pendingRequests >= 3) return;
            requestPending = true;
            pendingRequests++;
            var body = BuildContext();
            host.Post("/decide", body, 30, result =>
            {
                requestPending = false;
                pendingRequests--;
                if (this == null || Masked == null || Masked.isEnemyDead) return;
                if (result == null)
                {
                    Scripted();
                    return;
                }
                if (Cfg.LogPrompts.Value)
                {
                    Plugin.Log.LogInfo($"[decide] prompt:\n{result.Value<string>("prompt")}\n[decide] answer: {result.Value<string>("raw")}");
                }
                Apply(result.Value<string>("action") ?? "", result.Value<string>("target") ?? "", result.Value<string>("say") ?? "",
                    result.Value<string>("emote") ?? "", result.Value<string>("sign_text") ?? "", result.Value<string>("reason") ?? "");
            });
        }
        else
        {
            Scripted();
        }
    }

    private JObject BuildContext()
    {
        bool canAttack = CanAttack(out string attackReason);
        string location = GameContext.Where(transform.position, !Masked.isOutside);
        string real = Copied.isPlayerDead || !Copied.isPlayerControlled
            ? $"the real {Copied.playerUsername} is dead (only you know); never mention dying or coming back"
            : !SameSide(Copied) ? $"the real {Copied.playerUsername} is alive, {GameContext.Where(Copied)}"
            : $"the real {Copied.playerUsername} is alive, {Dist(Copied):0} m away from you";

        var players = new JArray();
        foreach (var p in AlivePlayers().OrderBy(Dist))
        {
            if (!SameSide(p) && Dist(p) > 60f) continue;
            if (Dist(p) > 60f) continue;
            if (p == Copied && !Cfg.SoloMode.Value) continue;
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
        }
        var evts = new JArray(events.Select(e => $"{Mathf.RoundToInt(Time.time - e.time)}s ago: {e.text}"));
        var allowed = new JArray("blend_in", "follow", "stare", "wander", "lure", "flee", "emote");
        if (canAttack) allowed.Add("attack");
        float hour = TimeOfDay.Instance != null ? 6f + TimeOfDay.Instance.normalizedTimeOfDay * 18f : 12f;
        return new JObject
        {
            ["key"] = HostServices.PlayerKey(Copied),
            ["name"] = Copied.playerUsername,
            ["you"] = new JObject
            {
                ["copying"] = Copied.playerUsername,
                ["location"] = location,
                ["holding"] = "nothing (you can't carry, show or hand over items)",
                ["real_player"] = real,
                ["revived"] = Disguise.Revived,
                ["health"] = Masked.enemyHP,
                ["disguise"] = Disguise.Revealed ? "blown, you're visibly a monster now" : "intact",
                ["seconds_with_group"] = Mathf.RoundToInt(SecondsWithGroup),
            },
            ["situation"] = $"you are {location}; {GameContext.Clock(hour)}" +
                            (StartOfRound.Instance.shipIsLeaving ? "; the ship is leaving right now!" : ""),
            ["round"] = GameContext.Round(),
            ["players"] = players,
            ["events"] = evts,
            ["can_attack"] = canAttack,
            ["attack_reason"] = attackReason,
            ["current"] = new JObject
            {
                ["action"] = ActionName(Current),
                ["target"] = Target != null ? Target.playerUsername : "",
                ["for_seconds"] = Mathf.RoundToInt(Time.time - behaviourStarted),
            },
            ["said_recently"] = new JArray(saidRecently),
            ["allowed"] = allowed,
            ["emotes"] = new JArray(BetterEmotesCompat.Available()),
            ["log_prompt"] = Cfg.LogPrompts.Value,
        };
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

    private PlayerControllerB? Nearest() =>
        AlivePlayers().Where(p => IsAudience(p) && SameSide(p)).OrderBy(Dist).FirstOrDefault()
        ?? AlivePlayers().Where(IsAudience).OrderBy(Dist).FirstOrDefault();

    private void Apply(string action, string targetName, string say, string emoteName, string sign, string reason)
    {
        LastReason = $"{action} {targetName} ({reason})";
        Behaviour b = ParseAction(action) ?? Behaviour.BlendIn;
        var target = FindPlayer(targetName);

        if (ParseAction(Cfg.ForceBehaviour.Value.Trim().ToLowerInvariant()) is Behaviour forced) b = forced;
        if (b == Behaviour.Attack && !CanAttack(out _)) b = Behaviour.Follow;
        if (Time.time < forcedFleeUntil)
        {
            b = Behaviour.Flee;
            target = fleeFrom;
        }
        if (target == null && b is Behaviour.Follow or Behaviour.Stare or Behaviour.Lure or Behaviour.Attack or Behaviour.Flee)
        {
            target = Nearest();
        }

        int emoteNumber = BetterEmotesCompat.EmoteNumber(emoteName);
        if (b == Behaviour.Emote && emoteNumber == 0) emoteNumber = BetterEmotesCompat.EmoteNumber("point");
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
        Behaviour.Wander => UnityEngine.Random.Range(15f, 30f),
        Behaviour.Lure => 30f,
        Behaviour.Follow => 25f,
        _ => 0f,
    };

    // Used when the LLM is off, offline or slow.
    private void Scripted()
    {
        var nearest = Nearest();
        if (Time.time < forcedFleeUntil) return;
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
        var b = r < 0.45f ? Behaviour.BlendIn : r < 0.8f ? Behaviour.Follow : r < 0.92f ? Behaviour.Stare : Behaviour.Wander;
        if (ParseAction(Cfg.ForceBehaviour.Value.Trim().ToLowerInvariant()) is Behaviour forced) b = forced;
        if (b == Behaviour.Attack && !canAttack) b = Behaviour.Follow;
        LastReason = "scripted: " + ActionName(b);
        if (b != Current) SetBehaviour(b, nearest, DefaultDuration(b));
        PushOverlay();
    }

    internal void SetBehaviour(Behaviour b, PlayerControllerB? target, float duration)
    {
        Behaviour previous = Current;
        Current = b;
        Target = target;
        behaviourStarted = Time.time;
        behaviourUntil = duration > 0f ? Time.time + duration : float.MaxValue;
        if (b != Behaviour.Emote) emote = 0;
        nextWanderPick = 0f;
        if (b == Behaviour.Lure && target != null) lurePoint = PickLurePoint(target);
        if (b == Behaviour.Attack && !Disguise.State.Hostile)
        {
            Plugin.Log.LogInfo($"{Copied.playerUsername}'s Masked attacks {target?.playerUsername}");
        }
        if ((b is Behaviour.Stare or Behaviour.Follow or Behaviour.Lure) && target != null)
        {
            Masked.LookAtPlayerServerRpc((int)target.playerClientId);
        }
        else if (previous is Behaviour.Stare or Behaviour.Follow or Behaviour.Lure || b is Behaviour.Flee or Behaviour.Wander or Behaviour.Attack)
        {
            if (Masked.stareAtTransform != null) Masked.StopLookingAtTransformServerRpc();
        }
    }

    private void TrySay(string text)
    {
        if (Time.time - lastLine < Cfg.MinSecondsBetweenLines.Value || text == lastSay) return;
        var host = HostServices.Instance;
        if (host == null || !host.HasConsent(Copied)) return; // no consent, no cloned voice
        if (!AlivePlayers().Any(p => IsAudience(p) && SameSide(p) && Dist(p) < 40f)) return;
        lastLine = Time.time;
        lastSay = text;
        saidRecently.Add(text);
        if (saidRecently.Count > 4) saidRecently.RemoveAt(0);
        host.Speak(this, text);
    }

    // ---- behaviours

    private float DesiredSpeed()
    {
        if (Current is Behaviour.Stare or Behaviour.Emote) return 0f;
        float walk = Cfg.WalkSpeed.Value;
        if (crouching) return walk * 0.45f;
        return sprinting ? Cfg.SprintSpeed.Value : walk;
    }

    internal void ApplySpeed()
    {
        if (Masked.agent != null && !Masked.isEnemyDead && Masked.stunNormalizedTimer < 0f && !Masked.inSpecialAnimation)
        {
            Masked.agent.speed = DesiredSpeed();
        }
    }

    private void Run()
    {
        sprinting = false;
        crouching = false;
        if (Target != null && (Target.isPlayerDead || !Target.isPlayerControlled))
        {
            Target = null;
            if (Current is Behaviour.Attack or Behaviour.Follow or Behaviour.Stare or Behaviour.Lure)
            {
                SetBehaviour(Behaviour.BlendIn, null, 0f);
            }
        }
        switch (Current)
        {
            case Behaviour.BlendIn: BlendIn(); break;
            case Behaviour.Follow: Follow(); break;
            case Behaviour.Stare: Stop(); break;
            case Behaviour.Wander: Wander(); break;
            case Behaviour.Lure: Lure(); break;
            case Behaviour.Attack: Attack(); break;
            case Behaviour.Flee: Flee(); break;
            case Behaviour.Emote: Stop(); break;
        }
    }

    private void Stop() => Masked.SetDestinationToPosition(transform.position);

    // Walks to a point, using the main entrance when the point is on the other side of it.
    private void GoTo(Vector3 point, bool inside)
    {
        bool imInside = !Masked.isOutside;
        if (inside != imInside)
        {
            Vector3 door = Masked.mainEntrancePosition;
            if (Vector3.Distance(transform.position, door) < 1.5f && Time.realtimeSinceStartup - Masked.timeAtLastUsingEntrance > 3f)
            {
                // Same as vanilla: going through flips inside/outside.
                Masked.TeleportMaskedEnemyAndSync(RoundManager.FindMainEntrancePosition(getTeleportPosition: true, getOutsideEntrance: !Masked.isOutside), !Masked.isOutside);
                return;
            }
            Masked.SetDestinationToPosition(door);
            return;
        }
        Masked.SetDestinationToPosition(point);
    }

    private void GoToPlayer(PlayerControllerB p, float keepDistance)
    {
        Vector3 to = p.transform.position;
        if (SameSide(p) && Dist(p) <= keepDistance)
        {
            Stop();
            return;
        }
        Vector3 offset = (transform.position - to);
        offset.y = 0f;
        Vector3 point = offset.sqrMagnitude > 0.01f ? to + offset.normalized * (keepDistance * 0.7f) : to;
        GoTo(RoundManager.Instance.GetNavMeshPosition(point, default, 3f), p.isInsideFactory);
    }

    private void BlendIn()
    {
        var crew = AlivePlayers().Where(IsAudience).ToList();
        if (crew.Count == 0)
        {
            Wander();
            return;
        }
        var anchor = crew.OrderBy(Dist).First();
        float d = Dist(anchor);
        sprinting = anchor.isSprinting || d > 25f || !SameSide(anchor);
        crouching = anchor.isCrouching && d < 10f;
        if (!SameSide(anchor) || d > 9f)
        {
            GoToPlayer(anchor, 5f);
            return;
        }
        // Mill around near the group, like someone looking for loot.
        if (Time.time >= nextWanderPick)
        {
            nextWanderPick = Time.time + UnityEngine.Random.Range(4f, 9f);
            Vector3 around = anchor.transform.position + UnityEngine.Random.insideUnitSphere * 6f;
            wanderPoint = RoundManager.Instance.GetNavMeshPosition(around, default, 4f);
        }
        if (Vector3.Distance(transform.position, wanderPoint) > 1f) Masked.SetDestinationToPosition(wanderPoint);
        if (Time.time >= nextLook)
        {
            nextLook = Time.time + UnityEngine.Random.Range(3f, 7f);
            if (UnityEngine.Random.value < 0.4f) Masked.LookAtPositionServerRpc(anchor.gameplayCamera.transform.position, UnityEngine.Random.Range(1f, 2.5f));
        }
    }

    private void Follow()
    {
        if (Target == null) { BlendIn(); return; }
        float d = Dist(Target);
        sprinting = Target.isSprinting || d > 14f || !SameSide(Target);
        crouching = Target.isCrouching && d < 8f;
        GoToPlayer(Target, 3.5f);
    }

    private void Wander()
    {
        if (Time.time >= nextWanderPick || Vector3.Distance(transform.position, wanderPoint) < 2f)
        {
            nextWanderPick = Time.time + UnityEngine.Random.Range(8f, 15f);
            var nodes = Masked.allAINodes;
            if (nodes != null && nodes.Length > 0)
            {
                var near = nodes.Where(n => n != null && Vector3.Distance(n.transform.position, transform.position) < 35f).ToList();
                var pick = near.Count > 0 ? near[UnityEngine.Random.Range(0, near.Count)] : nodes[UnityEngine.Random.Range(0, nodes.Length)];
                wanderPoint = pick.transform.position;
            }
        }
        Masked.SetDestinationToPosition(wanderPoint);
    }

    private Vector3 PickLurePoint(PlayerControllerB target)
    {
        var nodes = Masked.allAINodes;
        if (nodes == null || nodes.Length == 0) return transform.position;
        var others = AlivePlayers().Where(p => p != target).ToList();
        var good = nodes.Where(n => n != null)
            .Select(n => n.transform.position)
            .Where(pos => Vector3.Distance(pos, target.transform.position) is > 8f and < 22f)
            .Where(pos => others.All(o => Vector3.Distance(o.transform.position, pos) > 14f))
            .ToList();
        return good.Count > 0 ? good[UnityEngine.Random.Range(0, good.Count)] : transform.position;
    }

    private void Lure()
    {
        if (Target == null) { BlendIn(); return; }
        if (Vector3.Distance(transform.position, lurePoint) > 1.5f)
        {
            Masked.SetDestinationToPosition(lurePoint);
            return;
        }
        Stop();
        // Got them alone and close: the moment the rules allow it, strike.
        if (Dist(Target) < 4f && CanAttack(out _) && AlivePlayers().All(o => o == Target || o == Copied || Vector3.Distance(o.transform.position, transform.position) > 18f))
        {
            SetBehaviour(Behaviour.Attack, Target, 0f);
        }
    }

    private void Attack()
    {
        if (Target == null || !CanAttack(out _))
        {
            SetBehaviour(Behaviour.BlendIn, null, 0f);
            return;
        }
        sprinting = true;
        GoTo(Target.transform.position, Target.isInsideFactory);
    }

    private void Flee()
    {
        var from = Target ?? fleeFrom;
        sprinting = true;
        if (from == null) { Wander(); return; }
        if (Time.time >= nextWanderPick)
        {
            nextWanderPick = Time.time + 3f;
            var node = Masked.ChooseFarthestNodeFromPosition(from.transform.position, avoidLineOfSight: true);
            if (node != null) wanderPoint = node.position;
        }
        Masked.SetDestinationToPosition(wanderPoint);
    }

    // ---- sync to clients

    private void SyncVisuals()
    {
        var s = new MimicVisualState
        {
            Hostile = Current == Behaviour.Attack || Disguise.Revealed,
            Sprinting = sprinting,
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

    private void PushOverlay()
    {
        if (!Cfg.ShowBrainOverlay.Value) return;
        lastOverlay = Time.time;
        CanAttack(out string why);
        var sus = string.Join(" ", AlivePlayers().Where(p => Suspicion(p) > 0.01f).Select(p => $"{p.playerUsername}:{Suspicion(p) * 100f:0}"));
        Disguise.DebugText =
            $"{Copied.playerUsername} | {ActionName(Current)} {Target?.playerUsername}\n" +
            $"with group {SecondsWithGroup:0}s | {why}\nsus {sus}\n{LastReason}\n{(lastSay.Length > 0 ? "\"" + lastSay + "\"" : "")}";
    }
}
