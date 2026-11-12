#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Together;

// Persist only the inputs to trader spawning/movement. Native Start/TraderCommit
// still own inventory generation, reputation and all live controller state.
internal static class TraderLoadRecovery {
    static int generation;
    static bool watching;
    static WorldGeneration observedWorld;
    static object observedSession;
    static int observedLayer;

    internal static void Capture(WorldObjectDocument document) {
        document.traders = new List<WorldTraderPlacement>();
        var all = Traders();
        var skipped = new Dictionary<string, int>();
        int inactive = 0, disabled = 0, dead = 0;
        foreach (var trader in all) {
            if (!Eligible(trader, out string reason)) {
                string key = $"res={Resource(trader)} reason={reason}";
                skipped.TryGetValue(key, out int count); skipped[key] = count + 1;
                if (reason == "dead") dead++;
                continue;
            }
            if (!trader.gameObject.activeInHierarchy) inactive++;
            if (!trader.enabled) disabled++;
            var position = trader.transform.position;
            document.traders.Add(new WorldTraderPlacement {
                resource = Resource(trader), position = new[] { position.x, position.y, position.z },
                desired = new[] { trader.desiredPos.x, trader.desiredPos.y },
                moveMin = trader.MoveRange.min, moveMax = trader.MoveRange.max,
                farEnoughToMove = trader.farEnoughToMove, didMove = trader.didMove
            });
            log.l($"SAVE-EXIT: trader placement res={Resource(trader)} pos=({position.x:F3},{position.y:F3}) range=({trader.MoveRange.min:F3},{trader.MoveRange.max:F3})");
        }
        foreach (var skip in skipped) log.warn($"SAVE-EXIT: trader placement skipped {skip.Key} count={skip.Value}");
        log.l($"SAVE-EXIT: trader placements captured count={document.traders.Count} found={all.Length} inactiveIncluded={inactive} disabledIncluded={disabled} skippedDead={dead} skippedInvalid={skipped.Values.Sum() - dead} inventory=not-saved");
    }

    internal static void Begin(WorldGeneration world, object session) {
        if (!Util.GameIsFull() || !Net.IsServer) return;
        generation++; watching = true; observedWorld = world; observedSession = session; observedLayer = world.biomeDepth;
        foreach (var trader in Traders()) Watch(trader, generation);
        Census("pre"); // generated positions, before our cleanup/repositioning
    }

    internal static void Queue(WorldGeneration world, object session, List<WorldTraderPlacement> placements) {
        Util.StartCoroutine(RestoreAndObserve(world, session, placements, generation));
    }

