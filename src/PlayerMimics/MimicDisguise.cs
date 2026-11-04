using System.Collections;
using System.Collections.Generic;
using GameNetcodeStuff;
using TMPro;
using UnityEngine;
using UnityEngine.Animations.Rigging;

namespace PlayerMimics;

// Runs on every client. Makes a Masked look, animate and sound like the player it copies. It never shows its mask,
// not even while it attacks.
internal sealed class MimicDisguise : MonoBehaviour
{
    internal static readonly List<MimicDisguise> All = new();

    internal MaskedPlayerEnemy Masked = null!;
    internal PlayerControllerB Player = null!;
    internal bool Revived;
    internal string Name = ""; // what it goes by: the copied player's name unless Debug > MimicName is set
    internal bool Renamed;
    internal MimicVisualState State;
    internal string DebugText = "";
    internal MimicInventory? Inventory;

    private Animator animator = null!;
    private RuntimeAnimatorController? maskedController;
    private RuntimeAnimatorController? playerController;
    private bool usingPlayerAnimations;
    private bool inKillPose;
    private readonly List<GameObject> hiddenMasks = new();

    private Transform? nameTagRoot;
    private Transform? nameTagBillboard;
    private CanvasGroup? nameTagAlpha;

    private MeshRenderer? sign;
    private TextMeshPro? signText;

    private Vector3 lastPosition;
    private Vector3 smoothedVelocity;
    private float emoteWeight;
    private int playingEmote;
    private float lastFootstep;
    private bool wasCrouching;
    private float pitch;
    private Coroutine? jumpRoutine;
    private float jumpHeight;
    private float appliedJump;
    private float noiseSeed;
    private readonly HashSet<string> animParams = new();
    private float holdWeight;
    // Arm IK the Masked model brings along. The player clips pose the arms on their own; an IK still pulling a hand
    // toward its old target left the right arm bent forward as if holding something.
    private readonly List<IRigConstraint> armRigs = new();
    private float grabValidateAt = -1f;

    internal bool Moving => smoothedVelocity.magnitude > 0.35f;

    internal static MimicDisguise? Of(EnemyAI? enemy) =>
        enemy == null ? null : enemy.GetComponent<MimicDisguise>();

    internal void Apply(PlayerControllerB player, bool revived, string name)
    {
        Masked = GetComponent<MaskedPlayerEnemy>();
        Player = player;
        Revived = revived;
        Name = string.IsNullOrWhiteSpace(name) ? player.playerUsername : name.Trim();
        Renamed = Name != player.playerUsername;
        noiseSeed = Random.Range(0f, 100f);
        if (!All.Contains(this)) All.Add(this);
        animator = Masked.creatureAnimator;

        // Vanilla hides a Masked while the player it copies has a body lying around. Ours copy living players too.
        Masked.mimickingPlayer = player;
        Masked.allowSpawningWithoutPlayer = true;
        Masked.SetSuit(player.currentSuitID);
        // MoreCompany copies the player's cosmetics onto a Masked here.
        Masked.SetEnemyOutside(Masked.isOutside);

        HideMask();
        StopRadarSpin();
        foreach (var node in Masked.GetComponentsInChildren<ScanNodeProperties>(true))
        {
            node.gameObject.SetActive(false); // players can't be scanned
        }
        CopyFootstepAudio(player);
        UsePlayerAnimations();
        MakeNameTag(player);
        MakeSign();
        Inventory = GetComponent<MimicInventory>() ?? gameObject.AddComponent<MimicInventory>();
        Inventory.Init(this);
        CoronerCompat.OnDisguised(Name);

        // Position updates more often and smoother, so on other screens it moves like a player rather than in hops.
        Masked.syncMovementSpeed = 0.12f;
        Masked.updatePositionThreshold = 0.3f;
        if (Masked.IsOwner && Masked.agent != null)
        {
            Masked.agent.acceleration = 24f;
            Masked.agent.autoBraking = true;
            Masked.agent.stoppingDistance = 0.2f;
            Masked.agent.updateRotation = false; // the brain turns it like a player turns the camera
        }
        lastPosition = transform.position;
        Plugin.Log.LogInfo($"Masked #{Masked.thisEnemyIndex} now copies {player.playerUsername}{(Renamed ? $" as {Name}" : "")}" +
                           (revived ? " (revived)" : ""));
    }

