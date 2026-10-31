using System.Collections.Generic;
using GameNetcodeStuff;
using TMPro;
using UnityEngine;

namespace PlayerMimics;

// Runs on every client. Makes a Masked look, animate and sound like the player it copies.
internal sealed class MimicDisguise : MonoBehaviour
{
    internal static readonly List<MimicDisguise> All = new();

    internal MaskedPlayerEnemy Masked = null!;
    internal PlayerControllerB Player = null!;
    internal bool Revived;
    internal bool Revealed;
    internal MimicVisualState State;
    internal string DebugText = "";

    private Animator animator = null!;
    private RuntimeAnimatorController? maskedController;
    private bool usingPlayerAnimations;
    private readonly List<GameObject> hiddenMasks = new();

    private Transform? nameTagRoot;
    private Transform? nameTagBillboard;
    private CanvasGroup? nameTagAlpha;

    private MeshRenderer? sign;
    private TextMeshPro? signText;

    private Vector3 lastPosition;
    private Vector3 smoothedVelocity;
    private float emoteWeight;
    private float lastFootstep;

    internal bool Moving => smoothedVelocity.magnitude > 0.35f;

    internal static MimicDisguise? Of(EnemyAI? enemy) =>
        enemy == null ? null : enemy.GetComponent<MimicDisguise>();

    internal void Apply(PlayerControllerB player, bool revived)
    {
        Masked = GetComponent<MaskedPlayerEnemy>();
        Player = player;
        Revived = revived;
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

        if (Masked.IsOwner && Masked.agent != null)
        {
            Masked.agent.acceleration = 40f;
            Masked.agent.angularSpeed = 900f;
        }
        lastPosition = transform.position;
        Plugin.Log.LogInfo($"Masked #{Masked.thisEnemyIndex} now copies {player.playerUsername}{(revived ? " (revived)" : "")}");
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
            if (t.name.StartsWith("HeadMask") && t.gameObject.activeSelf)
            {
                hiddenMasks.Add(t.gameObject);
                t.gameObject.SetActive(false);
            }
        }
        if (Masked.maskTypes != null)
        {
            foreach (var mask in Masked.maskTypes)
            {
                if (mask != null && mask.activeSelf)
                {
                    hiddenMasks.Add(mask);
                    mask.SetActive(false);
                }
            }
        }
        if (Masked.maskEyesGlowLight != null) Masked.maskEyesGlowLight.enabled = false;
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
        RuntimeAnimatorController? controller = BetterEmotesCompat.OthersController ?? StartOfRound.Instance.otherClientsAnimatorController;
        if (controller == null) return;
        maskedController = animator.runtimeAnimatorController;
        animator.runtimeAnimatorController = controller;
        animator.SetFloat("animationSpeed", 1f);
        usingPlayerAnimations = true;
        if (animator.GetComponent<MimicAnimationEvents>() == null)
        {
            animator.gameObject.AddComponent<MimicAnimationEvents>().Disguise = this;
        }
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
            text.text = player.playerUsername;
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

    internal void SetState(MimicVisualState s)
    {
        State = s;
        if (s.Hostile && !Revealed && Cfg.RevealOnAttack.Value)
        {
            Reveal();
        }
    }

    // Drop the disguise: mask and Masked animations back on, no name tag.
    internal void Reveal()
    {
        Revealed = true;
        foreach (var mask in hiddenMasks)
        {
            if (mask != null) mask.SetActive(true);
        }
        if (usingPlayerAnimations && maskedController != null)
        {
            animator.runtimeAnimatorController = maskedController;
            usingPlayerAnimations = false;
        }
        if (sign != null) sign.enabled = false;
        SetNameTagVisible(false);
    }

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

        if (usingPlayerAnimations && !Masked.isEnemyDead)
        {
            Animate(dt);
        }
        UpdateNameTag(dt);
    }

    private void Animate(float dt)
    {
        bool moving = Moving && State.Emote == 0;
        animator.SetBool("Walking", moving);
        animator.SetBool("Sprinting", moving && State.Sprinting);
        animator.SetBool("crouching", State.Crouching);
        float forward = Vector3.Dot(smoothedVelocity, transform.forward);
        animator.SetFloat("animationSpeed", moving && forward < -0.2f ? -1f : 1f);

        animator.SetInteger("emoteNumber", State.Emote);
        emoteWeight = Mathf.Lerp(emoteWeight, State.Emote > 0 ? 1f : 0f, 10f * dt);
        SetLayerWeight("EmotesNoArms", emoteWeight);
        SetLayerWeight("UpperBodyEmotes", 0f);
        SetLayerWeight("HoldingItemsRightHand", 0f);
        SetLayerWeight("HoldingItemsBothHands", 0f);

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

    private void UpdateNameTag(float dt)
    {
        if (nameTagRoot == null || nameTagAlpha == null) return;
        var local = GameNetworkManager.Instance?.localPlayerController;
        bool can = !Revealed && Masked.enemyEnabled && !Masked.isEnemyDead && local != null && !local.isPlayerDead;
        if (!can)
        {
            SetNameTagVisible(false);
            return;
        }
        // Same rule as players: you see the name while looking at them from close by, then it fades.
        var cam = local!.gameplayCamera.transform;
        int mask = StartOfRound.Instance.collidersAndRoomMaskAndDefault | (1 << LayerMask.NameToLayer("Enemies"));
        if (Physics.Raycast(cam.position, cam.forward, out RaycastHit hit, Cfg.NameTagRange.Value, mask, QueryTriggerInteraction.Collide) &&
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
        // Player volumes: 0.9 sprinting, 0.6 walking.
        float volume = State.Sprinting ? 0.9f : 0.6f;
        Masked.movementAudio.pitch = Random.Range(0.93f, 1.07f);
        Masked.movementAudio.PlayOneShot(clips[clip], volume);
        WalkieTalkie.TransmitOneShotAudio(Masked.movementAudio, clips[clip], volume);
        RoundManager.Instance.PlayAudibleNoise(transform.position, State.Sprinting ? 22f : 17f, State.Sprinting ? 0.6f : 0.4f, 0,
            Masked.isInsidePlayerShip && StartOfRound.Instance.hangarDoorsClosed, 7);
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