    static IEnumerator RestoreAndObserve(WorldGeneration world, object session, List<WorldTraderPlacement> placements, int token) {
        double started = Time.realtimeSinceStartupAsDouble;
        try {
            while (!Util.IsWorldGenerated() || DeferredPlayerSaveRestore.InProgress) {
                if (!Current(world, session, token)) { log.warn("LOAD: census cancelled before ready; world/session changed"); yield break; }
                if (Time.realtimeSinceStartupAsDouble - started > 180) { log.error("LOAD: census timed out waiting for world/player restoration"); yield break; }
                yield return null;
            }
            if (!Current(world, session, token)) yield break;
            var pool = Traders().Where(Usable).ToList();
            var pairs = new List<(WorldTraderPlacement placement, TraderScript trader)>();
            var positionedTraders = new HashSet<TraderScript>();
            int initialPresent = pool.Count;
            int deficit = placements == null ? 0 : placements.GroupBy(x => x.resource).Sum(group => Math.Max(0, group.Count() - pool.Count(t => Resource(t) == group.Key)));
            if (placements != null && (initialPresent < placements.Count || deficit > 0))
                log.warn($"LOAD: trader census before saved={placements.Count} present={initialPresent} positioned=0 missing={deficit}; missing types will use native spawning");
            int spawned = 0;
            if (placements == null) {
                log.warn("LOAD: traders legacy snapshot has no saved placements; generated traders are retained at generated positions, which may differ from restored rooms");
            } else foreach (var placement in placements) {
                try {
                    var trader = pool.Where(t => Resource(t) == placement.resource)
                        .OrderBy(t => DistanceSquared(t.transform.position, placement.position)).FirstOrDefault();
                    if (trader != null) pool.Remove(trader);
                    else {
                        var prefab = Resources.Load<GameObject>(placement.resource);
                        if (prefab == null || prefab.GetComponent<TraderScript>() == null) throw new InvalidOperationException("Trader resource unavailable.");
                        var go = Object.Instantiate(prefab, Position(placement), Quaternion.identity);
                        trader = go.GetComponent<TraderScript>();
                        if (Resource(trader) != placement.resource) { Object.Destroy(go); throw new InvalidOperationException("Trader character/resource mismatch."); }
                        spawned++;
                        Watch(trader, token);
                    }
                    pairs.Add((placement, trader));
                } catch (Exception ex) { log.error($"LOAD: trader placement failed res={placement.resource}: {ex}"); }
            }
            // New traders must run native Start once, including inventory/tracker
            // setup, before applying desiredPos/MoveRange (Start resets desiredPos).
            yield return null;
            if (!Current(world, session, token)) yield break;
            int positioned = 0;
            foreach (var pair in pairs) {
                try {
                    var trader = pair.trader;
                    if (trader == null) throw new InvalidOperationException("Trader disappeared before placement.");
                    var old = trader.transform.position;
                    trader.transform.position = Position(pair.placement);
                    ApplyMovement(trader, pair.placement);
                    Watch(trader, token).pendingPlacement = pair.placement;
                    positioned++;
                    positionedTraders.Add(trader);
                    // Sleeping traders retain their state without forced activation
                    // or duplicate spawning. Existing chunk scanning wakes them at
                    // their NEW position; normal sync then publishes their state.
                    if (trader.gameObject.activeInHierarchy && trader.enabled) {
                        try {
                            var sync = NetObjectRegistry.Server_EnsureItemIsNetworkRegistered(trader.gameObject);
                            if (sync == null) throw new InvalidOperationException("Trader SyncInfo registration failed.");
                            sync.WriteObjectIntoPacket();
                            TraderCommit.Publish(trader);
                        } catch (Exception ex) { log.error($"LOAD: trader positioned but immediate sync failed res={Resource(trader)}: {ex}"); }
                    }
                    log.l($"LOAD: trader positioned res={Resource(trader)} from=({old.x:F3},{old.y:F3}) to=({trader.transform.position.x:F3},{trader.transform.position.y:F3}) active={trader.gameObject.activeInHierarchy} enabled={trader.enabled} inventory=generated-or-retained");
                } catch (Exception ex) { log.error($"LOAD: trader positioning failed res={pair.placement.resource}: {ex}"); }
            }
            log.l($"LOAD: traders positioned={positioned} spawned={spawned} keptExtra={pool.Count} saved={(placements == null ? -1 : placements.Count)}");
            PlacementCensus("0", placements, positionedTraders);
            Census("0");
            yield return new WaitForSecondsRealtime(3f);
            if (Current(world, session, token)) { PlacementCensus("3", placements, positionedTraders); Census("3"); }
            else log.warn("LOAD: census t=3 cancelled; world/session changed");
        } finally { if (token == generation) watching = false; }
    }