    private void OnDestroy()
    {
        All.Remove(this);
        if (nameTagRoot != null) Destroy(nameTagRoot.gameObject);
    }

    // ---- setup

    private void HideMask()
    {
        foreach (var t in Masked.GetComponentsInChildren<Transform>(true))
        {
            if (t.name.StartsWith("HeadMask")) hiddenMasks.Add(t.gameObject);
        }
        if (Masked.maskTypes != null) hiddenMasks.AddRange(Masked.maskTypes);
        KeepMaskHidden();
    }

    // Vanilla turns masks and the eye glow on in a few places (mask type, kill animation); undo it every frame.
    private void KeepMaskHidden()
    {
        foreach (var mask in hiddenMasks)
        {
            if (mask != null && mask.activeSelf) mask.SetActive(false);
        }
        if (Masked.maskEyesGlowLight != null && Masked.maskEyesGlowLight.enabled) Masked.maskEyesGlowLight.enabled = false;
    }

    private void StopRadarSpin()
    {
        foreach (var t in Masked.GetComponentsInChildren<Transform>(true))
        {
            if (t.name.StartsWith("MapDot"))
            {
                var dotAnimator = t.GetComponent<Animator>();
                if (dotAnimator != null) dotAnimator.enabled = false;
                t.localRotation = Quaternion.identity;
            }
        }
    }

    private void CopyFootstepAudio(PlayerControllerB player)
    {
        var from = player.movementAudio;
        var to = Masked.movementAudio;
        if (from == null || to == null) return;
        to.outputAudioMixerGroup = from.outputAudioMixerGroup;
        to.rolloffMode = from.rolloffMode;
        to.minDistance = from.minDistance;
        to.maxDistance = from.maxDistance;
        to.spatialBlend = from.spatialBlend;
        if (from.rolloffMode == AudioRolloffMode.Custom)
        {
            to.SetCustomCurve(AudioSourceCurveType.CustomRolloff, from.GetCustomCurve(AudioSourceCurveType.CustomRolloff));
        }
    }

    private void UsePlayerAnimations()
    {
        if (animator == null) return;
        // The player clips are bound to bone paths under "metarig". The Masked rig has to match or the body breaks.
        if (animator.transform.Find("spine") == null)
        {
            Plugin.Log.LogWarning($"Masked rig doesn't match the player rig ({animator.transform.name}); keeping Masked animations");
            return;
        }
        // The controller a player uses for their own body reacts to Walking/Sprinting/crouching like we need. The one
        // the game uses for other players doesn't: it only plays the states each player's owner sends over the network.
        playerController = BetterEmotesCompat.LocalController ?? StartOfRound.Instance.localClientAnimatorController;
        if (playerController == null) return;
        maskedController = animator.runtimeAnimatorController;
        int rigs = SwitchController(playerController);
        usingPlayerAnimations = true;
        FindArmRigs();
        if (animator.GetComponent<MimicAnimationEvents>() == null)
        {
            animator.gameObject.AddComponent<MimicAnimationEvents>().Disguise = this;
        }
        Plugin.Log.LogInfo($"Masked #{Masked.thisEnemyIndex}: player animations on " +
                           $"({(BetterEmotesCompat.LocalController != null ? "BetterEmotes" : "vanilla")}, {rigs} rig(s) rebuilt)");
    }

