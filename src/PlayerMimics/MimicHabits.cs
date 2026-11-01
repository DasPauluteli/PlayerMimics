using System;
using System.Collections.Generic;
using System.Linq;
using GameNetcodeStuff;
using UnityEngine;
using Random = UnityEngine.Random;

namespace PlayerMimics;

// Host only. Records how each player moves their camera and body while a round runs: short clips of looking around,
// crouch-spam, jumps and sprint bursts, each tagged with what was going on (walking or standing, inside, near other
// players, near loot, talking). A Masked replays its player's clips when it's in the same kind of situation, so it
// fidgets like them instead of in a generic way.
internal static class HabitRecorder
{
    [Flags]
    internal enum Situation
    {
        None = 0,
        Moving = 1,
        Inside = 2,
        Group = 4, // another player within 8 m
        Loot = 8, // scrap within 4 m
        Talking = 16,
    }

    internal struct Frame
    {
        public float Yaw; // camera yaw: from the walking direction while moving, else from where the clip started
        public float Pitch; // positive is down, like the camera
        public bool Crouch;
        public bool Sprint;
        public bool Jump;
        public bool AtPlayer; // looking right at someone
    }

    internal sealed class Clip
    {
        public Situation Situation;
        public Frame[] Frames = Array.Empty<Frame>();
    }

    private sealed class Recording
    {
        public Situation Situation;
        public readonly List<Frame> Frames = new();
        public float BaseYaw;
        public Vector3 LastPos;
        public Vector3 Velocity;
        public bool Jumped;
    }

    internal const float SampleRate = 10f;
    private const int ClipFrames = 30; // 3 seconds
    private const int MinFrames = 15;
    private const int MaxClipsPerSituation = 16;
    internal const float MovingSpeed = 1.2f;

    private static readonly Dictionary<string, Dictionary<Situation, List<Clip>>> books = new();
    private static readonly Dictionary<PlayerControllerB, Recording> live = new();
    private static readonly Dictionary<string, (float meters, int samples)> spacing = new();
    private static readonly List<GrabbableObject> scrap = new();
    private static float nextSample;
    private static float lastSample;
    private static float nextScrapRefresh;
    private static StartOfRound? hooked;

    internal static void Clear()
    {
        books.Clear();
        live.Clear();
        spacing.Clear();
        scrap.Clear();
        hooked = null;
    }

    // From HostServices.Update.
    internal static void Sample()
    {
        var round = StartOfRound.Instance;
        if (!Cfg.CopyHabits.Value || round == null || round.inShipPhase)
        {
            live.Clear();
            return;
        }
        if (hooked != round)
        {
            hooked = round;
            round.PlayerJumpEvent.AddListener(OnJump);
        }
        if (Time.time < nextSample) return;
        float dt = Mathf.Clamp(Time.time - lastSample, 0.02f, 0.5f);
        lastSample = Time.time;
        nextSample = Time.time + 1f / SampleRate;
        if (Time.time >= nextScrapRefresh) RefreshScrap();

        foreach (var p in round.allPlayerScripts)
        {
            if (p == null || !p.isPlayerControlled || p.isPlayerDead || Busy(p))
            {
                if (p != null && live.TryGetValue(p, out var stale))
                {
                    Store(p, stale);
                    live.Remove(p);
                }
                continue;
            }
            Vector3 pos = p.transform.position;
            if (!live.TryGetValue(p, out var rec))
            {
                live[p] = rec = new Recording { LastPos = pos };
            }
            Vector3 v = (pos - rec.LastPos) / dt;
            v.y = 0f;
            if (v.magnitude > 25f) v = Vector3.zero; // teleported
            rec.Velocity = Vector3.Lerp(rec.Velocity, v, 0.5f);
            rec.LastPos = pos;

            bool moving = rec.Velocity.magnitude > MovingSpeed;
            var situation = SituationOf(p, moving);
            if (moving && (situation & Situation.Group) != 0) RecordSpacing(p);
            if (rec.Frames.Count > 0 && situation != rec.Situation) Store(p, rec);

            var cam = p.gameplayCamera.transform;
            float camYaw = cam.eulerAngles.y;
            if (rec.Frames.Count == 0)
            {
                rec.Situation = situation;
                rec.BaseYaw = camYaw;
            }
            float reference = moving ? Mathf.Atan2(rec.Velocity.x, rec.Velocity.z) * Mathf.Rad2Deg : rec.BaseYaw;
            rec.Frames.Add(new Frame
            {
                Yaw = Mathf.DeltaAngle(reference, camYaw),
                Pitch = Mathf.Clamp(Mathf.DeltaAngle(0f, cam.localEulerAngles.x), -80f, 80f),
                Crouch = p.isCrouching,
                Sprint = p.isSprinting && moving,
                Jump = rec.Jumped,
                AtPlayer = LooksAtSomeone(p, cam),
            });
            rec.Jumped = false;
            if (rec.Frames.Count >= ClipFrames) Store(p, rec);
        }
    }

