using System.Collections.Generic;
using System.Linq;
using GameNetcodeStuff;
using UnityEngine;

namespace PlayerMimics;

// Host only. How a Masked moves, turns and looks around, matched to players: player walk speed, sprinting that builds
// up and runs out of stamina, a body that turns where a player would point their camera, glances at whoever talks or
// makes a noise, and following that keeps a loose spot beside someone instead of tailing them exactly.
internal sealed partial class MimicBrain
{
    private enum FleeStyle
    {
        Run,
        BackAway, // walks backwards, facing it (bracken, coil-head)
        Sneak, // crouch-walks away (eyeless dogs)
    }

    // Player numbers (PlayerControllerB): sprinting builds up to 2.25x walk speed, a full bar lasts 5 seconds, you
    // need 30% to start sprinting and stop at 10%.
    private const float SprintMultiplierMax = 2.25f;
    private const float SprintSeconds = 5f;
    private const float AttackSpeed = 8f; // a vanilla Masked's chase speed

    private float stamina = 1f;
    private bool exhausted;
    private float sprintMultiplier = 1f;
    private bool isSprinting;
    private bool crouching;
    private bool wantSprint;
    private bool wantCrouch;
    private float speedCap = float.MaxValue;
    private float lastPositionSync;

    // Where it looks. The body turns here on the host and reaches clients through the normal rotation sync; clients
    // aim the head themselves from the look RPCs (MimicDisguise.LookAtFocused).
    private PlayerControllerB? lookPlayer;
    private float lookPlayerUntil;
    private Vector3? lookPoint;
    private float lookPointUntil;
    private int staringAt = -1;
    private bool faceLookWhileMoving;
    private float yawVelocity;
    private float nextIdleLook;
    private PlayerControllerB? glancePlayer;
    private float glancePlayerAt;
    private Vector3? glancePoint;
    private float glancePointAt;
    private float lastNoiseGlance = -100f;

    private Vector3 wanderPoint;
    private bool wanderInside;
    private float nextWanderPick;
    private float lingerUntil;
    private string heading = "";
    private Vector3? spotOfInterest;
    private Vector3 lurePoint;

    private float slotAngle = 40f;
    private float slotDistance = 3f;
    private float nextSlotRoll;
    private bool followMoving;
    private float followReactAt = -1f;
    private Vector3 followDest;
    private float lastFollowPath = -100f;

    private PlayerControllerB? fleeFrom;
    private EnemyAI? fleeMonster;
    private Vector3 fleeOrigin;
    private FleeStyle fleeStyle;
    private float fleeUntil;
    private Vector3 fleePoint;
    private float nextFleePick;

    private readonly Dictionary<PlayerControllerB, (Vector3 pos, Vector3 vel, float time)> playerMotion = new();

    // A reflex (a monster, getting hit with a sign) is running and beats whatever the LLM picks.
    internal bool Fleeing => Time.time < fleeUntil;

    private void InitMotion()
    {
        wanderPoint = transform.position;
        RollFollowSlot();
        nextIdleLook = Time.time + Random.Range(1f, 3f);
    }

    private void ResetMovementPlan()
    {
        nextWanderPick = 0f;
        lingerUntil = 0f;
        heading = "";
        spotOfInterest = null;
        followMoving = false;
        followReactAt = -1f;
        lastFollowPath = -100f;
        nextFleePick = 0f;
    }

    // ---- every frame: stamina, speed, turning, position sync