    private void FindArmRigs()
    {
        armRigs.Clear();
        var found = new List<string>();
        foreach (var c in Masked.GetComponentsInChildren<MonoBehaviour>(true))
        {
            Transform? tip = c switch
            {
                TwoBoneIKConstraint two => two.data.tip,
                ChainIKConstraint chain => chain.data.tip,
                _ => null,
            };
            if (tip == null || c is not IRigConstraint rig) continue;
            string bone = tip.name.ToLowerInvariant();
            if (!bone.Contains("hand") && !bone.Contains("arm")) continue;
            armRigs.Add(rig);
            found.Add($"{c.name} -> {tip.name} ({rig.weight:0.##})");
        }
        if (found.Count > 0) Plugin.Log.LogInfo($"Masked #{Masked.thisEnemyIndex}: arm IK turned off: {string.Join(", ", found)}");
    }

    private int SwitchController(RuntimeAnimatorController controller)
    {
        animator.runtimeAnimatorController = controller;
        animator.Rebind();
        animator.SetFloat("animationSpeed", 1f);
        animParams.Clear();
        foreach (var p in animator.parameters) animParams.Add(p.name);
        // The Masked's look rig (Animation Rigging) is built on the old controller; without a rebuild it keeps driving
        // the bones with its own graph.
        int n = 0;
        foreach (var rig in Masked.GetComponentsInChildren<RigBuilder>(true))
        {
            rig.Clear();
            if (rig.Build()) n++;
        }
        return n;
    }

    private void MakeNameTag(PlayerControllerB player)
    {
        if (player.usernameBillboard == null || player.usernameCanvas == null) return;
        Transform canvas = player.usernameCanvas.transform;
        Transform billboard = player.usernameBillboard;
        // Copy whichever of the two contains the other, so the copy has both.
        Transform source = billboard.IsChildOf(canvas) ? canvas : billboard;
        string? billboardPath = RelativePath(source, billboard);

        var copy = Instantiate(source.gameObject, transform);
        copy.name = "PlayerMimicsNameTag";
        copy.transform.localPosition = player.transform.InverseTransformPoint(source.position);
        copy.transform.localRotation = Quaternion.identity;
        nameTagRoot = copy.transform;
        nameTagBillboard = billboardPath == null ? copy.transform : copy.transform.Find(billboardPath) ?? copy.transform;
        nameTagAlpha = copy.GetComponentInChildren<CanvasGroup>(true);
        foreach (var text in copy.GetComponentsInChildren<TextMeshProUGUI>(true))
        {
            text.text = Name;
            text.enabled = true;
        }
        if (nameTagAlpha != null) nameTagAlpha.alpha = 0f;
        SetNameTagVisible(false);
    }

    private static string? RelativePath(Transform root, Transform child)
    {
        if (child == root) return null;
        var parts = new List<string>();
        for (Transform? t = child; t != null && t != root; t = t.parent) parts.Insert(0, t.name);
        return string.Join("/", parts);
    }

    private void MakeSign()
    {
        var prefab = BetterEmotesCompat.SignPrefab;
        if (prefab == null || animator == null || !usingPlayerAnimations) return;
        var obj = Instantiate(prefab, animator.transform);
        obj.name = "Sign";
        obj.transform.localPosition = new Vector3(0.029f, -0.45f, 1.3217f);
        obj.transform.localRotation = Quaternion.Euler(65.556f, 180f, 180f);
        sign = obj.GetComponent<MeshRenderer>();
        var textObj = obj.transform.Find("Text");
        signText = textObj != null ? textObj.GetComponent<TextMeshPro>() : null;
        if (sign != null) sign.enabled = false;
        if (textObj != null) textObj.gameObject.SetActive(false);
    }

    // ---- state from the host

    internal void SetState(MimicVisualState s) => State = s;

    // ---- every frame

    private void Update()
    {
        if (Masked == null || Player == null) return;
        float dt = Mathf.Max(Time.deltaTime, 0.0001f);
        Vector3 velocity = (transform.position - lastPosition) / dt;
        lastPosition = transform.position;
        velocity.y = 0f;
        if (velocity.magnitude > 30f) velocity = Vector3.zero; // teleports
        smoothedVelocity = Vector3.Lerp(smoothedVelocity, velocity, 10f * dt);

        KeepMaskHidden();
        if (usingPlayerAnimations && !Masked.isEnemyDead)
        {
            UpdateKillPose();
            if (!inKillPose) Animate();
        }
        UpdateNameTag(dt);
    }