    // How far they keep from the nearest other player while walking together (a slow average).
    private static void RecordSpacing(PlayerControllerB p)
    {
        Vector3 pos = p.transform.position;
        float nearest = StartOfRound.Instance.allPlayerScripts
            .Where(o => o != p && o.isPlayerControlled && !o.isPlayerDead && o.isInsideFactory == p.isInsideFactory)
            .Select(o => Vector3.Distance(o.transform.position, pos)).DefaultIfEmpty(99f).Min();
        if (nearest > 8f) return;
        string key = HostServices.PlayerKey(p);
        spacing[key] = spacing.TryGetValue(key, out var s) ? (Mathf.Lerp(s.meters, nearest, 0.02f), s.samples + 1) : (nearest, 1);
    }

    // Known after about 10 seconds of walking with others.
    internal static bool Spacing(PlayerControllerB p, out float meters)
    {
        meters = 0f;
        if (!spacing.TryGetValue(HostServices.PlayerKey(p), out var s) || s.samples < 100) return false;
        meters = s.meters;
        return true;
    }

    private static void OnJump(PlayerControllerB p)
    {
        if (p != null && live.TryGetValue(p, out var rec)) rec.Jumped = true;
    }

    // Doing something that isn't moving around: typing, the terminal, a ladder, an emote, a car, a monster's grip.
    private static bool Busy(PlayerControllerB p) =>
        p.inTerminalMenu || p.isClimbingLadder || p.inSpecialInteractAnimation || p.isTypingChat || p.performingEmote ||
        p.inAnimationWithEnemy != null || p.inVehicleAnimation || p.jetpackControls || p.isUnderwater ||
        (p == GameNetworkManager.Instance.localPlayerController && p.quickMenuManager != null && p.quickMenuManager.isMenuOpen);

    private static Situation SituationOf(PlayerControllerB p, bool moving)
    {
        var s = Situation.None;
        if (moving) s |= Situation.Moving;
        if (p.isInsideFactory) s |= Situation.Inside;
        Vector3 pos = p.transform.position;
        if (StartOfRound.Instance.allPlayerScripts.Any(o => o != p && o.isPlayerControlled && !o.isPlayerDead &&
                                                            o.isInsideFactory == p.isInsideFactory &&
                                                            Vector3.Distance(o.transform.position, pos) < 8f))
        {
            s |= Situation.Group;
        }
        if (NearLoot(pos)) s |= Situation.Loot;
        if (Talking(p)) s |= Situation.Talking;
        return s;
    }

    internal static bool NearLoot(Vector3 pos) =>
        scrap.Any(g => g != null && !g.isHeld && Vector3.Distance(g.transform.position, pos) < 4f);

    private static void RefreshScrap()
    {
        nextScrapRefresh = Time.time + 2f;
        scrap.Clear();
        scrap.AddRange(UnityEngine.Object.FindObjectsOfType<GrabbableObject>()
            .Where(g => g.itemProperties != null && g.itemProperties.isScrap && !g.isInShipRoom));
    }

    private static bool Talking(PlayerControllerB p)
    {
        if (p == GameNetworkManager.Instance.localPlayerController)
        {
            var comms = StartOfRound.Instance.voiceChatModule;
            var me = comms != null ? comms.FindPlayer(comms.LocalPlayerName) : null;
            return me != null && me.IsSpeaking;
        }
        return p.voicePlayerState != null && p.voicePlayerState.IsSpeaking;
    }

    // Camera pointed within a few degrees of someone's head (a player or something that looks like one).
    private static bool LooksAtSomeone(PlayerControllerB p, Transform cam)
    {
        bool At(Vector3 head)
        {
            Vector3 d = head - cam.position;
            return d.magnitude is > 0.5f and < 20f && Vector3.Angle(cam.forward, d) < 8f;
        }
        foreach (var o in StartOfRound.Instance.allPlayerScripts)
        {
            if (o != p && o.isPlayerControlled && !o.isPlayerDead && At(o.gameplayCamera.transform.position)) return true;
        }
        return MimicDisguise.All.Any(m => m != null && m.Masked != null && !m.Masked.isEnemyDead &&
                                          At(m.Masked.eye != null ? m.Masked.eye.position : m.transform.position + Vector3.up * 2f));
    }

