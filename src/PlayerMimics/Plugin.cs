using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PlayerMimics;

[BepInPlugin(MyPluginInfo.Id, MyPluginInfo.Name, MyPluginInfo.Version)]
[BepInDependency("BetterEmotes", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("me.swipez.melonloader.morecompany", BepInDependency.DependencyFlags.SoftDependency)]
[BepInProcess("Lethal Company.exe")]
public class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log { get; private set; } = null!;
    internal static string AudioDumpDir => Path.Combine(Paths.BepInExRootPath, "PlayerMimics", "audio");

    private void Awake()
    {
        Log = Logger;
        Cfg.Bind(Config);
        ModNetworkSetup.Install();
        if (!Cfg.Enabled.Value)
        {
            Log.LogInfo($"{MyPluginInfo.Name} is turned off in its config");
            return;
        }
        new Harmony(MyPluginInfo.Id).PatchAll(typeof(Plugin).Assembly);

        // One object for the work that isn't tied to a Masked: consent popup, voice capture, debug keys, host services.
        var root = new GameObject("PlayerMimics") { hideFlags = HideFlags.HideAndDontSave };
        DontDestroyOnLoad(root);
        root.AddComponent<HostServices>();
        root.AddComponent<ConsentPopup>();
        root.AddComponent<VoiceCapture>();
        root.AddComponent<DebugTools>();
        Log.LogInfo($"{MyPluginInfo.Name} {MyPluginInfo.Version} loaded");
    }
}

internal enum ConsentChoice
{
    Unset,
    Allow,
    Deny,
}

internal static class Cfg
{
    internal static ConfigEntry<bool> Enabled = null!;
    internal static ConfigEntry<string> ServerUrl = null!;

    internal static ConfigEntry<bool> UseLlm = null!;
    internal static ConfigEntry<float> DecisionInterval = null!;
    internal static ConfigEntry<float> MinSecondsBeforeAttack = null!;
    internal static ConfigEntry<float> CoverBlownSuspicion = null!;
    internal static ConfigEntry<float> MinSecondsBetweenLines = null!;
    internal static ConfigEntry<bool> RevealOnAttack = null!;
    internal static ConfigEntry<float> WalkSpeed = null!;
    internal static ConfigEntry<float> SprintSpeed = null!;
    internal static ConfigEntry<float> NameTagRange = null!;

    internal static ConfigEntry<bool> ReviveUnwatched = null!;
    internal static ConfigEntry<float> ReviveChance = null!;
    internal static ConfigEntry<float> ReviveDelay = null!;
    internal static ConfigEntry<float> WatchedRange = null!;

    internal static ConfigEntry<ConsentChoice> VoiceConsent = null!;
    internal static ConfigEntry<bool> ConsentIncludedSaving = null!;
    internal static ConfigEntry<float> MimicVoiceVolume = null!;

    internal static ConfigEntry<bool> SoloMode = null!;
    internal static ConfigEntry<Key> SpawnMaskedKey = null!;
    internal static ConfigEntry<string> SpawnCopies = null!;
    internal static ConfigEntry<bool> ShowBrainOverlay = null!;
    internal static ConfigEntry<bool> LogPrompts = null!;
    internal static ConfigEntry<string> ForceBehaviour = null!;
    internal static ConfigEntry<bool> NoAttack = null!;
    internal static ConfigEntry<bool> RevealMasked = null!;
    internal static ConfigEntry<bool> SaveAudioToDisk = null!;
    internal static ConfigEntry<Key> ResetConsentKey = null!;