    private void Update()
    {
        if (Masked == null) return;
        if (Masked.isEnemyDead)
        {
            DropEverythingOnDeath();
            return;
        }
        float dt = Time.deltaTime;
        var agent = Masked.agent;
        bool walking = agent != null && agent.enabled && agent.velocity.sqrMagnitude > 0.5f;
        if (Current == Behaviour.Attack)
        {
            isSprinting = walking; // the chase itself ignores stamina, like a Masked
        }
        else if (!wantSprint || !walking)
        {
            isSprinting = false;
        }
        else if (isSprinting)
        {
            if (stamina <= 0.1f)
            {
                isSprinting = false;
                exhausted = true;
            }
        }
        else if (stamina >= 0.3f)
        {
            isSprinting = true;
        }

        if (isSprinting)
        {
            if (Current != Behaviour.Attack) stamina = Mathf.Max(0f, stamina - dt / SprintSeconds * CarryWeight); // loot drains it faster
            sprintMultiplier = Mathf.Lerp(sprintMultiplier, SprintMultiplierMax, dt);
        }
        else
        {
            // Players get stamina back faster standing still than walking.
            stamina = Mathf.Min(1f, stamina + dt / (SprintSeconds + (walking ? 9f : 4f)));
            if (exhausted && stamina >= 0.3f) exhausted = false;
            sprintMultiplier = Mathf.Lerp(sprintMultiplier, 1f, 10f * dt);
        }
        crouching = wantCrouch && !isSprinting;
        HazardStep();

        // Position updates 10 times a second while it moves, so other screens see a walk instead of hops.
        if (walking && Time.time - lastPositionSync >= 0.1f)
        {
            lastPositionSync = Time.time;
            Masked.SyncPositionToClients();
        }
    }

    // Runs after the vanilla Update, which sets its own Masked speeds every frame.
    internal void ApplySpeed()
    {
        var agent = Masked.agent;
        if (agent == null || Masked.isEnemyDead || Masked.stunNormalizedTimer >= 0f || Masked.inSpecialAnimation) return;
        // Like a player, what it carries divides its speed.
        agent.speed = Current == Behaviour.Attack
            ? AttackSpeed / CarryWeight
            : Mathf.Min(Cfg.WalkSpeed.Value / (crouching ? 1.5f : 1f) * sprintMultiplier / CarryWeight, speedCap);
        agent.speed *= HazardSpeedFactor;
    }

    private float CarryWeight => Disguise != null && Disguise.Inventory != null ? Disguise.Inventory.CarryWeight : 1f;

    private void LateUpdate()
    {
        if (Masked == null || Masked.isEnemyDead || Masked.inSpecialAnimation || Masked.stunNormalizedTimer >= 0f) return;
        float? want = WantedYaw();
        if (!want.HasValue)
        {
            yawVelocity = 0f;
            return;
        }
        float smooth = Disguise.Moving ? 0.12f : 0.2f;
        float yaw = Mathf.SmoothDampAngle(transform.eulerAngles.y, want.Value, ref yawVelocity, smooth, 720f, Time.deltaTime);
        transform.rotation = Quaternion.Euler(0f, yaw, 0f);
    }

    // A player's body always faces their camera: where they walk, or what they look at while walking (up to a
    // sideways strafe). Backing away keeps facing the danger.
    private float? WantedYaw()
    {
        var agent = Masked.agent;
        Vector3 velocity = agent != null ? agent.velocity : Vector3.zero;
        velocity.y = 0f;
        bool moving = velocity.sqrMagnitude > 0.25f;
        Vector3? look = LookTarget();
        if (look.HasValue)
        {
            Vector3 d = look.Value - transform.position;
            d.y = 0f;
            if (d.sqrMagnitude > 0.09f)
            {
                float lookYaw = Yaw(d);
                if (!moving || faceLookWhileMoving || Mathf.Abs(Mathf.DeltaAngle(lookYaw, Yaw(velocity))) < 100f) return lookYaw;
            }
        }
        if (!moving) return null;
        Vector3 desired = agent!.desiredVelocity;
        desired.y = 0f;
        return Yaw(desired.sqrMagnitude > 0.25f ? desired : velocity);
    }

    private static float Yaw(Vector3 d) => Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;

    private static float FlatDistance(Vector3 a, Vector3 b) => new Vector2(a.x - b.x, a.z - b.z).magnitude;

