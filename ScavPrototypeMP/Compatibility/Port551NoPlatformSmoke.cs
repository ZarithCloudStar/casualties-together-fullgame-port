#if FULLGAME && TESTING
using HarmonyLib;
namespace Together;
[HarmonyPatch(typeof(SteamManager), nameof(SteamManager.Awake))]
internal static class Port551NoPlatformSmoke {
    static bool Prefix() => !Plugin.FORCE_NO_STEAM;
}
#endif
