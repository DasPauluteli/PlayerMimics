using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations.Rigging;

namespace PlayerMimics;

// Animates a disguised Masked exactly the way the game animates another player on your screen.
//
// The game has two controllers for the player body. Your own body runs the "local" one, which reacts to parameters
// (Walking, crouching, Grab...). Everyone else's body on your screen runs the "others" one, which only plays the
// states their owner's local controller is in, sent over the network and crossfaded in. The local one also carries
// first-person tweaks that look wrong from the outside.
//
// A mimic does the same without the network: a hidden driver animator runs the local controller and gets the
// parameters; the visible body runs the others controller and copies the driver's states.
internal sealed class MimicAnimator
{
    private const float StateFade = 0.1f; // what UpdatePlayerAnimationClientRpc uses

    private readonly MimicDisguise disguise;
    private readonly Animator body;
    private readonly Animator driver;
    private readonly RuntimeAnimatorController maskedController;
    private readonly RuntimeAnimatorController othersController;
    private readonly HashSet<string> driverParams = new();
    private readonly HashSet<string> bodyParams = new();
    private readonly Dictionary<string, int> bodyLayers = new();
    private readonly List<IRigConstraint> armRigs = new();
    private int[] shownStates = Array.Empty<int>();

    private float emoteWeight;
    private float holdWeight;
    private bool wasCrouching;
    private float grabValidateAt = -1f;

    internal bool OnPlayerBody { get; private set; }
    internal Animator Body => body;

    private MimicAnimator(MimicDisguise disguise, Animator body, Animator driver, RuntimeAnimatorController masked,
        RuntimeAnimatorController others)
    {
        this.disguise = disguise;
        this.body = body;
        this.driver = driver;
        maskedController = masked;
        othersController = others;
    }

    // An animator only knows its layers and parameters once Unity has set it up, which can be a frame after adding it.
    private bool DriverReady()
    {
        if (driverParams.Count > 0) return true;
        if (!driver.isInitialized) return false;
        foreach (var p in driver.parameters) driverParams.Add(p.name);
        if (driverParams.Contains("animationSpeed")) driver.SetFloat("animationSpeed", 1f);
        return driverParams.Count > 0;
    }

    internal static MimicAnimator? Create(MimicDisguise disguise)
    {
        var masked = disguise.Masked;
        var body = masked.creatureAnimator;
        if (body == null || body.runtimeAnimatorController == null) return null;
        // The player clips are bound to bone paths under "metarig". The Masked rig has to match or the body breaks.
        if (body.transform.Find("spine") == null)
        {
            Plugin.Log.LogWarning($"Masked rig doesn't match the player rig ({body.transform.name}); keeping Masked animations");
            return null;
        }
        // BetterEmotes swaps both controllers for its own (same animations plus its emotes); use its pair or neither.
        var local = BetterEmotesCompat.LocalController;
        var others = BetterEmotesCompat.OthersController;
        string source = "BetterEmotes";
        if (local == null || others == null)
        {
            local = StartOfRound.Instance.localClientAnimatorController;
            others = StartOfRound.Instance.otherClientsAnimatorController;
            source = "vanilla";
        }
        if (local == null || others == null) return null;

        // No bones under it, so the driver moves nothing; it only runs the state machine and the animation events
        // (footsteps, claps).
        var go = new GameObject("PlayerMimicsAnimationDriver");
        go.transform.SetParent(masked.transform, false);
        var driver = go.AddComponent<Animator>();
        driver.runtimeAnimatorController = local;
        driver.applyRootMotion = false;
        driver.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        go.AddComponent<MimicAnimationEvents>().Disguise = disguise;

        var anim = new MimicAnimator(disguise, body, driver, body.runtimeAnimatorController, others);
        anim.FindArmRigs();
        anim.ShowPlayerBody();
        // The body's clips send the same events; they'd double every footstep.
        if (body.GetComponent<MimicAnimationEvents>() == null) body.gameObject.AddComponent<MimicAnimationEvents>();
        Plugin.Log.LogInfo($"Masked #{masked.thisEnemyIndex}: player animations on ({source}, {anim.armRigs.Count} arm IK)");
        return anim;
    }

    internal void Destroy()
    {
        if (driver != null) UnityEngine.Object.Destroy(driver.gameObject);
    }

    // ---- switching the body between the player animations and the Masked's own (kill grab, death fall)