    private static bool TryNavPoint(Vector3 pos, float radius, out Vector3 result)
    {
        result = RoundManager.Instance.GetNavMeshPosition(pos, default, radius);
        return RoundManager.Instance.GotNavMeshPositionResult;
    }

    // ---- watching players move

    private void TrackPlayerMotion(List<PlayerControllerB> alive)
    {
        float now = Time.time;
        foreach (var p in alive)
        {
            Vector3 pos = p.transform.position;
            if (playerMotion.TryGetValue(p, out var m) && now - m.time > 0.01f)
            {
                Vector3 v = (pos - m.pos) / (now - m.time);
                v.y = 0f;
                if (v.magnitude > 25f) v = Vector3.zero; // teleported
                playerMotion[p] = (pos, Vector3.Lerp(m.vel, v, 0.5f), now);
            }
            else
            {
                playerMotion[p] = (pos, Vector3.zero, now);
            }
        }
    }

    private Vector3 VelocityOf(PlayerControllerB p) => playerMotion.TryGetValue(p, out var m) ? m.vel : Vector3.zero;

    // ---- gaze

    private Vector3? LookTarget()
    {
        if (lookPlayer != null && Time.time < lookPlayerUntil && !lookPlayer.isPlayerDead) return lookPlayer.gameplayCamera.transform.position;
        if (lookPoint.HasValue && Time.time < lookPointUntil) return lookPoint.Value;
        return null;
    }

    internal void LookAtPlayer(PlayerControllerB p, float seconds)
    {
        lookPlayer = p;
        lookPlayerUntil = Time.time + seconds;
        lookPoint = null;
        int id = (int)p.playerClientId;
        if (staringAt != id)
        {
            staringAt = id;
            Masked.LookAtPlayerServerRpc(id);
        }
    }

    private void LookAtPoint(Vector3 point, float seconds)
    {
        StopStaring();
        lookPlayer = null;
        lookPoint = point;
        lookPointUntil = Time.time + seconds;
        Masked.LookAtPositionServerRpc(point, seconds);
    }

    private void ClearLook()
    {
        lookPlayer = null;
        lookPoint = null;
        StopStaring();
    }

    private void StopStaring()
    {
        if (staringAt == -1) return;
        staringAt = -1;
        Masked.StopLookingAtTransformServerRpc();
    }

    // People turn to whoever talks, after a moment.
    internal void GlanceAt(PlayerControllerB p)
    {
        if (p == lookPlayer) return;
        glancePlayer = p;
        glancePlayerAt = Time.time + Random.Range(0.15f, 0.5f);
    }

    // From the DetectNoise patch. Loud things nobody's standing next to get a quick look (a dropped item, a door, a
    // gunshot); steps and voices of the people around it don't.
    internal void OnNoise(Vector3 position, float loudness, int noiseId)
    {
        if (noiseId == 7 || Current is Behaviour.Attack or Behaviour.Flee) return; // 7: footsteps
        float d = Vector3.Distance(position, transform.position);
        if (d < 1.5f || loudness / d < 0.12f || Time.time - lastNoiseGlance < 4f) return;
        if (AlivePlayers().Any(p => Vector3.Distance(p.transform.position, position) < 3f)) return;
        lastNoiseGlance = Time.time;
        glancePoint = position;
        glancePointAt = Time.time + Random.Range(0.2f, 0.6f);
    }

    private void Gaze()
    {
        if (lookPlayer != null && (Time.time >= lookPlayerUntil || lookPlayer.isPlayerDead)) ClearLook();
        if (lookPoint.HasValue && Time.time >= lookPointUntil) lookPoint = null;
        bool busy = Current is Behaviour.Attack or Behaviour.Flee;

        if (glancePlayer != null && Time.time >= glancePlayerAt)
        {
            var p = glancePlayer;
            glancePlayer = null;
            if (!busy && !p.isPlayerDead && SameSide(p) && Dist(p) < 25f) LookAtPlayer(p, Random.Range(1.2f, 2.5f));
        }
        if (glancePoint.HasValue && Time.time >= glancePointAt)
        {
            Vector3 point = glancePoint.Value;
            glancePoint = null;
            if (!busy) LookAtPoint(point, Random.Range(0.8f, 1.6f));
        }

        if (busy || Current is Behaviour.Stare or Behaviour.Emote) return;
        if (LookTarget().HasValue || Time.time < nextIdleLook) return;
        IdleLook();
    }

