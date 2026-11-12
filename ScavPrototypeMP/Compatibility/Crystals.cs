using CasualtiesTogetherUtils;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Unity.VisualScripting;
using UnityEngine;

namespace Together;


#if FULLGAME



// Ported from upstream 5.5.0 (9e6a5d8). All 14 original patch targets remain.
// CrystalSync owns the transport and the server-only settlement boundary.
[HarmonyPatch(typeof(CrystalBehaviour), nameof(CrystalBehaviour.OnCollisionEnter2D))]
static class CrystalBehaviour_OnCollisionEnter2D_MultiplayerPatch {
    static bool Prefix(CrystalBehaviour __instance, Collision2D collision) {
        if (!Net.IsRunning) return true;
        if (collision != null) {
#if TESTING
            if (CrystalTests.Enabled) CrystalTests.collisions++;
#endif
            CrystalSync.Contact(__instance, collision.gameObject);
        }
        return false;
    }
}
[HarmonyPatch(typeof(CrystalBehaviour), nameof(CrystalBehaviour.BuildingHit))]
static class CrystalBehaviour_BuildingHit_MultiplayerPatch {
    static bool Prefix(CrystalBehaviour __instance, AttackInfo atk) {
        if (!Net.IsRunning) return true;
        CrystalSync.Hit(__instance, CrystalSync.MeleeAttacker, atk);
        return false;
    }
}

[HarmonyPatch(typeof(CrystalBurning), nameof(CrystalBurning.Burn))]
internal static class CrystalBurning_Burn_MultiplayerPatch {
    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        var codes = instructions;

        codes = TranspilerHelpers.PatchShakers(codes, generator, __originalMethod);

        return codes;
    }
    static bool Prefix(CrystalBurning __instance, ref Body body) {
        if (!Net.IsRunning) return true;
        if (!Net.IsServer || __instance.crystal == null) return false;

        // An explicit touched body wins; Hit's camera singleton is replaced only
        // inside the scoped dispatch. Never fall back to this peer's camera.
        if (CrystalSync.TriggerBody != null) body = CrystalSync.TriggerBody;
        return body != null && Net.IsServer;
    }
}

[HarmonyPatch(typeof(CrystalEMP), nameof(CrystalEMP.TryEMP))]
internal static class CrystalEMP_TryEMP_MultiplayerPatch {

    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        var codes = instructions;

        codes = BodyGetterOverrider.TranspileAll(codes, generator, __originalMethod.DeclaringType);
        codes = TranspilerHelpers.PatchShakers(codes, generator, __originalMethod);

        return codes;
    }

    static bool Prefix(CrystalEMP __instance) {
        if (!Net.IsRunning) return true;
        return Net.IsServer && __instance.crystal != null && CrystalSync.TriggerBody != null;
    }
}

[HarmonyPatch(typeof(CrystalElectric), nameof(CrystalElectric.Shock))]
internal static class CrystalElectric_Shock_MultiplayerPatch {

    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        var codes = instructions;

        codes = BodyGetterOverrider.TranspileAll(codes, generator, __originalMethod.DeclaringType);
        codes = TranspilerHelpers.PatchShakers(codes, generator, __originalMethod);

        return codes;
    }

    static bool Prefix(CrystalElectric __instance, ref Body body) {
        if (!Net.IsRunning) return true;
        if (!Net.IsServer || __instance.crystal == null) return false;

        // An explicit touched body wins; Hit's camera singleton is replaced only
        // inside the scoped dispatch. Never fall back to this peer's camera.
        if (CrystalSync.TriggerBody != null) body = CrystalSync.TriggerBody;
        return body != null && Net.IsServer;
    }
}



[HarmonyPatch(typeof(CrystalEnemy), nameof(CrystalEnemy.body), MethodType.Getter)]
static class CrystalEnemy_get_body_MultiplayerPatch {

 

    static void Postfix(CrystalEnemy __instance, ref Body __result) {
        if (!Net.IsRunning) return;

        __result = CrystalSync.EnemyTarget(__instance);

    }
}


[HarmonyPatch(typeof(CrystalEnemy), nameof(CrystalEnemy.Update))]
internal static class CrystalEnemy_Update_MultiplayerPatch {

    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        var codes = instructions;

        codes = BodyGetterOverrider.TranspileAll(codes, generator, __originalMethod.DeclaringType);
        codes = TranspilerHelpers.PatchShakers(codes, generator, __originalMethod);

