#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using System.Collections.Generic;
using System.Reflection.Emit;
using UnityEngine;

namespace Together;

// The 1.0.2 magazine-to-direct-feed path must use the validated actor, not the host camera.
[HarmonyPatch(typeof(GunScript), nameof(GunScript.LoadMag))]
internal static class GunLoadActor102 {
    static Body Actor(PlayerCamera camera) => Body_CombineItems_MultiplayerPatch.GunLoadingBody ?? camera.body;
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        foreach (var code in instructions) {
            if (code.LoadsField(AccessTools.Field(typeof(PlayerCamera), nameof(PlayerCamera.body)))) {
                code.opcode = OpCodes.Call;
                code.operand = AccessTools.Method(typeof(GunLoadActor102), nameof(Actor));
            }
            yield return code;
        }
    }
}

// Native customizer callbacks run outside HandleWhileDragging.
[HarmonyPatch(typeof(PlayerCamera), "Start")]
internal static class ItemCustomizerSync102 {
    static void Postfix(PlayerCamera __instance) {
        var camera = __instance;
        camera.favouriteButton.onClick.AddListener(() => PlayerCamera_Favourite_MultiplayerPatch.Send(camera.customizedItem));
        foreach (var swatch in camera.colorSwatches)
            swatch.onClick.AddListener(() => PlayerCamera_Favourite_MultiplayerPatch.Send(camera.customizedItem));
        camera.customItemLabel.onValueChanged.AddListener(_ => PlayerCamera_Favourite_MultiplayerPatch.Send(camera.customizedItem));
    }
}
#endif