    // What players do with their camera when nothing's going on: look at people, around the room, at loot, at
    // the floor. While walking, mostly ahead with a quick look aside now and then.
    private void IdleLook()
    {
        bool moving = Disguise.Moving;
        nextIdleLook = Time.time + (moving ? Random.Range(3f, 8f) : Random.Range(1.5f, 4.5f));
        var near = AlivePlayers().Where(p => IsAudience(p) && SameSide(p) && Dist(p) < (moving ? 12f : 18f)).OrderBy(Dist).ToList();
        float r = Random.value;
        if (moving)
        {
            if (r < 0.35f && near.Count > 0)
            {
                LookAtPlayer(near[Random.Range(0, Mathf.Min(2, near.Count))], Random.Range(0.6f, 1.4f));
            }
            else if (r < 0.6f)
            {
                Vector3 aside = Quaternion.Euler(0f, Random.Range(-70f, 70f), 0f) * transform.forward * 8f;
                LookAtPoint(transform.position + aside + Vector3.up * Random.Range(0.6f, 2.2f), Random.Range(0.6f, 1.2f));
            }
            return;
        }
        if (r < 0.45f && near.Count > 0)
        {
            LookAtPlayer(near[Random.Range(0, Mathf.Min(3, near.Count))], Random.Range(1.5f, 4f));
        }
        else if (r < 0.7f)
        {
            Vector3 around = Quaternion.Euler(0f, Random.Range(-120f, 120f), 0f) * transform.forward * Random.Range(4f, 12f);
            LookAtPoint(Head + around + Vector3.up * Random.Range(-1.2f, 0.5f), Random.Range(1f, 3f));
        }
        else if (r < 0.85f && NearbyPoi(10f, out Vector3 poi))
        {
            LookAtPoint(poi + Vector3.up * 0.3f, Random.Range(1f, 2.5f));
        }
        else
        {
            LookAtPoint(transform.position + transform.forward * Random.Range(2f, 4f), Random.Range(1f, 2f)); // the floor
        }
    }

    // ---- behaviours (one step per AI tick)

    private void Run()
    {
        wantSprint = false;
        wantCrouch = false;
        speedCap = float.MaxValue;
        faceLookWhileMoving = false;
        if (Target != null && (Target.isPlayerDead || !Target.isPlayerControlled))
        {
            Target = null;
            if (Current is Behaviour.Attack or Behaviour.Follow or Behaviour.Stare or Behaviour.Lure)
            {
                SetBehaviour(Behaviour.BlendIn, null, 0f);
            }
        }
        if (Fleeing && Current != Behaviour.Flee) SetBehaviour(Behaviour.Flee, fleeFrom, fleeUntil - Time.time);
        switch (Current)
        {
            case Behaviour.BlendIn: BlendIn(); break;
            case Behaviour.Follow: Follow(Target); break;
            case Behaviour.Stare: Stop(); break;
            case Behaviour.Wander: Wander(); break;
            case Behaviour.Lure: Lure(); break;
            case Behaviour.Attack: Attack(); break;
            case Behaviour.Flee: Flee(); break;
            case Behaviour.Emote: Stop(); break;
            case Behaviour.PickUp: PickUpStep(); break;
            case Behaviour.Drop: DropStep(); break;
            case Behaviour.Entrance: EntranceStep(); break;
            case Behaviour.GoToShip: GoToShipStep(); break;
        }
    }

    private void Stop() => Masked.SetDestinationToPosition(transform.position);