    // The kill itself uses the Masked's grab animation (the player rig has nothing like it), still without a mask.
    private void UpdateKillPose()
    {
        if (Masked.inKillAnimation && !inKillPose && maskedController != null)
        {
            inKillPose = true;
            SwitchController(maskedController);
            animator.SetBool("killing", true);
        }
        else if (!Masked.inKillAnimation && inKillPose && playerController != null)
        {
            inKillPose = false;
            SwitchController(playerController);
            HoldingPose();
        }
    }

    private void Animate()
    {
        bool moving = Moving && State.Emote == 0;
        // Same parameters the game sets on your own player body.
        Vector3 local = transform.InverseTransformDirection(smoothedVelocity);
        var dir = new Vector2(local.x, local.z);
        if (dir.sqrMagnitude > 0.0001f) dir.Normalize();
        animator.SetBool("Walking", moving);
        animator.SetBool("Sprinting", moving && State.Sprinting);
        animator.SetBool("Sideways", moving && Mathf.Abs(dir.y) < 0.3f);
        // The player's rule: moving mostly backwards or to the left plays the walk in reverse.
        animator.SetFloat("animationSpeed", moving && dir.y < 0.5f && dir.x < 0.5f ? -1f : 1f);
        if (State.Crouching && !wasCrouching) animator.SetTrigger("startCrouching");
        wasCrouching = State.Crouching;
        animator.SetBool("crouching", State.Crouching);
        animator.SetBool("hinderedMovement", false);

        // TooManyEmotes emotes (numbers from its Base up) are played by its own controller on top of this animator.
        int vanillaEmote = State.Emote < TooManyEmotesCompat.Base ? State.Emote : 0;
        if (State.Emote != playingEmote)
        {
            if (playingEmote >= TooManyEmotesCompat.Base) TooManyEmotesCompat.Stop(Masked);
            if (State.Emote >= TooManyEmotesCompat.Base) TooManyEmotesCompat.Play(Masked, State.Emote);
            playingEmote = State.Emote;
        }
        animator.SetInteger("emoteNumber", vanillaEmote);
        emoteWeight = Mathf.Lerp(emoteWeight, vanillaEmote > 0 ? 1f : 0f, 10f * Time.deltaTime);
        SetLayerWeight("EmotesNoArms", emoteWeight);
        SetLayerWeight("UpperBodyEmotes", 0f);
        // Doors, levers and the terminal play here for players, who fade it out after; it never plays for a Masked.
        SetLayerWeight("SpecialAnimations", 0f);
        foreach (var rig in armRigs) rig.weight = 0f;
        // The player's own blend for holding items: the right-hand layer, plus both hands for two-handed things.
        var held = Inventory != null ? Inventory.Held : null;
        holdWeight = Mathf.Lerp(holdWeight, held != null ? 1f : 0f, 25f * Time.deltaTime);
        SetLayerWeight("HoldingItemsRightHand", holdWeight);
        SetLayerWeight("HoldingItemsBothHands", held == null || held.itemProperties.twoHandedAnimation ? holdWeight : Mathf.Abs(holdWeight - 1f));
        if (grabValidateAt > 0f && Time.time >= grabValidateAt)
        {
            grabValidateAt = -1f;
            SetBool("GrabValidated", true);
        }

        if (sign != null)
        {
            bool showSign = State.Emote == BetterEmotesCompat.Sign;
            Transform? spine = sign.transform.parent.Find("spine");
            if (spine != null) sign.transform.localPosition = spine.localPosition;
            sign.enabled = showSign;
            if (signText != null)
            {
                signText.gameObject.SetActive(showSign);
                if (showSign && signText.text != State.SignText) signText.text = State.SignText ?? "";
            }
        }
    }

