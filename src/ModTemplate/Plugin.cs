#if (assets)
using System.IO;
#endif
using BepInEx;
#if (config)
using BepInEx.Configuration;
#endif
using BepInEx.Logging;
#if (configSync)
using ConfigSync;
#endif
#if (assets)
using UnityEngine;
#endif

namespace ModTemplate;

[BepInPlugin(MyPluginInfo.Id, MyPluginInfo.Name, MyPluginInfo.Version)]
#if (configSync)
[BepInDependency("tech.rethunk.mortar.ConfigSync")]
#endif
[BepInProcess("Lethal Company.exe")]
public class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log { get; private set; } = null!;
#if (config)

    // Settings here are each player's own. Use ConfigSync for ones the whole lobby should share.
    internal static ConfigEntry<bool> Enabled { get; private set; } = null!;
#if (configSync)

    // While connected, Value is the host's, so everyone in the lobby uses the same setting.
    internal static SyncedEntry<float> Strength { get; private set; } = null!;
#endif
#endif
#if (assets)

    internal static AssetBundle? Assets { get; private set; }
#endif

    private void Awake()
    {
        Log = Logger;
#if (config)
        Enabled = Config.Bind("General", "Enabled", true, "Turns the mod on or off.");
#if (configSync)
        Strength = SyncedConfig.For(this).Share(
            Config.Bind("General", "Strength", 1f, "How strong the mod's effect is. The host's value applies to everyone.")
        );
#endif
#endif
#if (assets)
        Assets = AssetBundle.LoadFromFile(Path.Combine(Path.GetDirectoryName(Info.Location)!, "modtemplate.bundle"));
        if (Assets == null)
        {
            Log.LogError("modtemplate.bundle is missing beside the plugin");
        }
#endif
#if (netcode)
        ModNetworkSetup.Install();
#endif
#if (config)
        // Put the mod's hooks after this check so Enabled can turn them off.
        if (!Enabled.Value)
        {
            Log.LogInfo($"{MyPluginInfo.Name} is turned off in its config");
            return;
        }
#endif
        Log.LogInfo($"{MyPluginInfo.Name} {MyPluginInfo.Version} loaded");
    }
}
