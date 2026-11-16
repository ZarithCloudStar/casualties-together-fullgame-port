#if FULLGAME
using System.Linq;
using HarmonyLib;
using UnityEngine;
namespace Together;

// Team-ending guards keep their existing route/target rules. Other guards use
// the nearest living peer, matching the upstream fix for client-only targets.
[HarmonyPatch(typeof(GuardScript), "body", MethodType.Getter)]
internal static class PortGuardTarget {
    static void Postfix(GuardScript __instance, ref Body __result) {
        if (!Net.IsRunning || !Net.IsServer || EndingGuardSync.Active) return;
        var player = ScavPlayer.AllLivingPlayers.Where(p => p != null && p.body != null)
            .OrderBy(p => ((Vector2)p.body.transform.position - (Vector2)__instance.transform.position).sqrMagnitude).FirstOrDefault();
        if (player != null) __result = player.body;
    }
}
#endif
