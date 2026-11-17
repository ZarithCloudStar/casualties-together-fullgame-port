using HarmonyLib;
using CasualtiesTogetherUtils;
using UnityEngine;

namespace Together;

[HarmonyPatch(typeof(Body), "FixedUpdate")]
internal static class Body_FixedUpdate_RemoteRagdoll {
    // Run after the telekinesis hold guard and honor any earlier simulation veto.
    [HarmonyPriority(Priority.Last)]
    static bool Prefix(Body __instance, bool __runOriginal) {
        if (!__runOriginal) return false;
        if (!Net.IsRunning || __instance.standing || !__instance.conscious || __instance.IsBodyLocal()) return true;
        if (!__instance.TryGetNetBody(out var nb) || !nb.IsPlayer || nb.piggybacking_on != null) return true;
        if (__instance.rb != null) {
            __instance.rb.gravityScale = 1f;
            __instance.lastTimeStepVelocity = __instance.rb.velocity;
            if ((__instance.grounded || (__instance.bodyAffect != null && __instance.bodyAffect.wasWater))
                && __instance.shock < 10f && __instance.rb.velocity.magnitude < __instance.maxSpeed * 0.1f) {
                __instance.Stand(false);
            }
        }
        return false;
    }
}