    // Walks to a point, through a door when the point is on the other side (MimicItems.WalkThroughDoorToward).
    private void GoTo(Vector3 point, bool inside)
    {
        if (inside == Masked.isOutside)
        {
            WalkThroughDoorToward(point);
            return;
        }
        Masked.SetDestinationToPosition(point);
    }

    // Sticks with the closest crewmate: goes along when they move, pokes around near them when they stop.
    private void BlendIn()
    {
        var anchor = StickWith();
        if (anchor == null)
        {
            Wander();
            return;
        }
        float d = Dist(anchor);
        if (!SameSide(anchor) || d > 12f || VelocityOf(anchor).magnitude > 1.5f)
        {
            heading = "";
            Follow(anchor);
            return;
        }
        wantCrouch = anchor.isCrouching && d < 10f;
        bool arrived = FlatDistance(transform.position, wanderPoint) < 1.5f;
        if (Time.time >= nextWanderPick || (arrived && lingerUntil > 0f && Time.time >= lingerUntil))
        {
            PickSpotNear(anchor);
            arrived = false;
        }
        else if (arrived && lingerUntil == 0f)
        {
            // Stop and check it out for a few seconds.
            lingerUntil = Time.time + Random.Range(3f, 8f);
            if (spotOfInterest.HasValue) LookAtPoint(spotOfInterest.Value + Vector3.up * 0.2f, Random.Range(1.5f, 3f));
        }
        if (arrived) Stop();
        else GoTo(wanderPoint, anchor.isInsideFactory);
    }

    private void PickSpotNear(PlayerControllerB anchor)
    {
        nextWanderPick = Time.time + Random.Range(10f, 20f);
        lingerUntil = 0f;
        Vector3 a = anchor.transform.position;
        bool inside = anchor.isInsideFactory;
        var spots = Pois().Where(p => p.Inside == inside && Vector3.Distance(p.Pos, a) < 10f &&
                                      Vector3.Distance(p.Pos, transform.position) > 2f).ToList();
        if (spots.Count > 0 && Random.value < 0.6f)
        {
            var poi = spots[Random.Range(0, spots.Count)];
            Vector3 beside = poi.Pos + Quaternion.Euler(0f, Random.Range(0f, 360f), 0f) * Vector3.forward * 1.2f;
            if (TryNavPoint(beside, 2.5f, out Vector3 spot))
            {
                wanderPoint = spot;
                spotOfInterest = poi.Pos;
                heading = poi.Name;
                return;
            }
        }
        spotOfInterest = null;
        heading = "";
        Vector3 around = a + Quaternion.Euler(0f, Random.Range(0f, 360f), 0f) * Vector3.forward * Random.Range(2.5f, 6f);
        wanderPoint = TryNavPoint(around, 3f, out Vector3 nav) ? nav : transform.position;
    }

    private void RollFollowSlot()
    {
        slotAngle = Random.Range(25f, 65f);
        slotDistance = Random.Range(2.2f, 4f);
        nextSlotRoll = Time.time + Random.Range(12f, 30f);
    }