    internal static void Census(string time) {
        var objects = Object.FindObjectsOfType<BuildingEntity>(true).Select(x => x.gameObject)
            .Concat(Object.FindObjectsOfType<Item>(true).Select(x => x.gameObject)).Concat(Traders().Select(x => x.gameObject))
            .Where(go => go != null && go.scene.IsValid() && go.scene.isLoaded).Distinct().ToArray();
        var counts = objects.GroupBy(go => go.TryGetComponent<TraderScript>(out var trader) ? Resource(trader) : (SyncInfo.GetItemID(go) ?? "<none>"))
            .ToDictionary(x => x.Key, x => x.Count());
        int Count(string resource) => counts.TryGetValue(resource, out int count) ? count : 0;
        log.l($"LOAD: census t={time} trader1={Count("trader1")} trader2={Count("trader2")} trader3={Count("trader3")} shadecrawler={Count("shadecrawler")} total={objects.Length}");
        log.l($"LOAD: census resources t={time} " + string.Join(";", counts.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value}")));
        foreach (var trader in Traders()) {
            var position = trader.transform.position;
            var building = trader.GetComponent<BuildingEntity>();
            log.l($"LOAD: census trader t={time} res={Resource(trader)} instance={trader.GetInstanceID()} pos=({position.x:F3},{position.y:F3}) active={trader.gameObject.activeInHierarchy} enabled={trader.enabled} hp={(building == null ? -1 : building.GetHealth()):F1}");
        }
    }

    internal static void BulkClear(string method) {
        if (watching && Util.GameIsFull() && Net.IsServer) log.warn($"LOAD: trader bulk clear during observation source={method} layer={observedLayer} currentWorld={ReferenceEquals(observedWorld, WorldGeneration.world)}");
    }
    internal static void Destroyed(TraderLoadWatch watch) {
        if (watching && watch.token == generation)
            log.warn($"LOAD: trader destroyed res={watch.resource} instance={watch.instance} lastPos=({watch.position.x:F3},{watch.position.y:F3}) sameWorld={ReferenceEquals(observedWorld, WorldGeneration.world)} via={Environment.StackTrace}");
    }

    static TraderLoadWatch Watch(TraderScript trader, int token) {
        var watch = trader.GetComponent<TraderLoadWatch>() ?? trader.gameObject.AddComponent<TraderLoadWatch>();
        if (watch.token != token) watch.pendingPlacement = null;
        watch.token = token; watch.resource = Resource(trader); watch.instance = trader.GetInstanceID(); watch.position = trader.transform.position;
        return watch;
    }
    // A slept-before-Start instance may initialize only when approached much
    // later. Restore movement after native Start, without resetting its inventory.
    internal static void AfterNativeStart(TraderScript trader) {
        if (!Util.GameIsFull() || !Net.IsServer) return;
        var watch = trader.GetComponent<TraderLoadWatch>();
        if (watch == null || watch.pendingPlacement == null) return;
        var placement = watch.pendingPlacement;
        watch.pendingPlacement = null;
        if (!Current(observedWorld, observedSession, watch.token)) return;
        ApplyMovement(trader, placement);
        log.l($"LOAD: trader movement restored after deferred Start res={Resource(trader)}");
    }
    static void ApplyMovement(TraderScript trader, WorldTraderPlacement placement) {
        trader.desiredPos = new Vector2(placement.desired[0], placement.desired[1]);
        trader.MoveRange = new RangeF(placement.moveMin, placement.moveMax);
        trader.farEnoughToMove = placement.farEnoughToMove;
        trader.didMove = placement.didMove;
    }
    static TraderScript[] Traders() => Object.FindObjectsOfType<TraderScript>(true).Where(t => t != null && t.gameObject.scene.IsValid() && t.gameObject.scene.isLoaded).ToArray();
    static bool Usable(TraderScript trader) => Eligible(trader, out _);
    static bool Eligible(TraderScript trader, out string reason) {
        reason = null;
        if (trader == null || trader.character < 0 || trader.character > 2) { reason = "invalid-character"; return false; }
        var building = trader.GetComponent<BuildingEntity>();
        if (building == null) { reason = "missing-building"; return false; }
        float health = building.GetHealth();
        if (!Finite(health)) { reason = "invalid-health"; return false; }
        if (health <= 200f) { reason = "dead"; return false; }
        var p = trader.transform.position;
        if (!Finite(p.x) || !Finite(p.y) || !Finite(p.z) || !Finite(trader.desiredPos.x) || !Finite(trader.desiredPos.y) ||
            !Finite(trader.MoveRange.min) || !Finite(trader.MoveRange.max) || trader.MoveRange.min > trader.MoveRange.max) {
            reason = "invalid-placement"; return false;
        }
        return true;
    }
    static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    static void PlacementCensus(string time, List<WorldTraderPlacement> saved, HashSet<TraderScript> positioned) {
        var present = Traders().Where(Usable).ToArray();
        var live = positioned.Where(t => t != null && present.Contains(t)).ToArray();
        int missing = saved == null ? -1 : Math.Max(0, saved.Count - live.Length);
        string line = $"LOAD: trader census saved={(saved == null ? -1 : saved.Count)} present={present.Length} positioned={live.Length} missing={missing} t={time}";
        if (saved != null && (present.Length < saved.Count || missing > 0)) log.warn(line);
        else log.l(line);
    }
    static string Resource(TraderScript trader) => trader.character >= 0 && trader.character <= 2 ? "trader" + (trader.character + 1) : "trader-invalid";
    static Vector3 Position(WorldTraderPlacement placement) => new(placement.position[0], placement.position[1], placement.position[2]);
    static float DistanceSquared(Vector3 position, float[] target) => (position.x - target[0]) * (position.x - target[0]) + (position.y - target[1]) * (position.y - target[1]);
    static bool Current(WorldGeneration world, object session, int token) => token == generation && Net.IsRunning && Net.IsServer &&
        world != null && ReferenceEquals(world, WorldGeneration.world) && ReferenceEquals(session, Net.ServerEntityManager) && world.biomeDepth == observedLayer;
}

internal sealed class TraderLoadWatch : MonoBehaviour {
    internal int token, instance;
    internal string resource;
    internal Vector3 position;
    internal WorldTraderPlacement pendingPlacement;
    void LateUpdate() { position = transform.position; }
    void OnDestroy() { TraderLoadRecovery.Destroyed(this); }
}

[HarmonyPatch]
internal static class TraderLoadBulkClearTrace {
    static IEnumerable<MethodBase> TargetMethods() {
        yield return AccessTools.Method(typeof(NetObjectRegistry), nameof(NetObjectRegistry.UnregisterAllObjectsButKeepInventory));
        yield return AccessTools.Method(typeof(NetObjectRegistry), nameof(NetObjectRegistry.UnregisterAllObjects));
        yield return AccessTools.Method(typeof(NetObjectRegistry), nameof(NetObjectRegistry.WipeSessionEntities));
    }
    static void Prefix(MethodBase __originalMethod) { TraderLoadRecovery.BulkClear(__originalMethod.Name); }
}

[HarmonyPatch(typeof(TraderScript), "Start")]
internal static class TraderLoadRestoreMovementAfterStart {
    [HarmonyPriority(Priority.Last)]
    static void Postfix(TraderScript __instance) { TraderLoadRecovery.AfterNativeStart(__instance); }
}
#endif