    internal void ShowPlayerBody()
    {
        SetBodyController(othersController);
        OnPlayerBody = true;
        Array.Clear(shownStates, 0, shownStates.Length);
        // The body starts over in its default states; bring it to the driver's parameters before copying states.
        foreach (var p in body.parameters)
        {
            if (!driverParams.Contains(p.name)) continue;
            switch (p.type)
            {
                case AnimatorControllerParameterType.Bool: body.SetBool(p.name, driver.GetBool(p.name)); break;
                case AnimatorControllerParameterType.Int: body.SetInteger(p.name, driver.GetInteger(p.name)); break;
                case AnimatorControllerParameterType.Float: body.SetFloat(p.name, driver.GetFloat(p.name)); break;
            }
        }
    }

    internal void ShowMaskedBody()
    {
        SetBodyController(maskedController);
        OnPlayerBody = false;
    }

    private void SetBodyController(RuntimeAnimatorController controller)
    {
        body.runtimeAnimatorController = controller;
        body.Rebind();
        bodyParams.Clear();
        foreach (var p in body.parameters) bodyParams.Add(p.name);
        bodyLayers.Clear();
        // The Masked's look rig (Animation Rigging) is built on the old controller; without a rebuild it keeps
        // driving the bones with its own graph.
        foreach (var rig in disguise.Masked.GetComponentsInChildren<RigBuilder>(true))
        {
            rig.Clear();
            rig.Build();
        }
    }

    // The arm IK of "Rig 1" (the same rig players have). Players keep it on all the time; the clips move its targets.
    // Without it the arms hang back in the raw bone pose.
    private void FindArmRigs()
    {
        armRigs.Clear();
        foreach (var c in disguise.Masked.GetComponentsInChildren<ChainIKConstraint>(true))
        {
            var tip = c.data.tip;
            if (tip == null) continue;
            string bone = tip.name.ToLowerInvariant();
            if (bone.Contains("hand") || bone.Contains("arm")) armRigs.Add(c);
        }
    }

    // ---- parameters: set on the driver, and on the body too when it has them, like the game sets some of them on
    // every client (emotes, grabbing, throwing)

    internal void Bool(string? name, bool value)
    {
        if (string.IsNullOrEmpty(name)) return;
        if (DriverReady() && driverParams.Contains(name!)) driver.SetBool(name, value);
        if (OnPlayerBody && bodyParams.Contains(name!)) body.SetBool(name, value);
    }

    internal void Float(string name, float value)
    {
        if (DriverReady() && driverParams.Contains(name)) driver.SetFloat(name, value);
        if (OnPlayerBody && bodyParams.Contains(name)) body.SetFloat(name, value);
    }

    internal void Integer(string name, int value)
    {
        if (DriverReady() && driverParams.Contains(name)) driver.SetInteger(name, value);
        if (OnPlayerBody && bodyParams.Contains(name)) body.SetInteger(name, value);
    }

    internal void Trigger(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        if (DriverReady() && driverParams.Contains(name!))
        {
            driver.ResetTrigger(name);
            driver.SetTrigger(name);
        }
        if (OnPlayerBody && bodyParams.Contains(name!))
        {
            body.ResetTrigger(name);
            body.SetTrigger(name);
        }
    }

    private void ResetTrigger(string name)
    {
        if (DriverReady() && driverParams.Contains(name)) driver.ResetTrigger(name);
        if (OnPlayerBody && bodyParams.Contains(name)) body.ResetTrigger(name);
    }

    // ---- every frame, while the body shows the player animations

    // What PlayerControllerB does for its own body each frame, from what the mimic is doing.
    internal void Tick(Vector3 velocity, bool moving, MimicVisualState state, GrabbableObject? held)
    {
        if (!OnPlayerBody || !DriverReady()) return;
        float dt = Time.deltaTime;
        bool emoting = state.Emote > 0 && state.Emote < TooManyEmotesCompat.Base;
        moving &= state.Emote == 0;

        Vector3 local = disguise.transform.InverseTransformDirection(velocity);
        var dir = new Vector2(local.x, local.z);
        if (dir.sqrMagnitude > 0.0001f) dir.Normalize();
        Bool("Walking", moving);
        Bool("Sprinting", moving && state.Sprinting);
        Bool("Sideways", moving && Mathf.Abs(dir.y) < 0.3f);
        // The player's rule: moving mostly backwards or to the left plays the walk in reverse.
        Float("animationSpeed", moving && dir.y < 0.5f && dir.x < 0.5f ? -1f : 1f);
        if (state.Crouching && !wasCrouching) Trigger("startCrouching");
        wasCrouching = state.Crouching;
        Bool("crouching", state.Crouching);
        Bool("hinderedMovement", false);
        Integer("emoteNumber", emoting ? state.Emote : 0);

        if (grabValidateAt > 0f && Time.time >= grabValidateAt)
        {
            grabValidateAt = -1f;
            Bool("GrabValidated", true);
        }

        CopyDriverStates();
        Weights(dt, emoting, held);
    }

