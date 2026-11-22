#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using UnityEngine;

namespace Together;

// The host owns the save/continue panel, but vanilla only opens it when the
// local host body is at the exit. In multiplayer the rule is percentage-based:
// once enough *other* living players reached the bottom, the host must see the
// panel too, even while elsewhere or dead. The native Update pass can overwrite
// that state every frame; re-assert it after Update to stop the panel flicker.
[HarmonyPatch(typeof(WorldGeneration), "Update")]
[HarmonyPriority(Priority.Last)]
internal static class WorldGeneration_Update_ForceHostSavePanel {
    static void Postfix(WorldGeneration __instance) {
        if (!Net.IsServer || !Net.IsRunning || Net.IsDedicatedServer) return;
        if (!Util.IsInWorld() || !Util.IsWorldGenerated()) return;
        if (__instance == null || !ReferenceEquals(WorldGeneration.world, __instance)) return;
        if (__instance.savePanel == null || __instance.generatingWorld || !__instance.worldExists) return;
        if (Traverse.Create(__instance).Field("doingRegen").GetValue<bool>()) return;
        if (SavesystemPatch.PreserveSave || SaveSlots.Busy || SaveSlotMenu.IsOpen || SaveCurrentLayerExit.IsSaving) return;

        bool show = !LayerExitWhenHostDead.ExitBlocked() && ServerMain.CheckIfEnoughPeopleAreAtLayerFinish();
        if (__instance.savePanel.activeSelf != show)
            __instance.savePanel.SetActive(show);
    }
}
#endif