    private void SetLayerWeight(string layer, float weight)
    {
        int index = animator.GetLayerIndex(layer);
        if (index >= 0) animator.SetLayerWeight(index, weight);
    }

    // Item animations name their own bools (grabAnim); skip any this controller doesn't have.
    private void SetBool(string? name, bool value)
    {
        if (!string.IsNullOrEmpty(name) && animParams.Contains(name!)) animator.SetBool(name, value);
    }

    private void Trigger(string? name)
    {
        if (string.IsNullOrEmpty(name) || !animParams.Contains(name!)) return;
        animator.ResetTrigger(name);
        animator.SetTrigger(name);
    }

    // ---- holding items (MimicInventory), with the parameters the game sets on a player's body

    private bool CanAnimateItems => usingPlayerAnimations && !inKillPose && animator != null;

    internal void OnGrab(GrabbableObject item)
    {
        if (!CanAnimateItems) return;
        SetBool("GrabInvalidated", false);
        SetBool("GrabValidated", false);
        SetBool("cancelHolding", false);
        if (animParams.Contains("Throw")) animator.ResetTrigger("Throw");
        SetBool("Grab", true);
        SetBool(item.itemProperties.grabAnim, true);
        float grabTime = item.itemProperties.grabAnimationTime > 0f ? item.itemProperties.grabAnimationTime : 0.4f;
        grabValidateAt = Time.time + Mathf.Max(0.15f, grabTime - 0.1f);
    }

    internal void OnSwitch(GrabbableObject? old, GrabbableObject? now)
    {
        if (!CanAnimateItems) return;
        if (old != null) SetBool(old.itemProperties.grabAnim, false);
        if (now == null)
        {
            SetBool("Grab", false);
            SetBool("cancelHolding", true);
            return;
        }
        SetBool("Grab", true);
        SetBool(now.itemProperties.grabAnim, true);
        if (old != null)
        {
            if (now.itemProperties.twoHandedAnimation || old.itemProperties.twoHandedAnimation) Trigger("SwitchHoldAnimationTwoHanded");
            Trigger("SwitchHoldAnimation");
            Trigger(now.itemProperties.pocketAnim);
        }
        SetBool("GrabValidated", true);
        SetBool("cancelHolding", false);
    }

    internal void OnDrop(GrabbableObject? item)
    {
        grabValidateAt = -1f;
        if (!CanAnimateItems) return;
        SetBool("Grab", false);
        if (item != null) SetBool(item.itemProperties.grabAnim, false);
        SetBool("cancelHolding", true);
        Trigger("Throw");
    }

    // The controller came back after the kill pose with every parameter reset.
    private void HoldingPose()
    {
        var held = Inventory != null ? Inventory.Held : null;
        if (held == null) return;
        SetBool("Grab", true);
        SetBool(held.itemProperties.grabAnim, true);
        SetBool("GrabValidated", true);
        SetBool("cancelHolding", false);
    }

    // ---- doors

    // Went through an entrance (MimicItems.CrossDoor), on every client: where it comes out, the door sounds players
    // hear, and inside/outside for the AI and the items it carries.
    internal void UsedEntrance(EntranceTeleport? door, Vector3 pos, float yaw, bool outside)
    {
        var m = Masked;
        m.timeAtLastUsingEntrance = Time.realtimeSinceStartup;
        if (m.IsOwner && m.agent != null)
        {
            m.agent.enabled = false;
            transform.position = pos;
            m.agent.enabled = true;
        }
        else
        {
            transform.position = pos;
        }
        transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        m.serverPosition = pos;
        m.targetYRotation = yaw;
        lastPosition = pos;
        smoothedVelocity = Vector3.zero;
        m.SetEnemyOutside(outside);
        if (m.IsOwner && m.agent != null && m.agent.enabled) m.SetDestinationToPosition(pos);
        if (door != null)
        {
            if (door.exitScript == null) door.FindExitPoint();
            if (door.exitScript != null) door.PlayAudioAtTeleportPositions();
        }
        if (Inventory != null)
        {
            foreach (var (_, item) in Inventory.Items) item.isInFactory = !outside;
        }
    }

