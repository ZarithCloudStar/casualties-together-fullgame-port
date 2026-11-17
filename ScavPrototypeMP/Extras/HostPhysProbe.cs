using System.Collections.Generic;
using System.Diagnostics;
using CasualtiesTogetherUtils;
using UnityEngine;

namespace Together;

// Adapted 5.5.3 diagnostic: debug call sites only, no physics writes and at most eight samples/second.
internal static class HostPhysProbe {
    sealed class Sample {
        internal Vector2 Raw0, Raw, Visual0, Visual;
        internal float Up, HostVy, Age;
        internal int Notes, Snaps;
        internal string Name;
    }
    static readonly Dictionary<ushort, Sample> Clients = new Dictionary<ushort, Sample>();
    static readonly Collider2D[] Hits = new Collider2D[64];
    static readonly HashSet<int> Seen = new HashSet<int>();
    static float next;
    static bool Players(out Vector2 host, out Vector2 local) {
        host = local = Vector2.zero;
        bool hasHost = false, hasLocal = false;
        foreach (var pair in ScavPlayer.BodyToPlayerDict) {
            if (pair.Key == null || pair.Value == null) continue;
            if (pair.Value.IsHost) { host = pair.Key.transform.position; hasHost = true; }
            if (pair.Value.IsLocal) { local = pair.Key.transform.position; hasLocal = true; }
        }
        return hasHost && hasLocal;
    }
    [Conditional("DEBUG")]
    internal static void NoteClient(SyncInfo si, Vector2 before, Vector2 applied, bool snap) {
        if (!Net.IsClient || si == null || si.go == null || !Players(out var host, out var local)
            || (local - host).sqrMagnitude <= 1600f) return;
        if (!Clients.TryGetValue(si.syncId.id, out var sample)) {
            sample = new Sample { Raw0 = si._position.Value, Visual0 = before, Visual = before, Name = si.GetItemID() };
            Clients[si.syncId.id] = sample;
        }
        sample.Up += Mathf.Max(0f, applied.y - sample.Visual.y);
        sample.Raw = si._position.Value;
        sample.Visual = applied;
        sample.HostVy = si._velocity.Value.y;
        sample.Age = (float)(Time.realtimeSinceStartupAsDouble - si.lastPositionUpdateTime);
        sample.Notes++;
        if (snap) sample.Snaps++;
    }
    [Conditional("DEBUG")]
    internal static void Tick() {
        if (!Net.IsRunning || !Util.IsWorldGenerated()) { Clients.Clear(); return; }
        if (Time.unscaledTime < next) return;
        next = Time.unscaledTime + 1f;
        if (!Players(out var host, out var local)) return;
        int emitted = 0;
        if (Net.IsClient) {
            foreach (var pair in Clients) {
                var sample = pair.Value;
                if (emitted++ >= 8) break;
                Plugin.log.LogInfo($"[fall] client id={pair.Key} {sample.Name} hostDist={Vector2.Distance(sample.Visual, host):0} rawDy={sample.Raw.y - sample.Raw0.y:0.00} visDy={sample.Visual.y - sample.Visual0.y:0.00} up={sample.Up:0.00} hostVy={sample.HostVy:0.00} gap={Vector2.Distance(sample.Raw, sample.Visual):0.00} age={sample.Age:0.00} snaps={sample.Snaps} notes={sample.Notes}");
            }
            Clients.Clear();
            return;
        }
        Seen.Clear();
        foreach (var pair in ScavPlayer.BodyToPlayerDict) {
            if (pair.Key == null || pair.Value == null || pair.Value.IsLocal) continue;
            Vector2 pos = pair.Key.transform.position;
            if ((pos - host).sqrMagnitude < 1600f) continue;
            int count = Physics2D.OverlapCircleNonAlloc(pos, 22f, Hits);
            for (int i = 0; i < count && emitted < 8; i++) {
                var rb = Hits[i] != null ? Hits[i].attachedRigidbody : null;
                if (rb == null || rb.GetComponentInParent<Body>() != null || !Seen.Add(rb.GetInstanceID())) continue;
                if (rb.bodyType != RigidbodyType2D.Dynamic && rb.velocity.sqrMagnitude < 0.04f) continue;
                emitted++;
                Plugin.log.LogInfo($"[fall] host {rb.gameObject.name} hostDist={Vector2.Distance(rb.position, host):0} plrDist={Vector2.Distance(rb.position, pos):0.0} vy={rb.velocity.y:0.00} grav={rb.gravityScale:0.00} inSim={SharedMain.WorldInSimRange(rb.position)} stamp={SharedMain.PositionIsInSimulatedChunk(rb.position)} sim={rb.simulated} sleep={rb.IsSleeping()} body={rb.bodyType}");
            }
        }
    }
}