    internal static void Bind(ConfigFile c)
    {
        Enabled = c.Bind("General", "Enabled", true, "Turns the mod on or off. Everyone in the lobby needs the mod.");
        ServerUrl = c.Bind("General", "AiServerUrl", "http://127.0.0.1:5279",
            "The local AI server (speech-to-text, voice cloning, LLM). Only the host uses it.");

        UseLlm = c.Bind("Brain", "UseLlm", true,
            "Host: let the LLM pick what each Masked does. Off, or while the server is down, Masked use simple scripted behaviour.");
        DecisionInterval = c.Bind("Brain", "DecisionInterval", 7f,
            "Host: seconds between LLM decisions when nothing happens. Events (being hit, someone talking) decide right away.");
        MinSecondsBeforeAttack = c.Bind("Brain", "MinSecondsBeforeAttack", 90f,
            "Host: a Masked won't attack before spending this long near the crew, unless its cover is blown or it's threatened.");
        CoverBlownSuspicion = c.Bind("Brain", "CoverBlownSuspicion", 0.7f,
            "Host: suspicion (0-1) at which a Masked counts its cover as blown and may attack right away.");
        MinSecondsBetweenLines = c.Bind("Brain", "MinSecondsBetweenLines", 6f, "Host: shortest gap between two lines of one Masked.");
        RevealOnAttack = c.Bind("Brain", "RevealOnAttack", true,
            "When a Masked attacks, its mask and Masked animations come back (the vanilla kill animation needs them).");
        WalkSpeed = c.Bind("Brain", "WalkSpeed", 4.6f, "Host: Masked walking speed (players walk at about 4.6).");
        SprintSpeed = c.Bind("Brain", "SprintSpeed", 7.4f, "Host: Masked sprinting speed.");
        NameTagRange = c.Bind("Brain", "NameTagRange", 5f, "How close you have to look at a Masked to see its name tag (players: 5).");

        ReviveUnwatched = c.Bind("Revive", "Enabled", true,
            "Host: players who die with nobody watching come back as a Masked that copies them.");
        ReviveChance = c.Bind("Revive", "Chance", 1f, "Host: chance (0-1) that an unwatched death comes back.");
        ReviveDelay = c.Bind("Revive", "DelaySeconds", 8f, "Host: seconds between the death and the Masked getting up.");
        WatchedRange = c.Bind("Revive", "WatchedRange", 35f,
            "Host: a death counts as watched if a living player had line of sight to it within this range.");

        VoiceConsent = c.Bind("Voice", "VoiceCloningConsent", ConsentChoice.Unset,
            "Your choice from the consent popup. Allow: your voice chat is sent to the host to clone your voice for Masked. " +
            "Deny: it never leaves your computer. Unset shows the popup again.");
        ConsentIncludedSaving = c.Bind("Voice", "ConsentIncludedSaving", false,
            "Set when you allowed cloning while a host was saving audio to disk. Managed by the popup.");
        MimicVoiceVolume = c.Bind("Voice", "MimicVoiceVolume", 1f, "Volume of Masked voices on your end.");

        SoloMode = c.Bind("Debug", "SoloMode", false,
            "Host: Masked may copy you and talk to you even when you're the only player (for testing alone).");
        SpawnMaskedKey = c.Bind("Debug", "SpawnMaskedKey", Key.None, "Host: key that spawns a Masked in front of you. None turns it off.");
        SpawnCopies = c.Bind("Debug", "SpawnCopies", "", "Host: name of the player a spawned Masked copies. Empty copies you.");
        ShowBrainOverlay = c.Bind("Debug", "ShowBrainOverlay", false,
            "Host: text over each Masked with its behaviour, target, suspicion and last LLM answer.");
        LogPrompts = c.Bind("Debug", "LogPrompts", false, "Host: write every LLM prompt and answer to the BepInEx log.");
        ForceBehaviour = c.Bind("Debug", "ForceBehaviour", "",
            "Host: lock every Masked to one behaviour (blend_in, follow, stare, wander, lure, attack, flee, emote). Empty is off.");
        NoAttack = c.Bind("Debug", "NoAttack", false, "Host: Masked never attack.");
        RevealMasked = c.Bind("Debug", "RevealMasked", false, "Shows a red marker on every Masked, through walls.");
        SaveAudioToDisk = c.Bind("Debug", "SaveAudioToDisk", false,
            "Host: keep every received voice clip as a WAV file in BepInEx/PlayerMimics/audio. The consent popup tells players.");
        ResetConsentKey = c.Bind("Debug", "ResetConsentKey", Key.None,
            "Key that clears your consent choice and shows the popup again. None turns it off.");
    }
}