    // ---- head look

    // Replaces the vanilla look code. Vanilla kept its last vertical angle forever and counted sideways angle as
    // pitch, which left the head tilted up. This aims the head at the real height of what it looks at and eases back
    // to level, with a little drift like a mouse that's never quite still. The body turn is done by the brain on the
    // host and reaches everyone else through the normal rotation sync.
    internal void LookAtFocused()
    {
        var m = Masked;
        if (m.headTiltTarget == null) return;
        Vector3? point = null;
        if (!m.inSpecialAnimation)
        {
            if (m.lookAtPositionTimer > 0f)
            {
                m.lookAtPositionTimer -= Time.deltaTime;
                point = m.focusOnPosition;
            }
            else if (m.stareAtTransform != null && Vector3.Distance(m.stareAtTransform.position, transform.position) <= 80f)
            {
                point = m.stareAtTransform.position;
            }
        }
        float target = 0f;
        if (point.HasValue)
        {
            Vector3 d = point.Value - m.headTiltTarget.position;
            float horizontal = new Vector2(d.x, d.z).magnitude;
            // Positive x is down, same as headTiltTarget.LookAt in vanilla's stare code.
            target = Mathf.Clamp(-Mathf.Atan2(d.y, Mathf.Max(horizontal, 0.3f)) * Mathf.Rad2Deg, -45f, 45f);
        }
        target += (Mathf.PerlinNoise(Time.time * 0.3f, noiseSeed) - 0.5f) * 7f;
        pitch = Mathf.Lerp(pitch, target, 6f * Time.deltaTime);
        m.headTiltTarget.localEulerAngles = new Vector3(pitch, 0f, 0f);
    }

    // ---- name tag

    private void UpdateNameTag(float dt)
    {
        if (nameTagRoot == null || nameTagAlpha == null) return;
        var local = GameNetworkManager.Instance?.localPlayerController;
        bool can = Masked.enemyEnabled && !Masked.isEnemyDead && local != null && !local.isPlayerDead;
        if (!can)
        {
            SetNameTagVisible(false);
            return;
        }
        // Same rule as players: you see the name while looking at them from close by, then it fades.
        float range = HostServices.IsHost ? Cfg.NameTagRange.Value : ModNetwork.HostNameTagRange;
        var cam = local!.gameplayCamera.transform;
        int mask = StartOfRound.Instance.collidersAndRoomMaskAndDefault | (1 << LayerMask.NameToLayer("Enemies"));
        if (Physics.Raycast(cam.position, cam.forward, out RaycastHit hit, range, mask, QueryTriggerInteraction.Collide) &&
            hit.collider.transform.IsChildOf(transform))
        {
            SetNameTagVisible(true);
            nameTagAlpha.alpha = 1f;
        }
        else if (nameTagAlpha.alpha > 0f)
        {
            nameTagAlpha.alpha -= dt;
        }
        else
        {
            SetNameTagVisible(false);
            return;
        }
        if (nameTagBillboard != null) nameTagBillboard.LookAt(local.localVisorTargetPoint);
    }

    private void SetNameTagVisible(bool visible)
    {
        if (nameTagRoot != null && nameTagRoot.gameObject.activeSelf != visible)
        {
            nameTagRoot.gameObject.SetActive(visible);
        }
    }

    // ---- sounds from the player animations

