using System.Runtime.CompilerServices;
using BepInEx.Bootstrap;

namespace PlayerMimics.Compat;

// MoreCompany is a soft dependency: the mod runs without it. Only call into this class after checking Enabled, and keep
// every use of MoreCompany's types inside it.
internal static class MoreCompanyCompat
{
    private static bool? enabled;

    internal static bool Enabled => enabled ??= Chainloader.PluginInfos.ContainsKey("me.swipez.melonloader.morecompany");

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    internal static void Initialize()
    {
        // Calls into MoreCompany go here.
    }
}
