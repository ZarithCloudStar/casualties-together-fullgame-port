#if FULLGAME
using CasualtiesTogetherUtils;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Together;

// Session-scoped checkpoint poses outlive the initial deferred inventory pass.
// A guest can finish WS1, or join the host, after that pass has already ended.
internal static class PlayerPlacementCheckpoint {
    internal sealed class Pose {
        public float x, y;
        public bool right;
        public int layer;
    }
    static Dictionary<string, Pose> saved = new();
    static object session;
    static WorldGeneration world;
    static bool queued;
    static string generation;
    static readonly Dictionary<ScavPlayer, Body> applied = new();

    internal static Dictionary<string, Pose> Capture() {
        var poses = new Dictionary<string, Pose>();
        foreach (var p in ScavPlayer.ClientIdToPlayerDict.Values.ToArray()) {
            if (p == null) continue;
            if (p.body == null) throw new InvalidOperationException("Connected player has no ready checkpoint body: " + p.playerName);
            // Capture the same root the restore path teleports; netBody.GetPosition()
            // is the torso centre and made every restore ~1.8 tiles low.
            var at = (Vector2)p.body.transform.position;
            if (!Finite(at.x) || !Finite(at.y)) throw new InvalidDataException("Nonfinite player checkpoint: " + p.playerName);
            poses.Add(p.GetPersistentId(), new Pose { x = at.x, y = at.y, right = p.body.isRight, layer = WorldGeneration.world.biomeDepth });
            log.l($"CHECKPOINT: pose player={p.GetPersistentId()} local={p.IsLocal} bodyIndex={ScavPlayer.BodyToPlayerDict.ContainsKey(p.body)} layer={WorldGeneration.world.biomeDepth} x={at.x:F3} y={at.y:F3} right={p.body.isRight}");
        }
        return poses;
    }
    internal static void Load(string folder, bool midlevel) {
        saved.Clear(); applied.Clear();
        session = Net.ServerEntityManager; world = WorldGeneration.world; queued = true; generation = null;
        string path = Path.Combine(folder, SavesystemPatch.mpsaveheaderfile);
        if (!midlevel || !File.Exists(path)) return;
        var root = JObject.Parse(File.ReadAllText(path));
        if (root["PLRPOSE"] is JObject poses) saved = poses.ToObject<Dictionary<string, Pose>>();
        else if (root["PLRPOS"] is JObject legacy) foreach (var entry in legacy.Properties()) {
            float x, y;
            if (entry.Value is JObject pair) { x = pair.Value<float>("Item1"); y = pair.Value<float>("Item2"); }
            else if (entry.Value is JArray array && array.Count >= 2) { x = array[0].Value<float>(); y = array[1].Value<float>(); }
            else continue;
            saved[entry.Name] = new Pose { x = x, y = y, right = true, layer = world.biomeDepth };
        }
        foreach (var pose in saved.Values)
            if (pose == null || !Finite(pose.x) || !Finite(pose.y) || pose.layer != world.biomeDepth)
                throw new InvalidDataException("Invalid checkpoint player pose/layer.");
        log.l($"LOAD: authoritative player poses={saved.Count} layer={world.biomeDepth}");
    }
    internal static void BeginGeneration() {
        if (!queued || !ReferenceEquals(session, Net.ServerEntityManager) || !ReferenceEquals(world, WorldGeneration.world)) saved.Clear();
        queued = false; applied.Clear(); generation = WorldSnapshotTransfer.Token;
    }
    internal static bool TryApply(ScavPlayer p, bool force = false) {
        if (!Net.IsServer || p == null || p.body == null || !ReferenceEquals(session, Net.ServerEntityManager) ||
            !ReferenceEquals(world, WorldGeneration.world) || generation != WorldSnapshotTransfer.Token ||
            !saved.TryGetValue(p.GetPersistentId(), out var pose) || pose.layer != world.biomeDepth) return false;
        if (!force && applied.TryGetValue(p, out var body) && ReferenceEquals(body, p.body)) return true;
        // Place a standing body root. A preceding inventory restore can leave
        // it ragdolled; later ResetEntropy/Stand otherwise moves the root by
        // the torso offset after we have already marked its pose as applied.
        p.ResetEntropy();
        p.body.ForceSwitchDir(pose.right);
        p.body.isRight = pose.right;
        p.Server_TeleportCharacter(new Vector2(pose.x, pose.y));
        p.body.SetVelocity(Vector2.zero);
        applied[p] = p.body;
        p.ServerPlayerState.did_give_spawn_location_from_a_save = true;
        log.l($"[spawn] checkpoint authoritative player={p.GetPersistentId()} layer={pose.layer} x={pose.x:F3} y={pose.y:F3} right={pose.right}");
        return true;
    }
    static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
}
#endif