    internal void Footstep()
    {
        if (!Moving || Masked.isEnemyDead || !Masked.enemyEnabled || Time.time - lastFootstep < 0.12f) return;
        lastFootstep = Time.time;
        var surfaces = StartOfRound.Instance.footstepSurfaces;
        Masked.GetMaterialStandingOn();
        var clips = surfaces[Masked.currentFootstepSurfaceIndex].clips;
        if (clips.Length == 0) return;
        int clip = Random.Range(0, clips.Length);
        if (clip == Masked.previousFootstepClip) clip = (clip + 1) % clips.Length;
        Masked.previousFootstepClip = clip;
        // Player volumes: 0.9 sprinting, 0.6 walking; crouching is quieter.
        float volume = State.Sprinting ? 0.9f : State.Crouching ? 0.35f : 0.6f;
        Masked.movementAudio.pitch = Random.Range(0.93f, 1.07f);
        Masked.movementAudio.PlayOneShot(clips[clip], volume);
        WalkieTalkie.TransmitOneShotAudio(Masked.movementAudio, clips[clip], volume);
        // Monsters that hunt by sound hear it like a player's step.
        RoundManager.Instance.PlayAudibleNoise(transform.position, State.Sprinting ? 22f : State.Crouching ? 8f : 17f,
            State.Sprinting ? 0.6f : State.Crouching ? 0.2f : 0.4f, 0, Masked.isInsidePlayerShip && StartOfRound.Instance.hangarDoorsClosed, 7);
    }

    // ---- jumping (the host sends it when its player's habits include a jump)

    // The player jump: a short crouch before take-off, then up and down again. Only the model moves; the Masked
    // itself stays on the ground for the AI and the network.
    internal void Jump()
    {
        if (!usingPlayerAnimations || inKillPose || Masked.isEnemyDead || State.Crouching) return;
        if (jumpRoutine != null) StopCoroutine(jumpRoutine);
        jumpRoutine = StartCoroutine(JumpRoutine());
    }

    private IEnumerator JumpRoutine()
    {
        SetBool("Jumping", true);
        PlayOneShot(JumpClip());
        yield return new WaitForSeconds(0.15f);
        float v = 6.5f; // about 0.9 m up, half a second in the air
        jumpHeight = 0f;
        do
        {
            yield return null;
            float dt = Time.deltaTime;
            jumpHeight = Mathf.Max(0f, jumpHeight + v * dt);
            v -= 24f * dt;
        } while (jumpHeight > 0f && !Masked.isEnemyDead && !inKillPose);
        jumpHeight = 0f;
        SetBool("Jumping", false);
        PlayOneShot(StartOfRound.Instance.playerHitGroundSoft);
        jumpRoutine = null;
    }

    // Lifts the model by the change since last frame, so it lands exactly where it started.
    private void LateUpdate()
    {
        if (animator == null || animator.transform == transform || Mathf.Approximately(jumpHeight, appliedJump)) return;
        animator.transform.position += Vector3.up * (jumpHeight - appliedJump);
        appliedJump = jumpHeight;
    }

    private AudioClip? JumpClip()
    {
        var unlockables = StartOfRound.Instance.unlockablesList.unlockables;
        int suit = Player != null ? Player.currentSuitID : -1;
        if (suit >= 0 && suit < unlockables.Count && unlockables[suit].jumpAudio != null) return unlockables[suit].jumpAudio;
        return StartOfRound.Instance.playerJumpSFX;
    }

    private void PlayOneShot(AudioClip? clip)
    {
        if (clip == null || Masked.movementAudio == null) return;
        Masked.movementAudio.pitch = 1f;
        Masked.movementAudio.PlayOneShot(clip);
        WalkieTalkie.TransmitOneShotAudio(Masked.movementAudio, clip);
    }

    internal void Clap()
    {
        var clip = BetterEmotesCompat.RandomClap();
        if (clip != null && Masked.movementAudio != null) Masked.movementAudio.PlayOneShot(clip);
    }
}

// Receives the animation events the player clips send (they call these by name on the animator's object).
internal sealed class MimicAnimationEvents : MonoBehaviour
{
    internal MimicDisguise? Disguise;

    public void PlayFootstepServer() => Disguise?.Footstep();
    public void PlayFootstepLocal() => Disguise?.Footstep();
    public void PlayFootstepSound() => Disguise?.Footstep();
    public void PlayClapSound() => Disguise?.Clap();
    public void LimpForward() { }
    public void LockArmsToCamera() { }
    public void UnlockArmsFromCamera() { }
}
