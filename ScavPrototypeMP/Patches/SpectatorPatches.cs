using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;

namespace Together;

[HarmonyPatch(typeof(PlayerCamera), "HandleCameraPosition")]
internal static class SpectatorCameraPositionPatch {
    static bool Prefix(PlayerCamera __instance) => !Net.IsRunning || !Spectator.FollowCamera(__instance);
}

[HarmonyPatch(typeof(PlayerCamera), "LateUpdate")]
internal static class SpectatorCameraLatePatch {
    [HarmonyPriority(Priority.First)]
    static void Prefix(PlayerCamera __instance) {
        if (Net.IsRunning) Spectator.FollowCamera(__instance);
    }
    // Apply after network interpolation and vanilla camera/UI readers.
    [HarmonyPriority(Priority.Last)]
    static void Postfix(PlayerCamera __instance) {
        if (Net.IsRunning) Spectator.FollowCamera(__instance);
    }
}

// Dragging is outside HandleInput in 1.0.1. UI callbacks can also invoke
// hand/gun/medical actions directly, so skipping HandleInput alone is insufficient.
[HarmonyPatch]
internal static class SpectatorWorldActionsPatch {
    static IEnumerable<MethodBase> TargetMethods() {
        foreach (string name in new[] {
            "HandleDragging", "HandleAttacks", "HandleGunMenu", "TryPickupFromWorld", "TryPickupFromUI",
            "SwitchHands", "GunRack", "GunEjectMag", "GunToggleSafety", "ApplyWoundItem", "WoundSpecialAction", "OpenCraftScreen",
            "TryPerformRadialAction", "DoBodyWorkout",
            "TraderTryHaggle", "TraderTryHug", "TraderTryThreaten", "TraderTryRest", "TraderTryMove"
        }) yield return AccessTools.Method(typeof(PlayerCamera), name);
    }
    [HarmonyPriority(Priority.First)]
    static bool Prefix() => !Net.IsRunning || !UIInGame.SpectatorModeActive;
}
