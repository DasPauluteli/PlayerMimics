using System.Runtime.CompilerServices;
using BepInEx.Bootstrap;

namespace PlayerMimics.Compat;

// BetterEmotes is a soft dependency: the mod runs without it. Only call into this class after checking Enabled, and keep
// every use of BetterEmotes's types inside it.
internal static class BetterEmotesCompat
{
    private static bool? enabled;

    internal static bool Enabled => enabled ??= Chainloader.PluginInfos.ContainsKey("BetterEmotes");

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    internal static void Initialize()
    {
        // Calls into BetterEmotes go here.
    }
}
