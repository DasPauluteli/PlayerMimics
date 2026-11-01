using System.Runtime.CompilerServices;
using BepInEx.Bootstrap;

namespace PlayerMimics.Compat;

// LethalConfig is a soft dependency: the mod runs without it. Only call into this class after checking Enabled, and keep
// every use of LethalConfig's types inside it.
internal static class LethalConfigCompat
{
    private static bool? enabled;

    internal static bool Enabled => enabled ??= Chainloader.PluginInfos.ContainsKey("ainavt.lc.lethalconfig");

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    internal static void Initialize()
    {
        // Calls into LethalConfig go here.
    }
}
