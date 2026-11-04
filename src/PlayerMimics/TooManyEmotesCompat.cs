using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;

namespace PlayerMimics;

// TooManyEmotes (FlipMods) is optional and read by reflection. It already gives every Masked an emote controller that
// copies an emote onto the body. Its own rules for that follow the vanilla Masked (emote only while stopped and
// staring at someone, at random), so for a disguised Masked those are replaced: the brain picks the emote, it reaches
// every client in the synced emote number (Base + the emote's index, which is the same everywhere), and each client
// starts and stops it on its copy of the Masked.
internal static class TooManyEmotesCompat
{
    internal const int Base = 1000;
    private const int Offered = 24;

    private static bool looked;
    private static IList? allEmotes; // List<UnlockableEmote>, same order on every client
    private static IDictionary? maskedControllers; // MaskedPlayerEnemy -> EmoteControllerMaskedEnemy
    private static IDictionary? playerControllers; // PlayerControllerB -> EmoteControllerPlayer
    private static MethodInfo? unlockedFor; // SessionManager.GetUnlockedEmotes(string username)
    private static MethodInfo? perform, stop;
    private static FieldInfo? emoteName, requiresProp, isPose, isPerforming, performing, initialized, animatorField;
    private static PropertyInfo? loopable, timeNormalized;
    private static readonly Dictionary<string, int> byName = new();
    private static string[] names = Array.Empty<string>();

    internal static bool Installed => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("FlipMods.TooManyEmotes");

    private static bool Look()
    {
        if (looked) return allEmotes != null;
        looked = true;
        if (!Installed) return false;
        try
        {
            var emote = AccessTools.TypeByName("TooManyEmotes.UnlockableEmote");
            var controller = AccessTools.TypeByName("TooManyEmotes.EmoteController");
            emoteName = AccessTools.Field(emote, "emoteName");
            requiresProp = AccessTools.Field(emote, "requiresHeldProp");
            loopable = AccessTools.Property(emote, "loopable");
            isPose = AccessTools.Field(emote, "isPose");
            perform = AccessTools.Method(controller, "PerformEmote");
            stop = AccessTools.Method(controller, "StopPerformingEmote");
            isPerforming = AccessTools.Field(controller, "isPerformingEmote");
            performing = AccessTools.Field(controller, "performingEmote");
            initialized = AccessTools.Field(controller, "initialized");
            animatorField = AccessTools.Field(controller, "animator");
            timeNormalized = AccessTools.Property(controller, "currentAnimationTimeNormalized");
            allEmotes = AccessTools.Field(AccessTools.TypeByName("TooManyEmotes.EmotesManager"), "allUnlockableEmotes")
                ?.GetValue(null) as IList;
            maskedControllers = AccessTools.Field(AccessTools.TypeByName("TooManyEmotes.EmoteControllerMaskedEnemy"),
                "allMaskedEnemyEmoteControllers")?.GetValue(null) as IDictionary;
            playerControllers = AccessTools.Field(AccessTools.TypeByName("TooManyEmotes.EmoteControllerPlayer"),
                "allPlayerEmoteControllers")?.GetValue(null) as IDictionary;
            unlockedFor = AccessTools.Method(AccessTools.TypeByName("TooManyEmotes.SessionManager"), "GetUnlockedEmotes",
                new[] { typeof(string) });
            if (allEmotes == null || perform == null || stop == null || maskedControllers == null)
            {
                throw new MissingMemberException("emote list or controller methods");
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"TooManyEmotes is installed but its emotes couldn't be found: {e.Message}");
            allEmotes = null;
        }
        return allEmotes != null;
    }

