using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using ConfigSync;

namespace PlayerMimics;

[BepInPlugin(MyPluginInfo.Id, MyPluginInfo.Name, MyPluginInfo.Version)]
[BepInDependency("tech.rethunk.mortar.ConfigSync")]
[BepInProcess("Lethal Company.exe")]
public class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log { get; private set; } = null!;

    // Settings here are each player's own. Use ConfigSync for ones the whole lobby should share.
    internal static ConfigEntry<bool> Enabled { get; private set; } = null!;

    // While connected, Value is the host's, so everyone in the lobby uses the same setting.
    internal static SyncedEntry<float> Strength { get; private set; } = null!;

    private void Awake()
    {
        Log = Logger;
        Enabled = Config.Bind("General", "Enabled", true, "Turns the mod on or off.");
        Strength = SyncedConfig.For(this).Share(
            Config.Bind("General", "Strength", 1f, "How strong the mod's effect is. The host's value applies to everyone.")
        );
        ModNetworkSetup.Install();
        // Put the mod's hooks after this check so Enabled can turn them off.
        if (!Enabled.Value)
        {
            Log.LogInfo($"{MyPluginInfo.Name} is turned off in its config");
            return;
        }
        Log.LogInfo($"{MyPluginInfo.Name} {MyPluginInfo.Version} loaded");
    }
}
