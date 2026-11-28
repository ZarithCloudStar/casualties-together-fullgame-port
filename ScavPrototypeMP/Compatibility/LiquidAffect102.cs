#if FULLGAME
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection.Emit;

namespace Together;

// 1.0.2's lava limb branch reads body.inCardiacArrest, but Start caches
// GetComponent<Body>() only on the root. A limb owns its body via Limb.body.
[HarmonyPatch(typeof(LiquidAffect), "FixedUpdate")]
internal static class LiquidAffect102 {
    static bool Prefix(Body ___body, Limb ___limb) =>
        !JoinSpawnProtection.BlocksDamage(___limb != null ? ___limb.body : ___body);

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        var codes = new List<CodeInstruction>(instructions);
        var body = AccessTools.Field(typeof(LiquidAffect), "body");
        var cardiac = AccessTools.PropertyGetter(typeof(Body), nameof(Body.inCardiacArrest));
        int matches = 0;
        for (int i = 0; i < codes.Count - 1; i++) {
            if (!codes[i].LoadsField(body) || !codes[i + 1].Calls(cardiac)) continue;
            codes[i].operand = AccessTools.Field(typeof(LiquidAffect), "limb");
            codes.Insert(++i, new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Limb), nameof(Limb.body))));
            matches++;
        }
        if (matches != 1) throw new InvalidOperationException("LiquidAffect 1.0.2 lava owner pattern mismatch: " + matches);
        return codes;
    }
}
#endif
