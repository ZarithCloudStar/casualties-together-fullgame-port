#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using UnityEngine;

namespace Together;

// The 1.0.2 magazine-to-direct-feed path must use the validated actor, not the host camera.
[HarmonyPatch(typeof(GunScript), nameof(GunScript.LoadMag))]
internal static class GunLoadActor102 {
    internal static Body Actor(PlayerCamera camera, GunScript gun) {
        if (!Net.IsRunning) return camera?.body;
        var actor = AmmoActor102.Resolve(gun);
        if (actor != null) return actor;
        AmmoActor102.WarnUnknownActor(gun);
        return camera?.body;
    }
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        int replacements = 0;
        foreach (var code in instructions) {
            if (code.LoadsField(AccessTools.Field(typeof(PlayerCamera), nameof(PlayerCamera.body)))) {
                replacements++;
                // Keep any labels on the first replacement instruction.
                var loadGun = new CodeInstruction(OpCodes.Ldarg_0);
                loadGun.labels.AddRange(code.labels); code.labels.Clear();
                loadGun.blocks.AddRange(code.blocks); code.blocks.Clear();
                yield return loadGun;
                code.opcode = OpCodes.Call;
                code.operand = AccessTools.Method(typeof(GunLoadActor102), nameof(Actor));
            }
            yield return code;
        }
        if (replacements == 0) throw new InvalidOperationException("Ammo actor patch found no PlayerCamera.body field access.");
    }
}

// Close captures the final label, swatch and favourite after the native editor releases selection.
[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.CloseItemCustomizer))]
internal static class ItemCustomizerCloseSync102 {
    static void Prefix(PlayerCamera __instance, out Item __state) => __state = Net.IsRunning ? __instance.customizedItem : null;
    static void Postfix(Item __state) { if (__state != null) PlayerCamera_Favourite_MultiplayerPatch.Send(__state); }
}

// Native customizer callbacks run outside HandleWhileDragging.
[HarmonyPatch(typeof(PlayerCamera), "Start")]
internal static class ItemCustomizerSync102 {
    static void Postfix(PlayerCamera __instance) {
        var camera = __instance;
        Item pendingLabelItem = null;
        camera.favouriteButton.onClick.AddListener(() => PlayerCamera_Favourite_MultiplayerPatch.Send(camera.customizedItem));
        foreach (var swatch in camera.colorSwatches)
            swatch.onClick.AddListener(() => PlayerCamera_Favourite_MultiplayerPatch.Send(camera.customizedItem));
        // Native CloseItemCustomizer clears customizedItem before releasing
        // selection, so retain the edited item until onEndEdit commits it.
        camera.customItemLabel.onValueChanged.AddListener(_ => {
            if (Net.IsRunning) pendingLabelItem = camera.customizedItem;
        });
        camera.customItemLabel.onEndEdit.AddListener(_ => {
            var edited = pendingLabelItem;
            pendingLabelItem = null;
            PlayerCamera_Favourite_MultiplayerPatch.Send(edited);
        });
    }
}
#endif
