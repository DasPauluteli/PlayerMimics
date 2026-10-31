using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace PlayerMimics;

// BetterEmotes is optional. Everything is read by reflection so the mod loads without it.
internal static class BetterEmotesCompat
{
    private static bool looked;
    private static FieldInfo? othersField;
    private static FieldInfo? signPrefabField;
    private static FieldInfo? clapsField;

    internal static bool Installed => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("BetterEmotes");

    private static void Look()
    {
        if (looked) return;
        looked = true;
        if (!Installed) return;
        try
        {
            othersField = AccessTools.Field(AccessTools.TypeByName("BetterEmote.Patches.EmotePatch"), "others");
            var localPlayer = AccessTools.TypeByName("BetterEmote.AssetScripts.LocalPlayer");
            signPrefabField = AccessTools.Field(localPlayer, "SignPrefab");
            clapsField = AccessTools.Field(AccessTools.TypeByName("BetterEmote.AssetScripts.CustomAudioAnimationEvent"), "claps");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"BetterEmotes is installed but its emotes couldn't be found: {e.Message}");
        }
    }

    // The animator controller BetterEmotes gives other players: the player animations plus all its emotes.
    internal static RuntimeAnimatorController? OthersController
    {
        get
        {
            Look();
            return othersField?.GetValue(null) as RuntimeAnimatorController;
        }
    }

    internal static GameObject? SignPrefab
    {
        get
        {
            Look();
            return signPrefabField?.GetValue(null) as GameObject;
        }
    }

    internal static AudioClip? RandomClap()
    {
        Look();
        if (clapsField?.GetValue(null) is AudioClip[] claps && claps.Length > 0)
        {
            return claps[UnityEngine.Random.Range(0, claps.Length)];
        }
        return null;
    }

    // Emote numbers used by the player animator. Vanilla only has dance (1) and point (2).
    internal static readonly string[] EmoteNames =
        { "", "dance", "point", "middle_finger", "clap", "shy", "griddy", "twerk", "salute", "prisyadka", "sign" };

    internal const int Sign = 10;

    internal static int EmoteNumber(string name)
    {
        int n = Array.IndexOf(EmoteNames, name);
        if (n <= 0) return 0;
        return n <= 2 || Installed ? n : 0;
    }

    internal static string[] Available() =>
        Installed ? EmoteNames[1..] : new[] { "dance", "point" };
}
