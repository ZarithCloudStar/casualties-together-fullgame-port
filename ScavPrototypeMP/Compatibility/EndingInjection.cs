#if FULLGAME
using HarmonyLib;
using System;

namespace Together;

// Capture the actual drain before vanilla consumes the liquid stack.
[HarmonyPatch(typeof(WaterContainerItem), nameof(WaterContainerItem.ApplyToLimb))]
internal static class WaterContainerItem_ApplyToLimb_AnesthesiaPatch {
    static void Prefix(WaterContainerItem __instance, Limb limb, float amount, out float __state) {
        if (Net.IsRunning && limb?.body != null) limb.GetCoUtilsInstance().SelectForMPOverride();
        __state = AnestheticDose(__instance, amount);
    }
    static void Postfix(float __state, Limb limb) => EndingFlow.NotifyAnesthesia(limb, __state);
    internal static float AnestheticDose(WaterContainerItem item, float amount) {
        if (item?.stack == null) return 0f;
        var drain = item.CalculateDrain(amount);
        float ml = 0f;
        for (int i = 0; i < drain.Count; i++)
            if (item.stack[i].liquidId == "localanesthetic") ml += drain[i];
        return ml;
    }
}
[HarmonyPatch(typeof(WaterContainerItem), nameof(WaterContainerItem.Inject))]
internal static class WaterContainerItem_Inject_AnesthesiaPatch {
    static void Prefix(WaterContainerItem __instance, float amount, out float __state) => __state = WaterContainerItem_ApplyToLimb_AnesthesiaPatch.AnestheticDose(__instance, amount);
    static void Postfix(float __state, Limb limb) => EndingFlow.NotifyAnesthesia(limb, __state);
}

[HarmonyPatch(typeof(CoUtils), nameof(CoUtils.DoTimedOp), new[] { typeof(string), typeof(Action), typeof(float) })]
internal static class Ending_AnesthesiaEffectProbe {
    static void Postfix(CoUtils __instance, string id) {
        if (EndingFlow.DuringEscape && id.StartsWith("localanesthetic"))
            log.l($"[anesth] effect id={id} remaining={__instance.DurationOf(id):0.000} player={__instance.GetPlayer()?.GetPersistentId()} server={Net.IsServer}");
        if (id == "localanestheticDownTorso") EndingFlow.ObserveTorsoAnesthesia(__instance);
    }
}

// Body has no Inject/ApplyToLimb entry; UseItem and CombineLiquids are its
// item-treatment/liquid entry points. Actual wound treatment is in Medical.cs.
[HarmonyPatch(typeof(Body), nameof(Body.UseItem))]
internal static class Ending_BodyTreatmentProbe {
    static void Prefix(Body __instance, Item item) {
        if (EndingFlow.DuringEscape) log.l($"[anesth] Body.UseItem body={__instance.name} item={item?.id}");
    }
}
[HarmonyPatch(typeof(Body), nameof(Body.CombineLiquids))]
internal static class Ending_BodyLiquidProbe {
    static void Prefix(Body __instance, WaterContainerItem wat1, WaterContainerItem wat2, float amt) {
        if (EndingFlow.DuringEscape) log.l($"[anesth] Body.CombineLiquids body={__instance.name} from={wat2?.name} to={wat1?.name} ml={amt}");
    }
}
#endif
