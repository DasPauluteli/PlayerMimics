using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using GameNetcodeStuff;
using HarmonyLib;

namespace PlayerMimics;

// Coroner is optional. This class never touches Coroner types itself, so it's safe to call without it;
// CoronerBridge does the actual work and only loads when Coroner is installed.
internal static class CoronerCompat
{
    internal static bool Installed => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.elitemastereric.coroner");

    internal static void Init()
    {
        if (!Installed) return;
        try
        {
            CoronerBridge.Init();
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"Coroner integration failed to start: {e.Message}");
        }
    }

    // Every client: prepare "killed by a Masked disguised as <name>" for the end-of-round report.
    internal static void OnDisguised(string name)
    {
        if (!Installed) return;
        try
        {
            CoronerBridge.RegisterFor(name);
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"Coroner: {e.Message}");
        }
    }

    // Every client, like Coroner's own Masked handling: report the cause of death for the victim.
    internal static void OnKill(MimicDisguise disguise, PlayerControllerB victim)
    {
        if (!Installed) return;
        try
        {
            CoronerBridge.ReportKill(disguise.Name, victim);
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"Coroner: {e.Message}");
        }
    }
}

internal static class CoronerBridge
{
    private const string GenericTag = "DeathEnemyPlayerMimic";
    private const string SelfTag = "DeathEnemyPlayerMimicSelf";
    private static readonly HashSet<string> injected = new();

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Init()
    {
        Inject(GenericTag, new[]
        {
            "Was killed by a Masked disguised as a crewmate.",
            "Trusted the wrong crewmate.",
            "Never asked for a second opinion.",
            "Walked with the Impostor.",
            "Counted the crew. Got one too many.",
            "Was sure it was a friend.",
            "Didn't notice the extra crewmate.",
            "Never checked who they were talking to.",
            "Was led away by a familiar face.",
            "Heard a friendly voice. It wasn't.",
            "Let the wrong one carry the scrap.",
            "Thought everyone made it back to the ship.",
        });
        Inject(SelfTag, new[]
        {
            "Was killed by a Masked disguised as themselves.",
            "Met their own double. It went badly.",
            "Was replaced.",
            "Was killed by their own face.",
            "Found out what they look like from behind.",
            "Had a familiar voice in their ear.",
            "Was outplayed by themselves.",
            "Lost an argument with themselves.",
        });
        Plugin.Log.LogInfo("Coroner integration on");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void RegisterFor(string name) => Inject(TagFor(name), NamedLines(name));

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ReportKill(string copiedName, PlayerControllerB victim)
    {
        string tag = victim.playerUsername == copiedName ? SelfTag : TagFor(copiedName);
        if (!Coroner.API.IsRegistered(tag)) tag = GenericTag;
        Coroner.API.SetCauseOfDeath(victim, Cause(tag));
    }

    // The first line is the literal one, used when Coroner's SeriousDeathMessages is on.
    private static string[] NamedLines(string name) => new[]
    {
        $"Was killed by a Masked disguised as {name}.",
        $"Trusted {name}. It wasn't {name}.",
        $"Followed {name} somewhere quiet.",
        $"Should have asked {name} a security question.",
        $"Got too close to the thing wearing {name}.",
        $"Thought {name} was acting a bit off.",
        $"Took {name} at their word.",
        $"Was walked home by {name}. Not the real one.",
        $"Asked {name} to wait up. It did.",
        $"Answered when {name} called.",
        $"Followed {name}'s voice into the dark.",
        $"Was told by {name} that it was safe.",
        $"Stuck with {name}. Big mistake.",
        $"Never saw what was wearing {name}.",
        $"Didn't know {name} had already died.",
        $"Learned {name} wasn't {name}.",
    };

    // Coroner keys must be valid XML names, and player names aren't, so key by a hash of the name.
    private static string TagFor(string name)
    {
        uint h = 2166136261;
        foreach (char c in name) h = (h ^ c) * 16777619;
        return $"{GenericTag}As{h:x8}";
    }

    private static Coroner.AdvancedCauseOfDeath Cause(string tag) =>
        Coroner.API.IsRegistered(tag) ? Coroner.AdvancedCauseOfDeath.Registry[tag] : Coroner.API.Register(tag);

    // Coroner reads its texts from XML files. Ours are added straight into its loaded language data instead,
    // so nothing has to be installed into its config folder.
    private static void Inject(string tag, string[] lines)
    {
        if (!injected.Add(tag)) return;
        _ = Cause(tag);
        var strings = new XElement("strings");
        foreach (string line in lines) strings.Add(new XElement(tag, new XAttribute("text", line)));
        var doc = new XDocument(new XElement("base", strings));
        var plugin = Coroner.Plugin.Instance;
        foreach (string field in new[] { "LanguageHandler", "FallbackLanguageHandler" })
        {
            object? handler = AccessTools.Field(typeof(Coroner.Plugin), field)?.GetValue(plugin);
            MethodInfo? populate = handler == null ? null : AccessTools.Method(handler.GetType(), "PopulateLanguageData");
            populate?.Invoke(handler, new object[] { doc, true });
        }
    }
}