        return codes;
    }

    static bool Prefix(CrystalEnemy __instance) {
        if (!Net.IsRunning) return true;

        if (!Net.IsServer || CrystalSync.EnemyTarget(__instance) == null) return false;
        var bgu = __instance.GetComponent<BodyGetterUpdater>();
        if (bgu == null) {
            bgu = __instance.AddComponent<BodyGetterUpdater>();
            bgu.cooldown = 5f;
            bgu.condition = (g) => { 
                if (g.TryGetComponent(out CrystalEnemy ce)) {
                    if (ce.body != null && ce.close && ce.body.GetLowerTorso() != null) {
                        List<RaycastHit2D> rh = new();
                        var result = Physics2D.Linecast(ce.body.GetLowerTorso().transform.position, ce.transform.position, new ContactFilter2D() { layerMask = LayerMask.GetMask("Ground") }, rh);
                        if (result > 0) {
                            rh.RemoveAll((x) => {
                                return x.transform == null || Util.TryGetBodyFromObject(x.transform, out var _) || ce.transform == x.transform;
                            });
                            result = rh.Count;
                        }
                        return result > 0;// something is blocking the view, pick another player
                    }
                    return true;
                }
                return true; 
            };
        }

        return true;
    }
}

[HarmonyPatch(typeof(CrystalEnemy), nameof(CrystalEnemy.Awake))]
static class CrystalEnemy_Awake_MultiplayerPatch {
    static void Postfix(CrystalEnemy __instance) {
        if (!Net.IsRunning) return;
        var bgo = __instance.GetOrAddComponent<BodyGetterOverrider>();
        bgo.SetNearestBody(); // Just pick a nearest body
    }
}



[HarmonyPatch(typeof(CrystalHealing), nameof(CrystalHealing.Touched))]
internal static class CrystalHealing_Touched_MultiplayerPatch {
    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        return TranspilerHelpers.PatchCoroutines(instructions, __originalMethod, null, new HashSet<string> { "FlashBrief" });
    }

    static bool Prefix(CrystalHealing __instance, GameObject touched) {
        return !Net.IsRunning || Net.IsServer;
    }
}


[HarmonyPatch(typeof(CrystalKinetic), nameof(CrystalKinetic.Touched))]
internal static class CrystalKinetic_Touched_MultiplayerPatch {

    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        var codes = instructions;

        codes = BodyGetterOverrider.TranspileAll(codes, generator, __originalMethod.DeclaringType);
        codes = TranspilerHelpers.PatchShakers(codes, generator, __originalMethod);

        return codes;
    }

    static bool Prefix(CrystalKinetic __instance) {
        return !Net.IsRunning || Net.IsServer && __instance.crystal != null;
    }
}



[HarmonyPatch(typeof(CrystalMetamorphic), nameof(CrystalMetamorphic.Touched))]
internal static class CrystalMetamorphic_Touched_MultiplayerPatch {
    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        return TranspilerHelpers.PatchCoroutines(instructions, __originalMethod, null, new HashSet<string> { "FlashBrief" });
    }

    static bool Prefix(CrystalMetamorphic __instance, GameObject touched) {
        return !Net.IsRunning || Net.IsServer;
    }
}





[HarmonyPatch(typeof(CrystalSeptic), nameof(CrystalSeptic.Update))]
internal static class CrystalSeptic_Update_MultiplayerPatch {

    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        var codes = instructions;

        codes = BodyGetterOverrider.TranspileAll(codes, generator, __originalMethod.DeclaringType);
        codes = TranspilerHelpers.PatchShakers(codes, generator, __originalMethod);

        return codes;
    }

    static bool Prefix(CrystalSeptic __instance) {
        if (!Net.IsRunning) return true;

        if (!Net.IsServer || __instance.crystal == null || !SharedMain.LocalWorldIsGenerated) return false;
        foreach (var nb in NetBody.GetBodiesInRadius(__instance.crystal.transform.position, 12f * __instance.crystal.crystalSize))
            if (nb.body != null) nb.body.septicShock += Time.deltaTime * 0.5f;
        return false;
    }
}



[HarmonyPatch(typeof(CrystalTeleport), nameof(CrystalTeleport.Touched))]
internal static class CrystalTeleport_Touched_MultiplayerPatch {

    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        var codes = instructions;

        codes = BodyGetterOverrider.TranspileAll(codes, generator, __originalMethod.DeclaringType);
        codes = TranspilerHelpers.PatchShakers(codes, generator, __originalMethod);
        codes = TranspilerHelpers.PatchCoroutines(codes, __originalMethod, null, new HashSet<string> { "FlashBrief" });

        return codes;
    }

    static bool Prefix(CrystalTeleport __instance) {
        return !Net.IsRunning || Net.IsServer && __instance.crystal != null;
    }
}

[HarmonyPatch(typeof(CrystalUnstable), nameof(CrystalUnstable.Hit))]
internal static class CrystalUnstable_Touched_MultiplayerPatch {

    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        var codes = instructions;

        codes = BodyGetterOverrider.TranspileAll(codes, generator, __originalMethod.DeclaringType);
        codes = TranspilerHelpers.PatchShakers(codes, generator, __originalMethod);
        codes = TranspilerHelpers.PatchCoroutines(codes, __originalMethod, null, new HashSet<string> { "FlashBrief" });

        return codes;
    }

    static bool Prefix(CrystalUnstable __instance) {
        return !Net.IsRunning || Net.IsServer && __instance.crystal != null;
    }
}

#endif