    // UpdatePlayerAnimationClientRpc, run locally every frame: the body crossfades into each state the driver enters.
    // A state the driver is fading into counts already, so the body doesn't lag a whole transition behind.
    private void CopyDriverStates()
    {
        if (driverParams.Contains("animationSpeed") && bodyParams.Contains("animationSpeed"))
        {
            float speed = driver.GetFloat("animationSpeed");
            if (body.GetFloat("animationSpeed") != speed) body.SetFloat("animationSpeed", speed);
        }
        if (shownStates.Length != driver.layerCount) shownStates = new int[driver.layerCount];
        for (int i = 0; i < shownStates.Length; i++)
        {
            var info = driver.IsInTransition(i) ? driver.GetNextAnimatorStateInfo(i) : driver.GetCurrentAnimatorStateInfo(i);
            int hash = info.fullPathHash;
            if (hash == 0 || hash == shownStates[i]) continue;
            shownStates[i] = hash;
            for (int l = 0; l < body.layerCount; l++)
            {
                if (!body.HasState(l, hash)) continue;
                body.CrossFadeInFixedTime(hash, StateFade, l);
                break;
            }
        }
    }

    // Layer weights and IK as PlayerControllerB.Update sets them on every client.
    private void Weights(float dt, bool emoting, GrabbableObject? held)
    {
        // Hands up against a wall only shows on your own body; other players never do it on your screen.
        SetLayerWeight("UpperBodyEmotes", 0f);
        emoteWeight = Mathf.Lerp(emoteWeight, emoting ? 1f : 0f, 10f * dt);
        SetLayerWeight("EmotesNoArms", emoteWeight);
        holdWeight = Mathf.Lerp(holdWeight, held != null ? 1f : 0f, 25f * dt);
        SetLayerWeight("HoldingItemsRightHand", holdWeight);
        SetLayerWeight("HoldingItemsBothHands",
            held == null || held.itemProperties.twoHandedAnimation ? holdWeight : Mathf.Abs(holdWeight - 1f));
        // Doors, levers and the terminal; a mimic never plays them.
        SetLayerWeight("SpecialAnimations", 0f);
        foreach (var rig in armRigs) rig.weight = 1f;
    }

    private void SetLayerWeight(string layer, float weight)
    {
        if (!bodyLayers.TryGetValue(layer, out int index))
        {
            index = body.GetLayerIndex(layer);
            bodyLayers[layer] = index;
        }
        if (index >= 0) body.SetLayerWeight(index, weight);
    }

    // ---- holding items (MimicInventory), with the parameters the game sets on a player's body

    internal void OnGrab(GrabbableObject item)
    {
        Bool("GrabInvalidated", false);
        Bool("GrabValidated", false);
        Bool("cancelHolding", false);
        ResetTrigger("Throw");
        Bool("Grab", true);
        Bool(item.itemProperties.grabAnim, true);
        float grabTime = item.itemProperties.grabAnimationTime > 0f ? item.itemProperties.grabAnimationTime : 0.4f;
        grabValidateAt = Time.time + Mathf.Max(0.15f, grabTime - 0.1f);
    }

    internal void OnSwitch(GrabbableObject? old, GrabbableObject? now)
    {
        if (old != null) Bool(old.itemProperties.grabAnim, false);
        if (now == null)
        {
            Bool("Grab", false);
            Bool("cancelHolding", true);
            return;
        }
        Bool("Grab", true);
        Bool(now.itemProperties.grabAnim, true);
        if (old != null)
        {
            if (now.itemProperties.twoHandedAnimation || old.itemProperties.twoHandedAnimation) Trigger("SwitchHoldAnimationTwoHanded");
            Trigger("SwitchHoldAnimation");
            Trigger(now.itemProperties.pocketAnim);
        }
        Bool("GrabValidated", true);
        Bool("cancelHolding", false);
    }

    internal void OnDrop(GrabbableObject? item)
    {
        grabValidateAt = -1f;
        Bool("Grab", false);
        if (item != null) Bool(item.itemProperties.grabAnim, false);
        Bool("cancelHolding", true);
        Trigger("Throw");
    }

    // ---- the Masked's own animations

    // On the Masked controller only (kill grab, death fall).
    internal void MaskedBool(string name, bool value)
    {
        if (!OnPlayerBody && bodyParams.Contains(name)) body.SetBool(name, value);
    }

    internal void StopDriver() => driver.enabled = false;
}
