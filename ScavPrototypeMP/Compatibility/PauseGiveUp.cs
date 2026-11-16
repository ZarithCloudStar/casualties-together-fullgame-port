#if FULLGAME
using HarmonyLib;
using System.Collections.Generic;
using System.Reflection.Emit;

namespace Together;

// Keep vanilla's four confirmation clicks, but never leave the room here.
[HarmonyPatch(typeof(PauseHandler), nameof(PauseHandler.TryQuit))]
internal static class PauseGiveUp {
    static bool QuitDirectly() => !Net.IsRunning && WorldGeneration.unchipped;

    static void GiveUp(PlayerCamera camera) {
        if (!Net.IsRunning) { camera.ToMainMenu(); return; }
        if (ScavPlayer.LocalPlayer?.IsAlive() != true || camera.selfDestructActive) return;
        if (PauseHandler.paused) PauseHandler.TogglePause();
        camera.StartSelfDestruct();
    }

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        foreach (var code in instructions) {
            if (code.Calls(AccessTools.PropertyGetter(typeof(WorldGeneration), nameof(WorldGeneration.unchipped)))) {
                code.opcode = OpCodes.Call;
                code.operand = AccessTools.Method(typeof(PauseGiveUp), nameof(QuitDirectly));
            } else if (code.Calls(AccessTools.Method(typeof(PlayerCamera), nameof(PlayerCamera.ToMainMenu)))) {
                code.opcode = OpCodes.Call;
                code.operand = AccessTools.Method(typeof(PauseGiveUp), nameof(GiveUp));
            }
            yield return code;
        }
    }
}
#endif
