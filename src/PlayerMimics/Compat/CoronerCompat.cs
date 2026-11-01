using System.Runtime.CompilerServices;
using BepInEx.Bootstrap;

namespace PlayerMimics.Compat;

// Coroner is a soft dependency: the mod runs without it. Only call into this class after checking Enabled, and keep
// every use of Coroner's types inside it.
internal static class CoronerCompat
{
    private static bool? enabled;

    internal static bool Enabled => enabled ??= Chainloader.PluginInfos.ContainsKey("com.elitemastereric.coroner");

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    internal static void Initialize()
    {
        // Calls into Coroner go here.
    }
}
