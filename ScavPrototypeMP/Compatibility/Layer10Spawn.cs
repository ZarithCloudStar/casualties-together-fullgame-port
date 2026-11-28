#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace Together;

// The native finish creates a solid building and lifts the local body. Let the
// host create and sync one pod, then publish a spawn clear of its collider.
[HarmonyPatch]
internal static class Layer10Spawn {
    internal const string PodId = "highpowereddrillpodbroken";
    const float NativeLift = 6f;
    const float Clearance = 0.25f;
    static Bounds podBounds;
    static Vector2 safeSpawn;

    internal static bool Applies => Util.GameIsFull() && WorldGeneration.world != null && WorldGeneration.world.biomeDepth == 10;
    internal static bool Ready { get; private set; }

    internal static void Reset() => Ready = false;

    static MethodBase TargetMethod() => AccessTools.EnumeratorMoveNext(
        AccessTools.Method(typeof(WorldGeneration), nameof(WorldGeneration.FinishWorldGeneration)));

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        var create = AccessTools.Method(typeof(Utils), nameof(Utils.Create), new[] { typeof(string), typeof(Vector2), typeof(float) });
        var createPod = AccessTools.Method(typeof(Layer10Spawn), nameof(CreatePod));
        var lift = AccessTools.Method(typeof(Layer10Spawn), nameof(ResolveLift));
        bool foundPod = false, foundLift = false;
        foreach (var instruction in instructions) {
            if (instruction.opcode == OpCodes.Ldstr && Equals(instruction.operand, PodId)) foundPod = true;
            if (foundPod && instruction.Calls(create)) instruction.operand = createPod;
            yield return instruction;
            if (foundPod && !foundLift && instruction.opcode == OpCodes.Ldc_R4 && Equals(instruction.operand, NativeLift)) {
                foundLift = true;
                yield return new CodeInstruction(OpCodes.Call, lift);
            }
        }
        if (!foundLift) log.error("[spawn] native layer10 pod/lift pattern missing");
    }

    static float ResolveLift(float lift) => Net.IsRunning && Net.IsClient && Applies ? 0f : lift;

    static GameObject CreatePod(string id, Vector2 origin, float angle) {
        if (!Net.IsRunning || !Applies || id != PodId) return Utils.Create(id, origin, angle);
        if (Net.IsClient) return null; // BuildingEntity sync supplies the host's pod.

        if (Body_PlaceBody_MultiplayerPatch.has_spawn_location) origin = Body_PlaceBody_MultiplayerPatch.SpawnLocation;
        var pod = Utils.Create(id, origin, angle);
        Physics2D.SyncTransforms();
        podBounds = pod.GetComponent<Collider2D>().bounds;
        float bodyBelowRoot = 0f;
        for (int character = 0; character < 3; character++) {
            var body = ServerMain.GetCharacterPrefab(character).GetComponentInChildren<Body>(true);
            var collider = body.GetComponent<BoxCollider2D>();
            bodyBelowRoot = Mathf.Max(bodyBelowRoot, (collider.size.y * 0.5f + collider.edgeRadius - collider.offset.y) *
                Mathf.Abs(collider.transform.lossyScale.y));
        }
        safeSpawn = new Vector2(origin.x, Mathf.Max(origin.y + NativeLift, podBounds.max.y + bodyBelowRoot + Clearance));
        Body_PlaceBody_MultiplayerPatch.SpawnLocation = safeSpawn;
        Body_PlaceBody_MultiplayerPatch.has_spawn_location = true;
        Ready = true;
        log.l($"[spawn] layer10 pod={origin:F3} top={podBounds.max.y:F3} shared={safeSpawn:F3}");
        return pod;
    }

    internal static void PlaceConnectedPlayers() {
        if (!Net.IsServer || !Applies || !Ready) return;
        foreach (var player in ScavPlayer.ClientIdToPlayerDict.Values) {
            if (player.body == null || !player.body.alive || player.ServerPlayerState.spawn_placement_completed ||
                PlayerPlacementCheckpoint.HasAppliedPose(player)) continue;
            player.ResetEntropy();
            if (!MapPoses.TryApply(player.body)) player.Server_TeleportCharacter(safeSpawn);
        }
    }

    internal static bool IsUnsafeCompanionSpawn(Vector2 position, Body guest) => Applies && Ready &&
        position.y < safeSpawn.y && position.x >= podBounds.min.x - guest.origColSize.x &&
        position.x <= podBounds.max.x + guest.origColSize.x;

}
#endif
