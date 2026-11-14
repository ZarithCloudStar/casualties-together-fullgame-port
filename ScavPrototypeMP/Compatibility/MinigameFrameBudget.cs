using HarmonyLib;
using System.Collections.Generic;
using System.Reflection.Emit;
using CasualtiesTogetherUtils;

namespace Together;

// Fullgame's catch-up loop subtracts one from a float until it reaches one.
// Infinity (or a value whose ULP exceeds one) never decreases. Bound each frame
// before entering that loop; do not move Unity callbacks onto a worker thread.
[HarmonyPatch(typeof(MinigameBase), "ActiveMinigameUpdate")]
internal static class MinigameFrameBudget {
    static float BoundSteps(float steps) {
        if (!Net.IsRunning) return steps;
        if (float.IsNaN(steps) || steps < 0f) return 0f;
        if (steps > 8f) {
            log.warn("[minigame-guard] discarded excess physics backlog=" + steps);
            return 8f;
        }
        return steps;
    }
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        var accumulator = AccessTools.Field(typeof(MinigameBase), "physicsUpdateTime");
        var bound = AccessTools.Method(typeof(MinigameFrameBudget), nameof(BoundSteps));
        foreach (var instruction in instructions) {
            if (instruction.opcode == OpCodes.Stfld && Equals(instruction.operand, accumulator)) {
                var call = new CodeInstruction(OpCodes.Call, bound);
                call.labels.AddRange(instruction.labels);
                instruction.labels.Clear();
                yield return call;
            }
            yield return instruction;
        }
    }
}