    // Walks with someone like a friend would: a few metres behind and off to one side, at their pace (people with
    // heavy loot are slow), a moment late when they start or stop, and only catching up when it falls behind.
    private void Follow(PlayerControllerB? who)
    {
        if (who == null)
        {
            Wander();
            return;
        }
        Anchor = who;
        anchorUntil = Time.time + 5f;
        float d = Dist(who);
        if (!SameSide(who))
        {
            // They went through a door: go after them.
            wantSprint = d > 25f;
            followMoving = true;
            GoTo(who.transform.position, who.isInsideFactory);
            return;
        }
        if (Time.time >= nextSlotRoll) RollFollowSlot();
        wantCrouch = who.isCrouching && d < 10f;
        Vector3 them = who.transform.position;
        Vector3 velocity = VelocityOf(who);
        float speed = velocity.magnitude;
        Vector3 goal;
        bool wantMove;
        if (speed > 1f)
        {
            Vector3 forward = velocity / speed;
            Vector3 toMe = transform.position - them;
            toMe.y = 0f;
            // Stay on the side it's already on, so it never cuts in front of them; beside them when it's ahead.
            float side = Vector3.Cross(forward, toMe).y >= 0f ? 1f : -1f;
            float angle = Vector3.Dot(forward, toMe) > 0f ? 100f : slotAngle;
            goal = them + Quaternion.Euler(0f, -angle * side, 0f) * -forward * slotDistance;
            float off = FlatDistance(transform.position, goal);
            wantMove = followMoving || off > 1.5f;
            float walk = Cfg.WalkSpeed.Value / (wantCrouch ? 1.5f : 1f);
            speedCap = Mathf.Clamp(speed + Mathf.Max(0f, off - 1f) * 0.8f, 1.5f, walk);
            wantSprint = (who.isSprinting && off > 3f) || off > 12f;
            if (wantSprint) speedCap = float.MaxValue;
        }
        else
        {
            // They stopped: only close a big gap, or step back when it's in their face.
            Vector3 fromThem = transform.position - them;
            fromThem.y = 0f;
            float dist = fromThem.magnitude;
            if (dist > 6f || dist < 1.2f)
            {
                Vector3 dir = dist > 0.1f ? fromThem / dist : -who.transform.forward;
                goal = them + dir * slotDistance;
                wantMove = true;
            }
            else
            {
                goal = followDest;
                wantMove = followMoving && FlatDistance(transform.position, followDest) > 0.8f;
            }
            wantSprint = dist > 20f;
        }

        if (wantMove && !followMoving)
        {
            if (followReactAt < 0f) followReactAt = Time.time + Random.Range(0.25f, 0.8f);
            if (Time.time < followReactAt)
            {
                Stop();
                return;
            }
        }
        followReactAt = -1f;
        followMoving = wantMove;
        if (!followMoving)
        {
            Stop();
            return;
        }
        if (!TryNavPoint(goal, 2.5f, out Vector3 nav)) nav = them;
        // A new path only when the spot really moved, so it doesn't twitch.
        if (FlatDistance(nav, followDest) > 1f || Time.time - lastFollowPath > 2f)
        {
            followDest = nav;
            lastFollowPath = Time.time;
            Masked.SetDestinationToPosition(nav);
        }
        if (speed <= 1f && FlatDistance(transform.position, followDest) < 0.6f) followMoving = false;
    }

    // ---- wandering to places a player would go

    // Not random: walks to places a player would go (scrap, the entrances, the apparatus, the ship), stays near the
    // crew when there is one, and stops at each spot for a bit like someone checking it out.
    private struct Poi
    {
        public Vector3 Pos;
        public bool Inside;
        public string Name;
    }

    private static readonly List<Poi> pois = new();
    private static float poisFound = -100f;

    private static List<Poi> Pois()
    {
        if (Time.time - poisFound < 5f) return pois;
        poisFound = Time.time;
        pois.Clear();
        var round = StartOfRound.Instance;
        if (round != null && round.elevatorTransform != null)
        {
            pois.Add(new Poi { Pos = round.elevatorTransform.position, Inside = false, Name = "the ship" });
        }
        foreach (var door in Object.FindObjectsOfType<EntranceTeleport>())
        {
            string name = door.entranceId == 0 ? "the main entrance" : "a fire exit";
            pois.Add(new Poi
            {
                Pos = door.entrancePoint != null ? door.entrancePoint.position : door.transform.position,
                Inside = !door.isEntranceToBuilding,
                Name = name,
            });
        }
        foreach (var item in Object.FindObjectsOfType<GrabbableObject>())
        {
            if (item.isHeld || item.isInShipRoom || item.isInElevator || item.itemProperties == null) continue;
            if (item is LungProp lung)
            {
                if (lung.isLungDocked) pois.Add(new Poi { Pos = item.transform.position, Inside = true, Name = "the apparatus" });
                continue;
            }
            if (!item.itemProperties.isScrap) continue;
            pois.Add(new Poi { Pos = item.transform.position, Inside = item.isInFactory, Name = "some scrap (" + item.itemProperties.itemName + ")" });
        }
        return pois;
    }

