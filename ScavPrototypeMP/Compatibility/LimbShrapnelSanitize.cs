using HarmonyLib;
using UnityEngine;

namespace Together;

// Old GCE bridge builds could store packed shard sources in Limb.shrapnel.
// The stripped build only understands the vanilla 0..5 count, so collapse
// any legacy packed value when a limb starts (including save loads).
[HarmonyPatch(typeof(Limb), nameof(Limb.Start))]
internal static class LimbShrapnelSanitizePatch {
    static void Postfix(Limb __instance) {
        if (__instance == null) return;
        int value = __instance.shrapnel;
        if (value <= 5) return;
        bool oldPacked = (value & (1 << 30)) != 0;
        bool newPacked = (value & (1 << 29)) != 0;
        if (oldPacked) value &= 0xFF;
        else if (newPacked) value = 5;
        __instance.shrapnel = Mathf.Clamp(value, 0, 5);
    }
}
