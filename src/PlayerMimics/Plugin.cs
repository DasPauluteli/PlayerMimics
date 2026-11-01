using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PlayerMimics;

[BepInDependency("ainavt.lc.lethalconfig", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("x753.More_Suits", BepInDependency.DependencyFlags.SoftDependency)]
[BepInPlugin(MyPluginInfo.Id, MyPluginInfo.Name, MyPluginInfo.Version)]
[BepInDependency("BetterEmotes", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("BestEmotes", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("me.swipez.melonloader.morecompany", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("com.elitemastereric.coroner", BepInDependency.DependencyFlags.SoftDependency)]
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
        CoronerCompat.Init();

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
    internal static ConfigEntry<float> SmallTalkSeconds = null!;
    internal static ConfigEntry<float> WalkSpeed = null!;
    internal static ConfigEntry<float> NameTagRange = null!;
    internal static ConfigEntry<bool> MonstersNoticeMimics = null!;

    internal static ConfigEntry<bool> ReviveEnabled = null!;
    internal static ConfigEntry<int> ReviveChancePercent = null!;
    internal static ConfigEntry<bool> ReviveOnlyUnobserved = null!;
    internal static ConfigEntry<float> ReviveDelay = null!;
    internal static ConfigEntry<float> WatchedRange = null!;

    internal static ConfigEntry<ConsentChoice> VoiceConsent = null!;
    internal static ConfigEntry<bool> ConsentIncludedSaving = null!;
    internal static ConfigEntry<Key> ResetConsentKey = null!;
    internal static ConfigEntry<float> MimicVoiceVolume = null!;
    internal static ConfigEntry<bool> StreamLines = null!;
    internal static ConfigEntry<float> StreamLeadSeconds = null!;

    internal static ConfigEntry<bool> SoloMode = null!;
    internal static ConfigEntry<Key> SpawnMaskedKey = null!;
    internal static ConfigEntry<string> SpawnCopies = null!;
    internal static ConfigEntry<string> MimicName = null!;
    internal static ConfigEntry<bool> ShowBrainOverlay = null!;
    internal static ConfigEntry<bool> LogPrompts = null!;
    internal static ConfigEntry<string> ForceBehaviour = null!;
    internal static ConfigEntry<bool> NoAttack = null!;
    internal static ConfigEntry<bool> RevealMasked = null!;
    internal static ConfigEntry<bool> SaveAudioToDisk = null!;

    // "Host only" settings are read on the host; the values everyone needs are sent to the clients. "Per player"
    // settings belong to each player.
    internal static void Bind(ConfigFile c)
    {
        Enabled = c.Bind("General", "Enabled", true, "Turns the mod on or off. Everyone in the lobby needs the mod.");
        ServerUrl = c.Bind("General", "AiServerUrl", "http://127.0.0.1:5279",
            "Host only: the local AI server (speech-to-text, voice cloning, LLM).");

        UseLlm = c.Bind("Brain", "UseLlm", true,
            "Host only: let the LLM pick what each Masked does. Off, or while the server is down, Masked use simple scripted behaviour.");
        DecisionInterval = c.Bind("Brain", "DecisionInterval", 7f,
            "Host only: seconds between LLM decisions when nothing happens. Events (being hit, someone talking) decide right away.");
        MinSecondsBeforeAttack = c.Bind("Brain", "MinSecondsBeforeAttack", 90f,
            "Host only: a Masked won't attack before spending this long near the crew, unless its cover is blown or it's threatened.");
        CoverBlownSuspicion = c.Bind("Brain", "CoverBlownSuspicion", 0.7f,
            "Host only: suspicion (0-1) at which a Masked counts its cover as blown and may attack right away.");
        MinSecondsBetweenLines = c.Bind("Brain", "MinSecondsBetweenLines", 4f, "Host only: shortest gap between two lines of one Masked.");
        SmallTalkSeconds = c.Bind("Brain", "SmallTalkSeconds", 60f,
            "Host only: about how many seconds a Masked waits before saying something unprompted. It always answers when talked to.");
        WalkSpeed = c.Bind("Brain", "WalkSpeed", 4.6f,
            "Host only: Masked walking speed. Players walk at 4.6 empty-handed; sprinting and stamina work like a player's.");
        NameTagRange = c.Bind("Brain", "NameTagRange", 5f,
            "Host only: how close you have to look at a Masked to see its name tag (players: 5). Sent to everyone.");
        MonstersNoticeMimics = c.Bind("Brain", "MonstersNoticeMimics", true,
            "Host only: roaming spiders, thumpers, slimes and giants go for a Masked that's near a player, so they don't " +
            "suspiciously ignore it. Masked always run from monsters like players do.");

        ReviveEnabled = c.Bind("Revive", "Enabled", true, "Host only: dead players can get back up as a Masked that copies them.");
        ReviveChancePercent = c.Bind("Revive", "ChancePercent", 100,
            new ConfigDescription("Host only: chance in percent that a death comes back as a Masked.", new AcceptableValueRange<int>(0, 100)));
        ReviveOnlyUnobserved = c.Bind("Revive", "OnlyWhenUnobserved", true,
            "Host only: only deaths nobody saw come back. Off: seen deaths come back too, and whoever saw one knows it's " +
            "not really them.");
        ReviveDelay = c.Bind("Revive", "DelaySeconds", 8f, "Host only: seconds between the death and the Masked getting up.");
        WatchedRange = c.Bind("Revive", "WatchedRange", 35f,
            "Host only: a death counts as watched if a living player had line of sight to it within this range.");

        VoiceConsent = c.Bind("Voice", "VoiceCloningConsent", ConsentChoice.Unset,
            "Per player: your choice from the voice popup. Allow: your voice chat also goes to the host, where an AI copy of " +
            "your voice is made. Deny: it isn't sent. Unset shows the popup again.");
        ConsentIncludedSaving = c.Bind("Voice", "ConsentIncludedSaving", false,
            "Per player: set when you allowed cloning while a host was saving audio to disk. Managed by the popup.");
        ResetConsentKey = c.Bind("Voice", "ResetConsentKey", Key.None,
            "Per player: key that clears your consent choice and shows the popup again. None turns it off.");
        MimicVoiceVolume = c.Bind("Voice", "MimicVoiceVolume", 1f, "Per player: volume of AI voices on your end.");
        StreamLines = c.Bind("Voice", "StreamLines", true,
            "Host only: start playing a Masked's line while the AI server is still making it, as early as it can without " +
            "running out of audio. Off: the whole line is made first.");
        StreamLeadSeconds = c.Bind("Voice", "StreamLeadSeconds", 0.4f,
            "Host only: seconds of a streamed line that must be ready before it starts playing (a buffer against hiccups). " +
            "Slower generation waits for more.");

        SoloMode = c.Bind("Debug", "SoloMode", false,
            "Host only: Masked copy you and talk to you even with other players around (alone, this happens anyway).");
        SpawnMaskedKey = c.Bind("Debug", "SpawnMaskedKey", Key.None, "Host only: key that spawns a Masked in front of you. None turns it off.");
        SpawnCopies = c.Bind("Debug", "SpawnCopies", "", "Host only: name of the player a spawned Masked copies. Empty copies you.");
        MimicName = c.Bind("Debug", "MimicName", "",
            "Host only: a name new Masked go by instead of the copied player's: on their name tag, when people talk to them, " +
            "for the AI and in the death report. They still look and sound like the copied player. Empty: off.");
        ShowBrainOverlay = c.Bind("Debug", "ShowBrainOverlay", false,
            "Host only: text over each Masked with its behaviour, target, suspicion and last LLM answer. Only the host sees it.");
        LogPrompts = c.Bind("Debug", "LogPrompts", false, "Host only: write every LLM prompt and answer to the BepInEx log.");
        ForceBehaviour = c.Bind("Debug", "ForceBehaviour", "",
            "Host only: lock every Masked to one behaviour (blend_in, follow, stare, wander, lure, attack, flee, emote). Empty is off.");
        NoAttack = c.Bind("Debug", "NoAttack", false, "Host only: Masked never attack.");
        RevealMasked = c.Bind("Debug", "RevealMasked", false, "Host only: shows a red marker on every Masked, through walls. Only the host sees it.");
        SaveAudioToDisk = c.Bind("Debug", "SaveAudioToDisk", false,
            "Host only: keep every received voice clip as a WAV file in BepInEx/PlayerMimics/audio. The consent popup tells players.");
    }
}
