#if FULLGAME
using HarmonyLib;
using UnityEngine;

namespace Together;

// Ghost carried-item replicas can exist without LiquidAffect (the component
// destroys itself for non-dynamic rigidbodies). Vanilla Item.Update then
// dereferences affect every frame and floods the log/frame budget. This is a
// defensive guard only; the parent-ready create path is the real fix.
[HarmonyPatch(typeof(Item), nameof(Item.Update))]
internal static class ItemUpdateAffectGuard {
    static bool Prefix(Item __instance) {
        if (!Net.IsRunning || __instance == null) return true;
        if (__instance.affect == null && __instance.transform.parent == null) return false;
        return true;
    }
}
#endif
