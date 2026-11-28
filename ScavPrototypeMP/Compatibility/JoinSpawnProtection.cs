#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace Together;

internal sealed class JoinSpawnProtection {
    const double TimeoutSeconds = 240; // world generation can take 180s, followed by body restore
    const double SettleSeconds = 1;

    readonly WorldGeneration world = WorldGeneration.world;
    readonly object session = Net.ServerEntityManager;
    readonly string generation = WorldSnapshotTransfer.Token;
    readonly double deadline = Time.realtimeSinceStartupAsDouble + TimeoutSeconds;
    double releaseAt = double.PositiveInfinity;
    bool ended;

    bool IsCurrent => ReferenceEquals(world, WorldGeneration.world) &&
        ReferenceEquals(session, Net.ServerEntityManager) && generation == WorldSnapshotTransfer.Token;

    static WorldGeneration clientWorld;
    static double clientDeadline;

    internal static void BeginGeneration() {
        if (!Util.GameIsFull()) return;
        if (Net.IsServer) {
            foreach (var player in ScavPlayer.ClientIdToPlayerDict.Values) Begin(player);
        } else if (Net.IsClient) {
            clientWorld = WorldGeneration.world;
            clientDeadline = Time.realtimeSinceStartupAsDouble + TimeoutSeconds;
        }
    }

    internal static void Begin(ScavPlayer player) {
        if (!Net.IsServer || !Util.GameIsFull() || player == null || player.IsHost || player.IsLocal ||
            player.ServerPlayerState is not { } state || WorldGeneration.world == null) return;
        if (state.joinSpawnProtection?.IsCurrent != true) state.joinSpawnProtection = new JoinSpawnProtection();
    }

    internal static void Cancel(ScavPlayer player) {
        if (player.ServerPlayerState?.joinSpawnProtection is { } window) window.ended = true;
    }

    // The client sends character frames once its native worldgen has closed the loading screen.
    // The earlier FinishedWorldgen request still waits for spawn and the native finish coroutine.
    internal static void OnClientFrame(Server_PlayerState state) {
        if (state.finished_worldgen && state.spawn_placement_completed && !DeferredPlayerSaveRestore.InProgress &&
            state.joinSpawnProtection is { ended: false } window && window.IsCurrent &&
            double.IsPositiveInfinity(window.releaseAt)) {
            window.releaseAt = Time.realtimeSinceStartupAsDouble + SettleSeconds;
        }
    }

    internal static bool BlocksDamage(Body body) {
        if (!Net.IsRunning || !Util.GameIsFull() || body == null) return false;
        double now = Time.realtimeSinceStartupAsDouble;
        if (Net.IsClient) {
            // Only suppress local prediction during this peer's loading window.
            // The server's health packets still apply saved injuries, including dead saves.
            return body == ScavPlayer.LocalPlayer?.body && clientWorld != null &&
                ReferenceEquals(clientWorld, WorldGeneration.world) && !WorldSnapshotTransfer.Failed &&
                now < clientDeadline && (SharedMain.LocalWorldIsGenerating ||
                now < SharedMain.LastWorldgenFinishTime + SettleSeconds);
        }
        if (!ScavPlayer.BodyToPlayerDict.TryGetValue(body, out var player) ||
            player.ServerPlayerState?.joinSpawnProtection is not { ended: false } window) return false;
        if (!window.IsCurrent || WorldSnapshotTransfer.Failed || now >= window.deadline || now >= window.releaseAt) {
            window.ended = true;
            return false;
        }
        return true;
    }
}

// Limb.Update drains blood and advances infection independently of Body.Update.
[HarmonyPatch(typeof(Limb), "Update")]
internal static class JoinSpawnLimbVitalsPatch {
    static bool Prefix(Limb __instance) => !JoinSpawnProtection.BlocksDamage(__instance.body);
}