    // "Thumbs up!" -> "thumbs_up", so the LLM can answer with it. Names BetterEmotes also has stay BetterEmotes'.
    private static string Simple(string name) =>
        string.Join("_", new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static void Index()
    {
        if (names.Length > 0 || !Look()) return;
        var list = new List<string>();
        for (int i = 0; i < allEmotes!.Count; i++)
        {
            var e = allEmotes[i];
            if (e == null || (requiresProp?.GetValue(e) is bool needsProp && needsProp)) continue;
            string n = Simple(emoteName?.GetValue(e) as string ?? "");
            if (n.Length == 0 || n.Length > 32 || byName.ContainsKey(n) || BetterEmotesCompat.EmoteNames.Contains(n)) continue;
            byName[n] = i;
            list.Add(n);
        }
        names = list.ToArray();
    }

    // Emotes to offer the LLM for this Masked: what the copied player has unlocked, a different handful each time.
    internal static IEnumerable<string> Available(PlayerControllerB copied)
    {
        Index();
        if (names.Length == 0) return Array.Empty<string>();
        IEnumerable<string> pool = names;
        try
        {
            if (unlockedFor?.Invoke(null, new object[] { copied.playerUsername }) is IList unlocked && unlocked.Count > 0)
            {
                var mine = new HashSet<string>(unlocked.Cast<object>().Select(e => Simple(emoteName?.GetValue(e) as string ?? "")));
                pool = names.Where(mine.Contains).ToArray();
            }
        }
        catch (Exception)
        {
            // Unlock lists are optional; every emote is fine.
        }
        return pool.OrderBy(_ => UnityEngine.Random.value).Take(Offered);
    }

    internal static int EmoteNumber(string name)
    {
        Index();
        return byName.TryGetValue(Simple(name), out int i) ? Base + i : 0;
    }

    internal static string? NameOf(int number)
    {
        Index();
        int i = number - Base;
        if (allEmotes == null || i < 0 || i >= allEmotes.Count) return null;
        return Simple(emoteName?.GetValue(allEmotes[i]) as string ?? "");
    }

    private static object? ControllerOf(MaskedPlayerEnemy masked) =>
        Look() && maskedControllers!.Contains(masked) ? maskedControllers[masked] : null;

    // On every client, when the synced emote number changes.
    internal static void Play(MaskedPlayerEnemy masked, int number)
    {
        var c = ControllerOf(masked);
        int i = number - Base;
        if (c == null || i < 0 || i >= allEmotes!.Count) return;
        try
        {
            perform!.Invoke(c, new[] { allEmotes[i], -1, (object)false });
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"TooManyEmotes emote failed on a Masked: {e.InnerException?.Message ?? e.Message}");
        }
    }

    internal static void Stop(MaskedPlayerEnemy masked)
    {
        var c = ControllerOf(masked);
        if (c == null || !(isPerforming?.GetValue(c) is bool on && on)) return;
        stop!.Invoke(c, null);
    }

    // The TooManyEmotes emote a player is doing, if any.
    internal static string? PlayerEmote(PlayerControllerB p)
    {
        if (!Look() || playerControllers == null || !playerControllers.Contains(p)) return null;
        var c = playerControllers[p];
        if (!(isPerforming?.GetValue(c) is bool on && on)) return null;
        var e = performing?.GetValue(c);
        return e == null ? null : Simple(emoteName?.GetValue(e) as string ?? "");
    }

    // ---- replacing its Masked rules for disguised Masked

    internal static void Patch(Harmony harmony)
    {
        if (!Look()) return;
        try
        {
            var patcher = AccessTools.TypeByName("TooManyEmotes.Patches.MaskedEnemyPatcher");
            var masked = AccessTools.TypeByName("TooManyEmotes.EmoteControllerMaskedEnemy");
            harmony.Patch(AccessTools.Method(patcher, "OnUpdate"), prefix: Hook(nameof(NoRandomEmotes)));
            harmony.Patch(AccessTools.Method(masked, "CanPerformEmote"), prefix: Hook(nameof(CanEmote)));
            harmony.Patch(AccessTools.Method(masked, "CheckIfShouldStopEmoting"), prefix: Hook(nameof(ShouldStop)));
            Plugin.Log.LogInfo($"TooManyEmotes support on ({allEmotes!.Count} emotes)");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"TooManyEmotes support is off: {e.Message}");
        }
    }

    private static HarmonyMethod Hook(string name) => new(AccessTools.Method(typeof(TooManyEmotesCompat), name));

    private static MimicDisguise? DisguiseOf(object controller) =>
        controller is Component c ? MimicDisguise.Of(c.GetComponentInParent<MaskedPlayerEnemy>()) : null;

    // Its random emoting at whoever a Masked stares at. The original's argument is named __instance, so it's read
    // through __args.
    private static bool NoRandomEmotes(object[] __args) =>
        !(__args.Length > 0 && __args[0] is MaskedPlayerEnemy m && MimicDisguise.Of(m) != null);

    private static bool CanEmote(object __instance, ref bool __result)
    {
        var d = DisguiseOf(__instance);
        if (d == null) return true;
        var animator = animatorField?.GetValue(__instance) as Animator;
        __result = initialized?.GetValue(__instance) is true && animator != null && animator.enabled &&
                   !d.Masked.isEnemyDead && !d.Masked.inKillAnimation;
        return false;
    }

    // Ends when the brain moves on (the synced number changes), when it dies or kills, or when a one-shot emote is done.
    private static bool ShouldStop(object __instance, ref bool __result)
    {
        var d = DisguiseOf(__instance);
        if (d == null) return true;
        if (!(isPerforming?.GetValue(__instance) is bool on && on))
        {
            __result = false;
            return false;
        }
        bool finished = false;
        var e = performing?.GetValue(__instance);
        if (e != null && loopable?.GetValue(e) is false && isPose?.GetValue(e) is false &&
            timeNormalized?.GetValue(__instance) is float t)
        {
            finished = t >= 1f;
        }
        __result = e == null || finished || d.Masked.isEnemyDead || d.Masked.inKillAnimation || d.State.Emote < Base;
        return false;
    }
}