    private static void Store(PlayerControllerB p, Recording rec)
    {
        if (rec.Frames.Count >= MinFrames)
        {
            string key = HostServices.PlayerKey(p);
            if (!books.TryGetValue(key, out var book)) books[key] = book = new Dictionary<Situation, List<Clip>>();
            if (!book.TryGetValue(rec.Situation, out var clips)) book[rec.Situation] = clips = new List<Clip>();
            var clip = new Clip { Situation = rec.Situation, Frames = rec.Frames.ToArray() };
            if (clips.Count < MaxClipsPerSituation) clips.Add(clip);
            else clips[Random.Range(0, clips.Count)] = clip; // keep a spread over the round, not just the start
        }
        rec.Frames.Clear();
    }

    internal static int ClipCount(PlayerControllerB p) =>
        books.TryGetValue(HostServices.PlayerKey(p), out var book) ? book.Values.Sum(c => c.Count) : 0;

    // A clip of this player from the closest situation: walking or standing has to match, then being inside counts
    // most, then other players around and talking, then loot. Null until there are a few clips to choose from.
    internal static Clip? Pick(PlayerControllerB p, Situation now)
    {
        if (!books.TryGetValue(HostServices.PlayerKey(p), out var book)) return null;
        bool moving = (now & Situation.Moving) != 0;
        var pool = book.Where(kv => ((kv.Key & Situation.Moving) != 0) == moving).ToList();
        if (pool.Sum(kv => kv.Value.Count) < 3) return null;
        int Score(Situation s) =>
            (Same(s, now, Situation.Inside) ? 4 : 0) + (Same(s, now, Situation.Group) ? 2 : 0) +
            (Same(s, now, Situation.Talking) ? 2 : 0) + (Same(s, now, Situation.Loot) ? 1 : 0);
        int best = pool.Max(kv => Score(kv.Key));
        var clips = pool.Where(kv => Score(kv.Key) == best).SelectMany(kv => kv.Value).ToList();
        return clips[Random.Range(0, clips.Count)];
    }

    private static bool Same(Situation a, Situation b, Situation flag) => (a & flag) == (b & flag);
}

// The replay side, on the brain.
internal sealed partial class MimicBrain
{
    private HabitRecorder.Clip? habit;
    private float habitStart;
    private int habitAppliedFrame = -1;
    private float habitBaseYaw;
    private float habitMismatchSince = -1f;
    private bool habitCrouch;
    private bool habitSprint;
    private Vector3? habitPoint;
    private float habitPointSentAt;
    private PlayerControllerB? habitPlayer;
    private float lastHabitJump = -10f;
    private MimicVoice? voice;

    private bool HabitMoving
    {
        get
        {
            var agent = Masked.agent;
            if (agent == null || !agent.enabled) return false;
            Vector3 v = agent.velocity;
            v.y = 0f;
            return v.magnitude > HabitRecorder.MovingSpeed;
        }
    }

    private HabitRecorder.Situation MySituation()
    {
        var s = HabitRecorder.Situation.None;
        if (HabitMoving) s |= HabitRecorder.Situation.Moving;
        if (!Masked.isOutside) s |= HabitRecorder.Situation.Inside;
        if (AlivePlayers().Any(p => IsAudience(p) && SameSide(p) && Dist(p) < 8f)) s |= HabitRecorder.Situation.Group;
        if (HabitRecorder.NearLoot(transform.position)) s |= HabitRecorder.Situation.Loot;
        if (voice == null) voice = GetComponent<MimicVoice>();
        if (voice != null && voice.Speaking) s |= HabitRecorder.Situation.Talking;
        return s;
    }

    private int HabitFrameIndex => (int)((Time.time - habitStart) * HabitRecorder.SampleRate);

    // From Gaze when it's time for a new idle look: most of the time one of its player's own clips.
    private bool StartHabit()
    {
        if (!Cfg.CopyHabits.Value || Random.value > 0.8f) return false;
        var clip = HabitRecorder.Pick(Copied, MySituation());
        if (clip == null) return false;
        habit = clip;
        habitStart = Time.time;
        habitAppliedFrame = -1;
        habitBaseYaw = transform.eulerAngles.y;
        habitMismatchSince = -1f;
        habitPoint = null;
        habitPlayer = null;
        HabitLook();
        return true;
    }

    private void StopHabit()
    {
        habit = null;
        habitCrouch = false;
        habitSprint = false;
        habitPoint = null;
        habitPlayer = null;
    }