    private bool NearbyPoi(float range, out Vector3 pos)
    {
        bool inside = !Masked.isOutside;
        var near = Pois().Where(p => p.Inside == inside && Vector3.Distance(p.Pos, transform.position) is > 1.5f and < 10f)
            .Where(p => Vector3.Distance(p.Pos, transform.position) < range).ToList();
        pos = near.Count > 0 ? near[Random.Range(0, near.Count)].Pos : Vector3.zero;
        return near.Count > 0;
    }

    private bool PickPoi(out Poi pick)
    {
        pick = default;
        bool inside = !Masked.isOutside;
        var crew = AlivePlayers().Where(p => IsAudience(p) && SameSide(p)).ToList();
        var options = Pois()
            .Where(p => p.Inside == inside)
            .Where(p => Vector3.Distance(p.Pos, transform.position) is > 4f and < 60f)
            .Where(p => Vector3.Distance(p.Pos, wanderPoint) > 3f)
            // With crew around, only go places near them, so it never drifts off on its own.
            .Where(p => crew.Count == 0 || crew.Any(c => Vector3.Distance(c.transform.position, p.Pos) < 25f))
            .OrderBy(p => Vector3.Distance(p.Pos, transform.position) * Random.Range(0.6f, 1.4f))
            .Take(4)
            .ToList();
        if (options.Count == 0) return false;
        pick = options[Random.Range(0, options.Count)];
        return true;
    }

    private void Wander()
    {
        if (heading.Length == 0 || Time.time >= nextWanderPick) PickWanderPoint();
        bool arrived = FlatDistance(transform.position, wanderPoint) < 2.5f && wanderInside == !Masked.isOutside;
        if (arrived && lingerUntil == 0f)
        {
            // Stop and look at the spot like someone checking it.
            lingerUntil = Time.time + Random.Range(2.5f, 6f);
            LookAtPoint(wanderPoint + Vector3.up * 0.5f, 1.5f);
        }
        if (arrived && Time.time < lingerUntil)
        {
            Stop();
            return;
        }
        if (arrived) PickWanderPoint();
        GoTo(wanderPoint, wanderInside);
    }

    private void PickWanderPoint()
    {
        nextWanderPick = Time.time + Random.Range(20f, 35f);
        lingerUntil = 0f;
        if (PickPoi(out var poi))
        {
            wanderPoint = RoundManager.Instance.GetNavMeshPosition(poi.Pos, default, 4f);
            wanderInside = poi.Inside;
            heading = poi.Name;
            return;
        }
        // Nothing interesting nearby: go back to the crew, or to the ship when there's nobody.
        var crew = Nearest();
        if (crew != null)
        {
            heading = "back to " + crew.playerUsername;
            wanderPoint = RoundManager.Instance.GetNavMeshPosition(crew.transform.position, default, 4f);
            wanderInside = crew.isInsideFactory;
            nextWanderPick = Time.time + 4f; // they move, so re-aim soon
            return;
        }
        wanderPoint = StartOfRound.Instance.elevatorTransform.position;
        wanderInside = false;
        heading = "the ship";
    }