[HarmonyPatch]
internal static class JoinSpawnDrugVitalsPatch {
    static IEnumerable<MethodBase> TargetMethods() {
        yield return AccessTools.Method(typeof(Painkillers), "Update");
        yield return AccessTools.Method(typeof(Antidepressants), "Update");
        yield return AccessTools.Method(typeof(SleepingPills), "Update");
    }

    static bool Prefix(Body ___body) => !JoinSpawnProtection.BlocksDamage(___body);
}

[HarmonyPatch]
internal static class JoinSpawnContactDamagePatch {
    static IEnumerable<MethodBase> TargetMethods() {
        yield return AccessTools.Method(typeof(CactusScript), "OnCollisionEnter2D");
        yield return AccessTools.Method(typeof(SawbladeScript), "OnCollisionEnter2D");
        yield return AccessTools.Method(typeof(SawbladeScript), "OnCollisionStay2D");
    }

    static bool Prefix(Collision2D __0) => !Utils.GetBody(__0.gameObject, out var body) ||
        !JoinSpawnProtection.BlocksDamage(body);
}

[HarmonyPatch(typeof(BarbedFence), "OnTriggerEnter2D")]
internal static class JoinSpawnFenceDamagePatch {
    static bool Prefix(Collider2D collision) => !Utils.GetBody(collision.gameObject, out var body) ||
        !JoinSpawnProtection.BlocksDamage(body);
}

[HarmonyPatch(typeof(BloodSuckerScript), "OnWillRenderObject")]
internal static class JoinSpawnBloodDrainPatch {
    static bool Prefix(Limb ___limb) => !JoinSpawnProtection.BlocksDamage(___limb == null ? null : ___limb.body);
}

// These native attacks select victims through Utils.GetBody and then write wounds directly.
[HarmonyPatch]
internal static class JoinSpawnNativeTargetPatch {
    static IEnumerable<MethodBase> TargetMethods() {
        yield return AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(GuardScript), "MeleeAttack"));
        yield return AccessTools.Method(typeof(FanTrap), "OnWillRenderObject");
        yield return AccessTools.Method(typeof(BloodSuckerScript), "OnTriggerEnter2D");
    }

    static bool GetDamageTarget(GameObject target, out Body body) =>
        Utils.GetBody(target, out body) && !JoinSpawnProtection.BlocksDamage(body);

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        var lookup = AccessTools.Method(typeof(Utils), nameof(Utils.GetBody));
        var replacement = AccessTools.Method(typeof(JoinSpawnNativeTargetPatch), nameof(GetDamageTarget));
        foreach (var instruction in instructions) {
            if (instruction.Calls(lookup)) instruction.operand = replacement;
            yield return instruction;
        }
    }
}

// Vanilla affects the local camera body before the MP postfix handles the roster.
// Keep the line's position/timer updates and gate just its two native body writes.
[HarmonyPatch(typeof(RadiationLine), nameof(RadiationLine.Update))]
internal static class JoinSpawnNativeRadiationPatch {
    static void SetRadiation(Body body, float value) {
        if (!JoinSpawnProtection.BlocksDamage(body)) body.radiationSickness = value;
    }

    static void SetEyeScare(Body body, float value) {
        if (!JoinSpawnProtection.BlocksDamage(body)) body.eyeScareTime = value;
    }

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        var radiation = AccessTools.Field(typeof(Body), nameof(Body.radiationSickness));
        var scare = AccessTools.Field(typeof(Body), nameof(Body.eyeScareTime));
        foreach (var instruction in instructions) {
            if (instruction.opcode == OpCodes.Stfld && Equals(instruction.operand, radiation)) {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(JoinSpawnNativeRadiationPatch), nameof(SetRadiation));
            } else if (instruction.opcode == OpCodes.Stfld && Equals(instruction.operand, scare)) {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(JoinSpawnNativeRadiationPatch), nameof(SetEyeScare));
            }
            yield return instruction;
        }
    }
}
#endif