    // From Gaze (AI tick): keeps the clip's look going. False once it's over or something else took the eyes (a
    // glance at someone talking, a noise), or the situation changed (it stopped or started walking).
    private bool HabitGaze()
    {
        if (habit == null) return false;
        bool mine = (habitPlayer != null && lookPlayer == habitPlayer) ||
                    (habitPoint.HasValue && lookPlayer == null && lookPoint.HasValue && lookPoint.Value == habitPoint.Value);
        bool matches = (MySituation() & HabitRecorder.Situation.Moving) == (habit.Situation & HabitRecorder.Situation.Moving);
        if (matches) habitMismatchSince = -1f;
        else if (habitMismatchSince < 0f) habitMismatchSince = Time.time;
        bool over = HabitFrameIndex >= habit.Frames.Length;
        if (!mine || over || (habitMismatchSince >= 0f && Time.time - habitMismatchSince > 0.6f))
        {
            StopHabit();
            if (over) nextIdleLook = Time.time + Random.Range(0.2f, 1.2f); // players rarely hold still for long
            return false;
        }
        HabitLook();
        return true;
    }

    private void HabitLook()
    {
        if (habit == null) return;
        var f = habit.Frames[Mathf.Clamp(HabitFrameIndex, 0, habit.Frames.Length - 1)];
        if (f.AtPlayer)
        {
            var near = AlivePlayers().Where(p => IsAudience(p) && SameSide(p) && Dist(p) < 15f).OrderBy(Dist).FirstOrDefault();
            if (near != null)
            {
                if (lookPlayer != near) LookAtPlayer(near, 0.8f);
                else lookPlayerUntil = Time.time + 0.8f;
                habitPlayer = near;
                habitPoint = null;
                return;
            }
        }
        habitPlayer = null;
        bool moving = (habit.Situation & HabitRecorder.Situation.Moving) != 0;
        float reference = habitBaseYaw;
        if (moving && Masked.agent != null && Masked.agent.velocity.sqrMagnitude > 0.25f) reference = Yaw(Masked.agent.velocity);
        Vector3 dir = Quaternion.Euler(f.Pitch, reference + f.Yaw, 0f) * Vector3.forward;
        Vector3 point = Head + dir * 6f;
        // Fewer look messages: only when the aim moved, or often enough to keep up while walking.
        if (habitPoint.HasValue && lookPoint.HasValue && lookPoint.Value == habitPoint.Value &&
            Vector3.Angle(point - Head, habitPoint.Value - Head) < 4f && Time.time - habitPointSentAt < (moving ? 0.2f : 0.5f))
        {
            lookPointUntil = Time.time + 0.8f;
            return;
        }
        LookAtPoint(point, 0.8f);
        habitPoint = point;
        habitPointSentAt = Time.time;
    }

    // Crouching, sprinting and jumping from the clip only while it's just hanging around or walking with people.
    private bool HabitBodyAllowed =>
        Current is Behaviour.BlendIn or Behaviour.Follow or Behaviour.Wander or Behaviour.Lure &&
        !Fleeing && !Masked.inSpecialAnimation && !HuntingKnower;

    // From Update, every frame: the clip's crouch and sprint, and its jumps when they come up.
    private void HabitBodyStep()
    {
        if (habit == null || !HabitBodyAllowed)
        {
            habitCrouch = false;
            habitSprint = false;
            return;
        }
        int i = HabitFrameIndex;
        if (i >= habit.Frames.Length)
        {
            habitCrouch = false;
            habitSprint = false;
            return;
        }
        var f = habit.Frames[i];
        habitCrouch = f.Crouch;
        habitSprint = f.Sprint && (habit.Situation & HabitRecorder.Situation.Moving) != 0;
        for (int j = Mathf.Max(0, habitAppliedFrame + 1); j <= i; j++)
        {
            if (habit.Frames[j].Jump) HabitJump();
        }
        habitAppliedFrame = i;
    }

    private void HabitJump()
    {
        // Players can't jump crouched or out of breath; a web holds you down.
        if (crouching || exhausted || Time.time - lastHabitJump < 0.9f || HazardSpeedFactor < 0.99f || Masked.inSpecialAnimation) return;
        lastHabitJump = Time.time;
        stamina = Mathf.Max(0f, stamina - 0.08f);
        ModNetwork.Instance?.MimicJump(Masked);
    }

    private string HabitOverlay() =>
        Cfg.CopyHabits.Value ? $"habits: {HabitRecorder.ClipCount(Copied)} clips{(habit != null ? $", playing {habit.Situation}" : "")}\n" : "";
}
