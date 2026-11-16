#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Together;

internal static class GuardTargeting {
    const float TargetRange = 60f, SwitchAdvantage = 6f, SwitchInterval = 1f;
    sealed class TargetTrace {
        internal int frame = -1;
        internal Body target, loggedTarget;
        internal float nextLog, nextSwitch;
    }
    static readonly ConditionalWeakTable<GuardScript, TargetTrace> traces = new();

    internal static Body Current(GuardScript guard) {
        if (!Net.IsRunning || !Net.IsServer) return guard.body;
        var trace = traces.GetValue(guard, _ => new TargetTrace());
        // Update reads body many times. Keep one deterministic choice per frame.
        if (trace.frame != Time.frameCount) {
            trace.frame = Time.frameCount;
            Vector2 position = guard.transform.position;
            var nearest = EndingGuardSync.NearestLivingPlayer(position);
            bool valid = trace.target != null && ScavPlayer.BodyToPlayerDict.TryGetValue(trace.target, out var owner)
                && owner != null && owner.IsAlive()
                && ScavPlayer.ClientIdToPlayerDict.TryGetValue(owner.playerId, out var registered) && registered == owner;
            float distance = valid ? Vector2.Distance(position, trace.target.transform.position) : float.PositiveInfinity;
            bool clearlyCloser = nearest != null && nearest != trace.target
                && Vector2.Distance(position, nearest.transform.position) + SwitchAdvantage < distance;
            // Lost/dead/distant targets can be replaced immediately. Otherwise
            // similar distances must not flip the aim back and forth each frame.
            if (!valid || distance > TargetRange || (Time.unscaledTime >= trace.nextSwitch && clearlyCloser)) {
                if (trace.target != nearest) trace.nextSwitch = Time.unscaledTime + SwitchInterval;
                trace.target = nearest;
            }
        }
        if (trace.target != trace.loggedTarget && Time.unscaledTime >= trace.nextLog) {
            trace.loggedTarget = trace.target;
            trace.nextLog = Time.unscaledTime + 2f;
            ScavPlayer player = null;
            if (trace.target != null) ScavPlayer.BodyToPlayerDict.TryGetValue(trace.target, out player);
            log.l($"[guard-target] guard={guard.GetInstanceID()} target={player?.playerName ?? "vanilla"} playerId={player?.playerId} host={player?.IsHost}");
        }
        return trace.target ?? guard.body;
    }

    internal static Body SpawnerCurrent(GuardSpawner spawner) => Net.IsRunning && Net.IsServer
        ? EndingGuardSync.NearestLivingPlayer(spawner.transform.position) ?? spawner.body : spawner.body;
}

// Patch call sites rather than the tiny private getter that Mono may inline.
// Iterator bodies execute in MoveNext, not in the IEnumerator factory method.
[HarmonyPatch]
internal static class GuardTargeting_CallSitesPatch {
    static IEnumerable<MethodBase> TargetMethods() {
        foreach (string name in new[] { "Start", "Update", "MeleeAttack", "Fire", "ReticleOverlapsPlayer" })
            yield return AccessTools.DeclaredMethod(typeof(GuardScript), name);
        foreach (var nested in typeof(GuardScript).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)) {
            var moveNext = AccessTools.DeclaredMethod(nested, "MoveNext");
            if (moveNext != null) yield return moveNext;
        }
    }

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) {
        var getter = AccessTools.PropertyGetter(typeof(GuardScript), "body");
        int replaced = 0;
        foreach (var code in instructions) {
            if (code.Calls(getter)) {
                code.opcode = OpCodes.Call;
                code.operand = AccessTools.Method(typeof(GuardTargeting), nameof(GuardTargeting.Current));
                replaced++;
            }
            yield return code;
        }
        if (replaced > 0) log.l($"[guard-target] patched {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name} calls={replaced}");
    }
}

[HarmonyPatch(typeof(GuardSpawner), "Update")]
internal static class GuardSpawnerTargeting_CallSitesPatch {
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        var getter = AccessTools.PropertyGetter(typeof(GuardSpawner), "body");
        foreach (var code in instructions) {
            if (code.Calls(getter)) {
                code.opcode = OpCodes.Call;
                code.operand = AccessTools.Method(typeof(GuardTargeting), nameof(GuardTargeting.SpawnerCurrent));
            }
            yield return code;
        }
    }
}
#endif
