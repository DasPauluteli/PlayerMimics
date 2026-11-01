using System.Runtime.CompilerServices;
using BepInEx.Bootstrap;

namespace PlayerMimics.Compat;

// More_Suits is a soft dependency: the mod runs without it. Only call into this class after checking Enabled, and keep
// every use of More_Suits's types inside it.
internal static class More_SuitsCompat
{
    private static bool? enabled;

    internal static bool Enabled => enabled ??= Chainloader.PluginInfos.ContainsKey("x753.More_Suits");

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    internal static void Initialize()
    {
        // Calls into More_Suits go here.
    }
}