    // ---- luring, attacking, fleeing

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
        return good.Count > 0 ? good[Random.Range(0, good.Count)] : transform.position;
    }

    private void Lure()
    {
        if (Target == null)
        {
            BlendIn();
            return;
        }
        if (FlatDistance(transform.position, lurePoint) > 1.5f)
        {
            Masked.SetDestinationToPosition(lurePoint);
            return;
        }
        Stop();
        if (lookPlayer != Target) LookAtPlayer(Target, 4f);
        // Got them alone and close: the moment the rules allow it, strike.
        if (Dist(Target) < 4f && CanAttack(out _) &&
            AlivePlayers().All(o => o == Target || o == Copied || Vector3.Distance(o.transform.position, transform.position) > 18f))
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
        wantSprint = true;
        if (lookPlayer != Target) LookAtPlayer(Target, 30f);
        GoTo(Target.transform.position, Target.isInsideFactory);
    }

    private void FleeFrom(PlayerControllerB by, float seconds)
    {
        fleeFrom = by;
        fleeMonster = null;
        fleeStyle = FleeStyle.Run;
        fleeOrigin = by.transform.position;
        fleeUntil = Time.time + seconds;
        SetBehaviour(Behaviour.Flee, by, seconds);
    }

    private void Flee()
    {
        if (fleeMonster != null && fleeMonster.isEnemyDead) fleeMonster = null;
        if (!Fleeing)
        {
            // No reflex: the LLM chose to run, from the player it named or from whatever monster is around.
            fleeStyle = FleeStyle.Run;
            fleeFrom = null;
            if (Target != null) fleeMonster = null;
            else if (fleeMonster == null || Vector3.Distance(fleeMonster.transform.position, transform.position) > 40f) fleeMonster = NearestMonster();
        }
        var fromPlayer = fleeFrom ?? Target;
        Vector3 danger = fleeMonster != null ? fleeMonster.transform.position
            : fromPlayer != null ? fromPlayer.transform.position
            : fleeOrigin;
        switch (fleeStyle)
        {
            case FleeStyle.BackAway:
                faceLookWhileMoving = true;
                speedCap = Cfg.WalkSpeed.Value * 0.6f;
                if (fleeMonster != null && !lookPoint.HasValue) LookAtPoint(MonsterPoint(fleeMonster), 1.5f);
                break;
            case FleeStyle.Sneak:
                wantCrouch = true;
                break;
            default:
                wantSprint = true;
                break;
        }
        if (Time.time >= nextFleePick || FlatDistance(transform.position, fleePoint) < 2f)
        {
            nextFleePick = Time.time + (fleeStyle == FleeStyle.Run ? 2.5f : 4f);
            fleePoint = PickFleePoint(danger, fleeStyle == FleeStyle.Run ? 35f : 15f);
        }
        Masked.SetDestinationToPosition(fleePoint);
    }

    // Away from the danger, never past it, and toward the others when they're around (players run together).
    private Vector3 PickFleePoint(Vector3 danger, float maxDistance)
    {
        Vector3 me = transform.position;
        Vector3 away = me - danger;
        away.y = 0f;
        away = away.sqrMagnitude > 0.01f ? away.normalized : -transform.forward;
        float myDanger = Vector3.Distance(me, danger);
        var crew = Crew(40f).Select(p => p.transform.position).ToList();
        Vector3 best = me;
        float bestScore = float.MinValue;
        var nodes = Masked.allAINodes;
        if (nodes != null)
        {
            foreach (var node in nodes)
            {
                if (node == null) continue;
                Vector3 p = node.transform.position;
                float fromMe = Vector3.Distance(p, me);
                if (fromMe < 5f || fromMe > maxDistance) continue;
                float fromDanger = Vector3.Distance(p, danger);
                if (fromDanger < myDanger + 3f) continue;
                Vector3 dir = p - me;
                dir.y = 0f;
                float along = Vector3.Dot(dir.normalized, away);
                if (along < 0.2f) continue;
                float score = fromDanger + along * 10f - fromMe * 0.3f + Random.Range(-3f, 3f);
                if (crew.Count > 0) score -= crew.Min(c => Vector3.Distance(c, p)) * 0.2f;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = p;
                }
            }
        }
        if (bestScore > float.MinValue) return best;
        var far = Masked.ChooseFarthestNodeFromPosition(danger);
        return far != null ? far.position : me + away * 8f;
    }
}
