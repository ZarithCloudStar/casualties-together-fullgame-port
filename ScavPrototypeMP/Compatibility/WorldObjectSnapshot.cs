#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using KrokV5Optimization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Tilemaps;
using Object = UnityEngine.Object;

namespace Together;

// Also marks the two Start methods whose generation-only side effects must not replay.
internal sealed class WorldSnapshotRestored : MonoBehaviour { internal int networkKey; }

internal static class WorldObjectSnapshot {
    internal static bool Instantiating;
    static readonly Dictionary<string, string[]> ExtraFields = new() {
        [nameof(BuildingEntity)] = new[] { "cantHit", "dropChanceMultiplier", "guaranteedDropAmount" },
        [nameof(CustomItemBehaviour)] = new[] { "state", "data" },
        [nameof(GunScript)] = new[] { "safe", "racked" },
        [nameof(SpikeStabberScript)] = new[] { "activated" },
        [nameof(BearTrap)] = new[] { "activated" },
        [nameof(JumpPadScript)] = new[] { "cooldown" },
        [nameof(CoilScript)] = new[] { "cooldown" },
        [nameof(GunmineScript)] = new[] { "cooldown" },
        [nameof(GeyserScript)] = new[] { "liquidType" },
        [nameof(CorpseScript)] = new[] { "didComment" },
        [nameof(DrillPod)] = new[] { "working" },
        [nameof(StalactiteDropper)] = new[] { "dropped", "counting", "countTime", "lastVel" },
        [nameof(FanTrap)] = new[] { "fanProgress", "spinSpeed", "desiredSpinSpeed", "speedMult", "dislodged" },
        [nameof(PistonScript)] = new[] { "up", "delay", "desiredDelay" },
        [nameof(SurvivorNote)] = new[] { "loreString" },
        [nameof(LifepodController)] = new[] { "heatState" },
        [nameof(Heater)] = new[] { "desiredTemp", "tempLerpStrength" },
        [nameof(TurretScript)] = new[] { "didBeep", "beepTime", "didShoot", "<timeSinceFired>k__BackingField" }
    };
    static readonly HashSet<string> UnsafeControllers = new() {
        "SpiderHandler", "GrabberPlant", "TraderScript", "ElderThornbackBehaviour", "CrystalEnemy", "CrystalMimic",
        "GuardScript", "LaserDroneScript"
    };
    sealed class Batch {
        internal WorldGeneration world;
        internal object session;
        internal List<(WorldObjectRecord data, GameObject go)> spawned = new();
        internal int skipped, removed, criticalFailures;
        internal string firstCriticalFailure;
        internal Dictionary<string, int> keptDynamic = new();
        internal BackgroundSnapshot.Batch backgrounds;
        internal List<WorldTraderPlacement> traders;
    }
    static Batch pending;

    // Terrain/backgrounds have their own strict barrier. Never lose a container
    // root or an interactive building while tolerating a missing prop/drop.
    static bool Critical(WorldObjectRecord record) => record.container || (record.parent == 0 && record.building && record.usable == true);
    static bool IsCrystalResource(string resource) =>
        !string.IsNullOrEmpty(resource) && resource.StartsWith("crystal", StringComparison.OrdinalIgnoreCase);

    static void SkipObject(Batch batch, WorldObjectRecord record, string phase, string reason, Exception error = null, GameObject failed = null) {
        batch.skipped++;
        bool critical = Critical(record);
        string detail = $"side={(Net.IsClient ? "client" : "server")} phase={phase} id={record.id} res={record.resource} parent={record.parent} critical={critical} reason={reason}";
        if (critical) {
            batch.criticalFailures++;
            if (batch.firstCriticalFailure == null) batch.firstCriticalFailure = detail;
        }
        log.warn("LOAD: world object skipped " + detail + (error == null ? "" : " exception=" + error));
        SessionLog.Event("WORLD OBJECT SKIPPED", detail);
        if (failed != null) {
            try { Remove(failed); }
            catch (Exception ex) { log.warn($"LOAD: failed object cleanup id={record.id} res={record.resource}: {ex}"); }
        }
    }

    internal static byte[] Serialize() {
        RequireServer();
        var world = WorldGeneration.world;
        var document = new WorldObjectDocument { biome = world.biomeDepth, width = world.width, height = world.height, saveId = WoundView.view.cInfo[2], structureCatalog = 1 };
        document.endingFlow = EndingFlow.Capture();
        var ids = new Dictionary<GameObject, int>();
        var skips = new Dictionary<string, int>();
        foreach (var go in Candidates().Where(Managed).OrderBy(x => Depth(x.transform))) {
            string resource = SyncInfo.GetItemID(go);
            if (!SafeResource(go, resource, out var reason)) { Skip(skips, resource, reason); continue; }
            int parent = 0;
            if (go.transform.parent != null) {
                var container = go.transform.parent.GetComponent<Container>();
                bool embedded = false;
                for (var t = go.transform.parent; t != null; t = t.parent)
                    if (t.GetComponent<Item>() != null || t.GetComponent<BuildingEntity>() != null) { embedded = true; break; }
                if ((container != null && !ids.TryGetValue(container.gameObject, out parent)) || (container == null && embedded)) {
                    Skip(skips, resource, "unsupported or excluded parent"); continue;
                }
            }
            // Read errors for an otherwise supported object fail the save batch.
            var record = Capture(go, resource, document.objects.Count + 1, parent);
            document.objects.Add(record);
            ids.Add(go, record.id);
        }
        foreach (var skip in skips) log.warn($"SAVE-EXIT: world objects skipped {skip.Key} count={skip.Value}");
        BackgroundSnapshot.Capture(document);
        TraderLoadRecovery.Capture(document);
        var bytes = WorldObjectSnapshotData.Encode(document);
        log.l($"SAVE-EXIT: world objects captured count={document.objects.Count} skipped={skips.Values.Sum()} logicalActive={document.objects.Count(o => o.active)} layer={document.biome}");
        return bytes;
    }

    internal static int Validate(byte[] bytes) {
        RequireServer();
        var world = WorldGeneration.world;
        return WorldObjectSnapshotData.Decode(bytes, world.biomeDepth, world.width, world.height, WoundView.view.cInfo[2]).objects.Count;
    }

    internal static void PreflightRestore(byte[] bytes) {
        RequireWorld();
        var world = WorldGeneration.world;
        var document = WorldObjectSnapshotData.Decode(bytes, world.biomeDepth, world.width, world.height, WoundView.view.cInfo[2]);
        BackgroundSnapshot.ValidateForRestore(document);
        int foldedHeaters = 0;
        foreach (var record in document.objects) {
            try {
                var prefab = ResolveSavedPrefab(record);
                if (prefab == null || !SafeResource(prefab, record.resource, out _) ||
                    (prefab.GetComponent<BuildingEntity>() != null) != record.building ||
                    (prefab.GetComponent<Container>() != null) != record.container)
                    throw new InvalidDataException("World object template unavailable/changed: " + record.resource);
                if (record.noteFont.HasValue && (prefab.GetComponent<SurvivorNote>() == null || record.noteFont.Value < -1 || record.noteFont.Value >= prefab.GetComponent<SurvivorNote>().fonts.Length))
                    throw new InvalidDataException("Invalid note font reference.");
                if (record.noteSprite != null && SnapshotPrefabCatalog.Sprite(record.noteSprite) == null) throw new InvalidDataException("Missing note sprite reference.");
                foreach (var state in record.components) {
                    // Compatibility for saves written before the crystal-Heater
                    // fold: the runtime component was persisted, but the prefab
                    // never had it. CrystalSync will recreate it from the saved
                    // crystal snapshot, so this is not a missing prefab shape.
                    if (record.hasCrystal == true && state.type == typeof(Heater).FullName) {
                        foldedHeaters++;
                        continue;
                    }
                    var node = prefab.transform;
                    if (state.path.Length != 0) foreach (var part in state.path.Split('/')) {
                        var children = PrefabChildren(node); int index = int.Parse(part);
                        if (index >= children.Length) throw new InvalidDataException("Missing component child: " + record.resource);
                        node = children[index];
                    }
                    var component = node.GetComponents<MonoBehaviour>().FirstOrDefault(x => x != null && x.GetType().FullName == state.type);
                    if (component == null) throw new InvalidDataException("Missing prefab component: " + state.type);
                    var allowed = Fields(component.GetType()).ToDictionary(f => f.Name);
                    foreach (var field in state.fields) {
                        if (!allowed.TryGetValue(field.Key, out var info)) throw new InvalidDataException("Unsupported component field: " + field.Key);
                        if (component is CustomItemBehaviour && field.Key == "data" && field.Value.Type != JTokenType.Null) field.Value.ToObject<float[]>();
                        else field.Value.ToObject(info.FieldType);
                    }
                }
            } catch (Exception ex) {
                if (Critical(record)) throw new InvalidDataException($"Critical world object preflight failed id={record.id} res={record.resource}: {ex.Message}", ex);
                log.warn($"LOAD: optional world object preflight failed id={record.id} res={record.resource}: {ex}");
            }
        }
        if (foldedHeaters > 0) log.l($"LOAD: folded {foldedHeaters} legacy crystal Heater component(s) into crystal state");
        log.l($"LOAD: world preflight format={document.version} objects={document.objects.Count} backgrounds={document.backgrounds.Count} bytes={bytes.Length}");
    }

    internal static void Deserialize(byte[] bytes) {
        RequireWorld();
        PreflightRestore(bytes); // no destructive cleanup before structural/critical checks pass
        var world = WorldGeneration.world;
        // Decode and validate the WHOLE file before removing any generated object.
        var document = WorldObjectSnapshotData.Decode(bytes, world.biomeDepth, world.width, world.height, WoundView.view.cInfo[2]);
        int legacyWoken = 0;
        var prefabs = new Dictionary<int, GameObject>();
        var batch = new Batch { world = world, session = WorldSnapshotTransfer.Session };
        batch.traders = document.traders;
        if (Net.IsServer) TraderLoadRecovery.Begin(world, Net.ServerEntityManager);
        foreach (var record in document.objects) {
            var prefab = ResolveSavedPrefab(record);
            string reason = "missing resource";
            if (prefab == null || !SafeResource(prefab, record.resource, out reason) ||
                (prefab.GetComponent<BuildingEntity>() != null) != record.building ||
                (prefab.GetComponent<Container>() != null) != record.container ||
                (record.parent != 0 && !prefabs.ContainsKey(record.parent))) {
                SkipObject(batch, record, "prefab", reason ?? "resource shape/parent mismatch");
                continue;
            }
            // v1/v2 persisted ChunkSleep's inactive bit without its wake registry.
            // These formats cannot distinguish sleep from intentionally disabled
            // roots. Prefer a usable prefab and report the migration explicitly.
            if (document.version < 3 && !record.active && prefab.activeSelf) { record.active = true; legacyWoken++; }
            prefabs.Add(record.id, prefab);
        }
        if (document.version < 3) {
            log.warn($"LOAD: legacy world snapshot v{document.version}; recovering {legacyWoken} inactive objects as awake. Old files did not distinguish optimisation sleep from gameplay inactivity.");
            Multiplayer.DoMultiplayerStatusMessageLog("旧版当前层存档：正在恢复休眠建筑；旧格式无法区分休眠和手动关闭状态。 / Recovering sleeping objects from a legacy checkpoint.");
        }
        foreach (var go in Candidates().Where(Managed).OrderByDescending(x => Depth(x.transform)).ToArray()) {
            // Match the writer's supported types. Also protect children of an
            // excluded controller/container, so clearing stock cannot gut an NPC.
            if (KeepGenerated(go, out bool dynamic)) {
                if (dynamic) CountKept(batch.keptDynamic, go);
                continue;
            }
            Remove(go);
            batch.removed++;
        }
        var created = new Dictionary<int, GameObject>();
        foreach (var record in document.objects) {
            if (!prefabs.TryGetValue(record.id, out var prefab)) continue;
            if (record.parent != 0 && !created.ContainsKey(record.parent)) {
                SkipObject(batch, record, "spawn", "Parent container was not created."); continue;
            }
            GameObject go = null;
            try {
                Instantiating = true;
                try { go = Object.Instantiate(prefab, Position(record), Rotation(record)); }
                finally { Instantiating = false; }
                int key = Net.IsServer ? record.id : record.networkKey;
                if (key == 0) throw new InvalidDataException("Checkpoint lacks network object identity.");
                WorldSnapshotTransfer.Track(go, key);
                StripBakedChildren(go);
                if (go.TryGetComponent<Rigidbody2D>(out var rb)) rb.simulated = false;
                ApplyState(go, record, false); // before Start: plush/liquids must see saved values
                created.Add(record.id, go);
                batch.spawned.Add((record, go));
            } catch (Exception ex) {
                SkipObject(batch, record, "spawn", ex.Message, ex, go);
            }
        }
        batch.backgrounds = BackgroundSnapshot.Stage(document);
        foreach (var kept in batch.backgrounds.keptDynamic) {
            batch.keptDynamic.TryGetValue(kept.Key, out int count);
            batch.keptDynamic[kept.Key] = count + kept.Value;
        }
        foreach (var kept in batch.keptDynamic.OrderBy(x => x.Key)) log.l($"LOAD: world objects keptDynamic res={kept.Key} count={kept.Value}");
        log.l($"LOAD: world objects cleared={batch.removed} keptDynamic={batch.keptDynamic.Values.Sum()}");
        pending = batch;
        log.l($"LOAD: world objects staged count={batch.spawned.Count} removed={batch.removed} layer={document.biome}");
    }

    internal static IEnumerator FinishRestore() {
        var batch = pending;
        pending = null;
        if (batch == null) yield break;
        // Start can reset wetTime, liquid condition, rigid type and random state.
        // No physics for restored roots until this second application completes.
        yield return null;
        if (!Net.IsRunning || batch.world == null || !ReferenceEquals(batch.world, WorldGeneration.world) ||
            !ReferenceEquals(batch.session, WorldSnapshotTransfer.Session)) {
            log.warn("LOAD: world object restore cancelled; world/session changed");
            yield break;
        }
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var ready = new Dictionary<int, GameObject>();
        long applyMs = 0;
        int applied = 0;
        foreach (var entry in batch.spawned) {
            var go = entry.go;
            try {
                if (go == null) throw new InvalidDataException("Object disappeared during Start.");
                ApplyState(go, entry.data, true);
                if (entry.data.parent != 0) {
                    if (!ready.TryGetValue(entry.data.parent, out var parent)) throw new InvalidDataException("Snapshot container unavailable.");
                    var container = parent.GetComponent<Container>();
                    var item = go.GetComponent<Item>();
                    // Use the saved topology, not LoadItem's distance/weight gates.
                    go.transform.SetParent(parent.transform);
                    go.transform.localPosition = Vector3.zero;
                    item.rb.simulated = false;
                    if (go.TryGetComponent<SpriteRenderer>(out var sprite)) sprite.enabled = container.itemsVisible;
                    Container.UpdateItemLight(go, !container.itemsVisible);
                }
                // The initial Start frame may have enrolled this root. A real
                // gameplay-disabled v3 object must not be woken by that entry.
                if (!entry.data.active) ChunkSleep.Drop(go);
                go.SetActive(entry.data.active);
                ready.Add(entry.data.id, go);
            } catch (Exception ex) {
                SkipObject(batch, entry.data, "finish", ex.Message, ex, go);
            }
            // This pass can touch thousands of objects and Start callbacks. Let
            // the loading screen breathe instead of holding one frame for 20s.
            if ((++applied & 63) == 0) yield return null;
        }
        applyMs = stopwatch.ElapsedMilliseconds;
        int registered = 0, registrationFailures = 0;
        foreach (var entry in batch.spawned) {
            if (!ready.TryGetValue(entry.data.id, out var go)) continue;
            try {
                // Existing spatial registry brings distant meshes in when approached.
                if (!Net.IsServer || !go.activeInHierarchy || NetObjectRegistry.ObjectShouldNotBeSynced(go) || !NetObjectRegistry.ShouldKeepRegistered(go)) continue;
                var sync = NetObjectRegistry.Server_EnsureItemIsNetworkRegistered(go);
                if (sync == null) throw new InvalidOperationException("SyncInfo registration failed.");
                sync.WriteObjectIntoPacket();
                registered++;
            } catch (Exception ex) { registrationFailures++; SkipObject(batch, entry.data, "register", ex.Message, ex); }
            if ((++applied & 63) == 0) yield return null;
        }
        BackgroundSnapshot.Finish(batch.backgrounds);
        long totalMs = stopwatch.ElapsedMilliseconds;
        log.l($"LOAD: world objects restored={ready.Count} skipped={batch.skipped} criticalFailures={batch.criticalFailures} removed={batch.removed} registered={registered} registrationFailures={registrationFailures} layer={batch.world.biomeDepth} keptDynamic={batch.keptDynamic.Values.Sum()} applyMs={applyMs} registerMs={totalMs - applyMs} totalMs={totalMs}");
        if (Net.IsServer) TraderLoadRecovery.Queue(batch.world, batch.session, batch.traders);
        if (batch.criticalFailures != 0 || ready.Count == 0)
            throw new InvalidDataException($"Critical world object restore failed: restored={ready.Count} skipped={batch.skipped} criticalFailures={batch.criticalFailures}; {batch.firstCriticalFailure ?? "No world objects restored."}");
        if (batch.skipped != 0) log.warn($"LOAD: continuing after {batch.skipped} noncritical world object failures; terrain/background/container barriers passed.");
    }

    static GameObject ResolveSavedPrefab(WorldObjectRecord record) {
        // Older saves stored the shared building ID. Ceiling generation starts
        // at 180 degrees and adds at most 15 degrees of variation.
        if (record.building && record.resource == ScavMPBuildingSynchronizer.SpiceVineGround &&
            (Rotation(record) * Vector3.up).y < 0f)
            record.resource = ScavMPBuildingSynchronizer.SpiceVineCeiling;
        return SnapshotPrefabCatalog.Resolve(record.resource);
    }

    static IEnumerable<GameObject> Candidates() => Object.FindObjectsOfType<Item>(true).Select(x => x.gameObject)
        .Concat(Object.FindObjectsOfType<BuildingEntity>(true).Select(x => x.gameObject)).Distinct();

    static bool Managed(GameObject go) {
        if (EndingLabObjects.SceneCritical(go)) return false;
        if (go == null || !go.scene.IsValid() || !go.scene.isLoaded || go.GetComponentInChildren<Body>(true) != null ||
            (NetObjectRegistry.ObjectShouldNotBeSynced(go) && !StaticIgnored(go))) return false;
        for (var t = go.transform; t != null; t = t.parent) {
            if (t.GetComponent<Body>() != null || t.GetComponent<NetBody>() != null || t.name.StartsWith("SAVESTATE_")) return false;
            if (t.GetComponent<Tilemap>() != null) return false;
            var building = t.GetComponent<BuildingEntity>();
            if (building != null && (building.id == "background" || (building.id ?? "").StartsWith(BuildingEntity_Start_MultiplayerPatch.identified_bg_prefix))) return false;
        }
        return true;
    }

    static bool SafeResource(GameObject go, string resource, out string reason) {
        reason = null;
        if (string.IsNullOrWhiteSpace(resource) || SnapshotPrefabCatalog.Resolve(resource) == null) reason = "no Resources prefab";
        else if (go.GetComponentInChildren<Body>(true) != null) reason = "Body belongs to body lifecycle";
        else if (go.GetComponentsInChildren<MonoBehaviour>(true).Any(x => x != null && UnsafeControllers.Contains(x.GetType().Name))) reason = "dynamic controller not safely restorable";
        else if (go.TryGetComponent<BuildingEntity>(out var b) && b.animal) reason = "animal controller not snapshotted";
        else if (go.GetComponent<Item>() == null && go.GetComponent<BuildingEntity>() == null) reason = "not Item/BuildingEntity";
        else if (go.GetComponent<Tilemap>() != null || (NetObjectRegistry.ObjectShouldNotBeSynced(go) && !StaticIgnored(go))) reason = "structural/nonunique resource";
        else if (go.GetComponentsInChildren<CustomItemBehaviour>(true).Any(x => x.data != null && x.data.Any(y => !(y is float)))) reason = "unsupported custom item data";
        return reason == null;
    }
    static bool StaticIgnored(GameObject go) => go.GetComponent<Climbable>() != null ||
        (go.GetComponent<BuildingEntity>() is var b && b != null &&
         !b.id.StartsWith("lifepod", StringComparison.OrdinalIgnoreCase) && b.id != "KSMUTLTI_IGN" &&
         go.GetComponent<KrokoshaScavMultiGameObjectNetworkTracker_FORCEIGNORE>() == null);
#if TESTING
    internal static string CoverageReason(GameObject go) {
        SafeResource(go, SyncInfo.GetItemID(go), out var reason);
        return reason ?? "ordinary snapshot";
    }
#endif

    static bool KeepGenerated(GameObject go, out bool dynamic) {
        dynamic = go.GetComponentsInChildren<MonoBehaviour>(true).Any(x => x != null && UnsafeControllers.Contains(x.GetType().Name));
        bool unsupported = false;
        for (var t = go.transform; t != null; t = t.parent) {
            if (IsDynamicNode(t)) dynamic = true;
            if (t == go.transform || t.GetComponent<Item>() != null || t.GetComponent<BuildingEntity>() != null)
                if (!SafeResource(t.gameObject, SyncInfo.GetItemID(t.gameObject), out _)) unsupported = true;
        }
        return dynamic || unsupported;
    }

    internal static bool IsDynamicNode(Transform node) => node.GetComponent<Body>() != null || node.GetComponent<NetBody>() != null ||
        (node.GetComponent<BuildingEntity>() is var building && building != null && building.animal) ||
        node.GetComponents<MonoBehaviour>().Any(x => x != null && UnsafeControllers.Contains(x.GetType().Name));

    static GameObject[] DynamicChildren(GameObject root) {
        var found = new HashSet<GameObject>();
        foreach (var node in root.GetComponentsInChildren<Transform>(true).Where(t => t != root.transform && IsDynamicNode(t))) {
            var entity = node;
            while (entity.parent != null && entity.parent != root.transform && entity.GetComponent<Item>() == null &&
                entity.GetComponent<BuildingEntity>() == null && entity.GetComponent<Body>() == null) entity = entity.parent;
            found.Add(entity.gameObject);
        }
        return found.Where(go => {
            for (var t = go.transform.parent; t != null && t != root.transform; t = t.parent) if (found.Contains(t.gameObject)) return false;
            return true;
        }).ToArray();
    }

    internal static GameObject[] DetachGeneratedDynamics(GameObject root) {
        var kept = DynamicChildren(root);
        foreach (var go in kept) go.transform.SetParent(null, true); // preserve generated pose/controller instance
        return kept;
    }
    internal static void StripDynamicChildren(GameObject root) {
        foreach (var go in DynamicChildren(root)) Object.DestroyImmediate(go); // prefab copies only, before Start
    }
    internal static void CountKept(Dictionary<string, int> counts, GameObject go) {
        string resource = SyncInfo.GetItemID(go) ?? go.name;
        counts.TryGetValue(resource, out int count); counts[resource] = count + 1;
    }

    static WorldObjectRecord Capture(GameObject go, string resource, int id, int parent) {
        var transform = go.transform;
        var p = transform.position; var r = transform.rotation; var s = transform.localScale;
        var item = go.GetComponent<Item>(); var building = go.GetComponent<BuildingEntity>();
        var record = new WorldObjectRecord {
            networkKey = WorldSnapshotTransfer.SnapshotMode ? WorldSnapshotTransfer.Key(go) : 0,
            id = id, parent = parent, resource = resource, building = building != null, container = go.GetComponent<Container>() != null,
            active = ChunkSleep.SnapshotActive(go), position = new[] { p.x, p.y, p.z }, rotation = new[] { r.x, r.y, r.z, r.w }, scale = new[] { s.x, s.y, s.z },
            condition = item != null ? item.condition : building.GetHealth(), velocity = new float[2]
        };
        record.usable = go.GetComponent<UsableObject>() != null;
        if (building != null) record.seating = new[] { building.blockPlacedOn.x, building.blockPlacedOn.y };
        if (go.TryGetComponent<SurvivorNote>(out var note)) {
            record.noteFont = Array.IndexOf(note.fonts, note.fontAsset);
            if (note.loreSprite != null) record.noteSprite = SnapshotPrefabCatalog.SpriteKey(note.loreSprite);
        }
        if (go.TryGetComponent<Climbable>(out var climb)) {
            record.climbPoints = climb.points?.Select(p => new[] { p.x, p.y }).ToArray();
            record.climbVelocity = climb.downwardsVelocity;
        }
        if (IsCrystalResource(resource) || SnapshotPrefabCatalog.Resolve(resource)?.GetComponent<CrystalBehaviour>() != null) {
            record.hasCrystal = go.TryGetComponent<CrystalBehaviour>(out var crystal);
            if (crystal != null && crystal.effects != null) record.crystal = CrystalSync.Get(crystal).Capture();
            // GenerationFinished destroys CrystalBehaviour on ordinary crystals
            // after SetColor, so their color/light is not in record.crystal.
            if (go.TryGetComponent<SpriteRenderer>(out var crystalSprite)) {
                var color = crystalSprite.color;
                record.crystalColor = new[] { color.r, color.g, color.b, color.a };
            }
            if (go.TryGetComponent<UnityEngine.Rendering.Universal.Light2D>(out var crystalLight2D)) {
                record.crystalLight = crystalLight2D.intensity;
            }
        }
        if (item != null) { record.favourited = item.favourited; record.label = item.label; record.swatch = item.colorSwatch; record.wetAge = Time.time - item.wetTime; }
        if (go.TryGetComponent<Rigidbody2D>(out var rb)) {
            record.hasRigid = true; record.simulated = rb.simulated; record.rigidType = (int)rb.bodyType;
            record.velocity = new[] { rb.velocity.x, rb.velocity.y }; record.angularVelocity = rb.angularVelocity;
        }
        if (go.TryGetComponent<SpriteRenderer>(out var sprite)) {
            record.spriteSize = new[] { sprite.size.x, sprite.size.y };
            record.flipX = sprite.flipX; record.flipY = sprite.flipY; record.sortingOrder = sprite.sortingOrder;
            if (go.TryGetComponent<CorpseScript>(out var corpse)) record.corpseSprite = Array.IndexOf(corpse.startSprites, sprite.sprite);
        }
        if (go.TryGetComponent<TrackerBuildingEntityBackgroundified>(out var background)) record.background = background.is_backgroundified;
        CaptureFields(go.transform, "", record.components);
        // CrystalTemperature adds Heater at runtime, so it is not part of the
        // crystalmedium prefab. CrystalSync already persists the same values
        // (heaterTemp/heaterStrength) and recreates the component on restore.
        // Saving it again here makes the loader demand a prefab component that
        // cannot exist, which aborts the whole checkpoint as a critical error.
        if (record.hasCrystal == true) {
            int folded = record.components.RemoveAll(c => c.type == typeof(Heater).FullName);
            if (folded > 0) log.l($"SAVE-EXIT: folded {folded} runtime crystal Heater component(s) into crystal state id={id} res={resource}");
        }
        return record;
    }

    static readonly Dictionary<Type, FieldInfo[]> FieldCache = new();
    static readonly Dictionary<Type, Dictionary<string, FieldInfo>> FieldMapCache = new();

    static FieldInfo[] Fields(Type type) {
        if (FieldCache.TryGetValue(type, out var cached))
            return cached;
        var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(f => !f.IsStatic && !f.IsInitOnly && ((Attribute.IsDefined(type, typeof(Saveable)) && Attribute.IsDefined(f, typeof(JsonPropertyAttribute))) ||
                (ExtraFields.TryGetValue(type.Name, out var names) && names.Contains(f.Name))))
            .ToArray();
        FieldCache[type] = fields;
        return fields;
    }

    static Dictionary<string, FieldInfo> FieldMap(Type type) {
        if (FieldMapCache.TryGetValue(type, out var cached))
            return cached;
        var map = Fields(type).ToDictionary(f => f.Name);
        FieldMapCache[type] = map;
        return map;
    }

    static bool Cacheable(object value) => value == null || value is string || value.GetType().IsValueType;

    internal static void CaptureFields(Transform transform, string path, List<WorldObjectComponent> components) {
        foreach (var component in transform.GetComponents<MonoBehaviour>()) {
            if (component == null) continue;
            var fields = new JObject();
            foreach (var field in Fields(component.GetType())) {
                var value = field.GetValue(component);
                if (component is CustomItemBehaviour && field.Name == "data" && value is object[] data && data.Any(x => x != null && !(x is float)))
                    throw new InvalidDataException("Unsupported CustomItemBehaviour data in world snapshot.");
                fields.Add(field.Name, value == null ? JValue.CreateNull() : JToken.FromObject(value));
            }
            if (fields.Count != 0) components.Add(new WorldObjectComponent { path = path, type = component.GetType().FullName, fields = fields });
        }
        var children = PrefabChildren(transform);
        for (int i = 0; i < children.Length; i++) {
            var child = children[i];
            // Inventory children are separate records; special visual children are part of the prefab.
            CaptureFields(child, path.Length == 0 ? i.ToString() : path + "/" + i, components);
        }
    }

    static void ApplyState(GameObject go, WorldObjectRecord record, bool final) {
        go.transform.SetPositionAndRotation(Position(record), Rotation(record));
        go.transform.localScale = new Vector3(record.scale[0], record.scale[1], record.scale[2]);
        ApplyFields(go, record.components, final);
        if (final && go.TryGetComponent<DrillPod>(out var drill) && drill.working) {
            if (go.TryGetComponent<UsableObject>(out var use)) Object.DestroyImmediate(use);
            go.GetComponent<BuildingEntity>().description = Locale.GetBuilding("drillpoddscfixed");
        }
        if (go.TryGetComponent<Item>(out var item)) {
            item.condition = record.condition; item.favourited = record.favourited; item.label = record.label; item.colorSwatch = record.swatch;
            item.wetTime = Time.time - record.wetAge;
        } else if (go.TryGetComponent<BuildingEntity>(out var building)) {
            AccessTools.Field(typeof(BuildingEntity), "_health").SetValue(building, record.condition);
            if (record.seating != null) {
                building.blockPlacedOn = new Vector2Int(record.seating[0], record.seating[1]);
                var world = WorldGeneration.world;
                if (final && building.requireGround && record.seating[0] >= 0 && record.seating[1] >= 0 && record.seating[0] < world.width && record.seating[1] < world.height)
                    world.ChunkUpdated[record.seating[0] / WorldGeneration.CHUNKSIZE, record.seating[1] / WorldGeneration.CHUNKSIZE].AddListener(building.CheckSeating);
            }
            if (record.background) {
                var tracker = go.GetComponent<TrackerBuildingEntityBackgroundified>() ?? go.AddComponent<TrackerBuildingEntityBackgroundified>();
                tracker.is_backgroundified = true;
                building.Backgroundify();
            }
        }
        ApplyPresentation(go, record, final);
    }
    internal static void ApplyFields(GameObject go, List<WorldObjectComponent> components, bool final) {
        if (components == null) return;
        foreach (var state in components) {
            if (state.type == typeof(Heater).FullName && go.GetComponent<CrystalBehaviour>() != null) {
                // Old saves: CrystalSync.Apply restores heaterTemp/heaterStrength
                // after CrystalTemperature.Start recreates this component.
                continue;
            }
            var transform = go.transform;
            if (state.path.Length != 0) foreach (var part in state.path.Split('/')) transform = PrefabChildren(transform)[int.Parse(part)];
            // Resolve only an existing prefab component; never Type.GetType/AddComponent from disk.
            var component = transform.GetComponents<MonoBehaviour>().FirstOrDefault(x => x != null && x.GetType().FullName == state.type);
            if (component == null) throw new InvalidDataException("Missing prefab component: " + state.type);
            var allowed = FieldMap(component.GetType());
            foreach (var field in state.fields) {
                if (!allowed.TryGetValue(field.Key, out var info)) throw new InvalidDataException("Unsupported snapshot field: " + field.Key);
                object value;
                if (final && state.decodedFields != null && state.decodedFields.TryGetValue(field.Key, out var cached)) {
                    value = cached;
                } else {
                    value = component is CustomItemBehaviour && field.Key == "data" && field.Value.Type != JTokenType.Null
                        ? field.Value.ToObject<float[]>().Cast<object>().ToArray() : field.Value.ToObject(info.FieldType);
                    // The same JSON value is applied twice (before and after Start).
                    // Cache only immutable values; arrays/classes may be mutated by
                    // Start and must be decoded again for the final pass.
                    if (!final && Cacheable(value)) {
                        state.decodedFields ??= new Dictionary<string, object>();
                        state.decodedFields[field.Key] = value;
                    }
                }
                info.SetValue(component, value);
            }
            if (component is WaterContainerItem water)
                LiquidSanitize.Clean(water, "WorldObjectSnapshot.ApplyFields");
            if (component is NonDescriptCan can)
                LiquidSanitize.Clean(can);
        }
    }
    internal static void ValidateFields(GameObject prefab, List<WorldObjectComponent> components) {
        if (components == null) return;
        if (components.Count > 256) throw new InvalidDataException("Too many background component states.");
        foreach (var state in components) {
            if (state == null || state.path == null || state.path.Length > 256 || state.type == null || state.fields == null)
                throw new InvalidDataException("Invalid background component state.");
            var node = prefab.transform;
            if (state.path.Length != 0) foreach (var step in state.path.Split('/')) {
                var children = PrefabChildren(node);
                if (!int.TryParse(step, out int index) || index < 0 || index >= children.Length) throw new InvalidDataException("Invalid background component path.");
                node = children[index];
            }
            var component = node.GetComponents<MonoBehaviour>().FirstOrDefault(c => c != null && c.GetType().FullName == state.type);
            if (component == null) throw new InvalidDataException("Missing background component: " + state.type);
            var fields = FieldMap(component.GetType());
            foreach (var field in state.fields) {
                if (!fields.TryGetValue(field.Key, out var info)) throw new InvalidDataException("Unsupported background field: " + field.Key);
                field.Value.ToObject(info.FieldType);
            }
        }
    }
    static void ApplyPresentation(GameObject go, WorldObjectRecord record, bool final) {
        if (record.usable == false && go.TryGetComponent<UsableObject>(out var use)) Object.DestroyImmediate(use);
        if (final && record.noteFont.HasValue && go.TryGetComponent<SurvivorNote>(out var note)) {
            note.fontAsset = record.noteFont.Value < 0 ? null : note.fonts[record.noteFont.Value];
            note.loreSprite = record.noteSprite == null ? null : SnapshotPrefabCatalog.Sprite(record.noteSprite);
        }
        if (go.TryGetComponent<SpriteRenderer>(out var sprite)) {
            if (record.spriteSize != null) sprite.size = new Vector2(record.spriteSize[0], record.spriteSize[1]);
            sprite.flipX = record.flipX; sprite.flipY = record.flipY; sprite.sortingOrder = record.sortingOrder;
            if (record.corpseSprite >= 0 && go.TryGetComponent<CorpseScript>(out var corpse) && record.corpseSprite < corpse.startSprites.Length) {
                sprite.sprite = corpse.startSprites[record.corpseSprite];
                if (go.TryGetComponent<CorpseScriptMultiplayerRewrite>(out var tracker)) tracker.corpseSpriteIndex = (byte)record.corpseSprite;
            }
        }
        if (go.TryGetComponent<Rigidbody2D>(out var rb)) {
            if (Net.IsClient) {
                // Static roots remain queryable without running saved dynamic physics.
                // Keep Start/the topology pass inert, and never expose inventory colliders.
                rb.simulated = false;
                rb.velocity = Vector2.zero; rb.angularVelocity = 0f;
                rb.bodyType = RigidbodyType2D.Static;
                rb.simulated = final && record.parent == 0;
            } else if (record.hasRigid) {
                rb.bodyType = (RigidbodyType2D)record.rigidType;
                rb.velocity = new Vector2(record.velocity[0], record.velocity[1]); rb.angularVelocity = record.angularVelocity;
                rb.simulated = final && record.simulated;
            }
        }
        if (final && record.climbPoints != null && go.TryGetComponent<Climbable>(out var climb)) {
            climb.points = record.climbPoints.Select(p => new Vector2(p[0], p[1])).ToList();
            climb.downwardsVelocity = record.climbVelocity; climb.CalculateTotalLength();
        }
        if (go.TryGetComponent<CrystalBehaviour>(out var crystal)) {
            if (record.hasCrystal == false) Object.DestroyImmediate(crystal);
            else if (final && record.crystal != null) CrystalSync.Get(crystal).Apply(record.crystal);
        }
        // CrystalSync restores effectful crystals; ordinary crystals have no
        // CrystalBehaviour left, so restore their saved renderer/light here.
        if (record.crystalColor == null && record.crystal == null &&
            (IsCrystalResource(record.resource) || SnapshotPrefabCatalog.Resolve(record.resource)?.GetComponent<CrystalBehaviour>() != null) &&
            go.TryGetComponent<SpriteRenderer>(out var legacyCrystalSprite)) {
            // Saves written before the appearance fields existed cannot recover
            // the original random magenta/cyan shade. Recreate a stable shade
            // from the saved position instead of leaving a pure-white crystal.
            int seed = Mathf.Abs(Mathf.RoundToInt(record.position[0] * 17f + record.position[1] * 31f + record.position[2] * 13f));
            Color baseColor = (seed & 1) == 0 ? Color.cyan : Color.magenta;
            float shade = 0.9f + ((seed >> 1) % 11) * 0.01f;
            var fallbackColor = new Color(baseColor.r * shade, baseColor.g * shade, baseColor.b * shade, 1f);
            legacyCrystalSprite.color = fallbackColor;
            if (go.TryGetComponent<UnityEngine.Rendering.Universal.Light2D>(out var legacyCrystalLight)) {
                legacyCrystalLight.color = fallbackColor;
                legacyCrystalLight.intensity = 0.6f;
            }
        }
        if (record.crystalColor != null && record.crystalColor.Length == 4 &&
            go.TryGetComponent<SpriteRenderer>(out var crystalSprite)) {
            var crystalColor = new Color(record.crystalColor[0], record.crystalColor[1], record.crystalColor[2], record.crystalColor[3]);
            crystalSprite.color = crystalColor;
            if (go.TryGetComponent<UnityEngine.Rendering.Universal.Light2D>(out var crystalLight2D)) {
                crystalLight2D.color = crystalColor;
                if (record.crystalLight.HasValue) crystalLight2D.intensity = record.crystalLight.Value;
            }
        }
        // Restore bypasses SetUpEffects, and ordinary crystals have already lost
        // CrystalBehaviour. Publish their restored appearance through the same
        // identity/version state protocol as generated crystals. Otherwise a
        // streamed replacement prefab stays white: HasState is false on the host
        // and neither its initial send nor the replica's recovery request works.
        if (final && record.hasCrystal.HasValue && go.TryGetComponent<SpriteRenderer>(out var restoredCrystalSprite))
            CrystalSync.Get(restoredCrystalSprite).MarkAppearance();
    }

    static void StripBakedChildren(GameObject root) {
        foreach (var item in root.GetComponentsInChildren<Item>(true).Where(x => x.gameObject != root).OrderByDescending(x => Depth(x.transform)).ToArray()) {
            // Those items will be recreated once, from their own saved record.
            Object.DestroyImmediate(item.gameObject);
        }
    }
    static Transform[] PrefabChildren(Transform transform) => transform.Cast<Transform>()
        .Where(x => x.GetComponent<Item>() == null && x.GetComponent<Body>() == null).ToArray();
    static void Remove(GameObject go) {
        if (go == null) return;
        go.SetActive(false); // no further Start/physics while Destroy is deferred
        if (NetObjectRegistry.TryGetSyncInfo(go, out var sync) && sync != null && !sync.IsDestroyed) sync.Destroy();
        else if (go.TryGetComponent<SyncInfoGameObjectTracker>(out var tracker) && tracker.syncInfo != null && !tracker.syncInfo.IsDestroyed) tracker.syncInfo.Destroy();
        Object.Destroy(go); // not CheckDamage/SafeDestroyObject: do not spill loot
    }
    static Vector3 Position(WorldObjectRecord record) => new(record.position[0], record.position[1], record.position[2]);
    static Quaternion Rotation(WorldObjectRecord record) => new(record.rotation[0], record.rotation[1], record.rotation[2], record.rotation[3]);
    static int Depth(Transform transform) { int depth = 0; for (var t = transform.parent; t != null; t = t.parent) depth++; return depth; }
    static void Skip(Dictionary<string, int> skips, string resource, string reason) {
        string key = $"res={resource ?? "<none>"} reason={reason}";
        skips.TryGetValue(key, out int count); skips[key] = count + 1;
    }
    static void RequireWorld() {
        if (!Util.GameIsFull() || !Net.IsRunning || WorldGeneration.world == null)
            throw new InvalidOperationException("World snapshot requires an active fullgame world.");
    }
    static void RequireServer() {
        if (!Util.GameIsFull() || !Net.IsRunning || !Net.IsServer || WorldGeneration.world == null)
            throw new InvalidOperationException("World object snapshots require an active fullgame server world.");
    }
}

[HarmonyPatch(typeof(CorpseScript), "Start")]
internal static class WorldSnapshot_CorpseStart {
    static bool Prefix(CorpseScript __instance) => !Util.GameIsFull() || __instance.GetComponent<WorldSnapshotRestored>() == null;
}
[HarmonyPatch(typeof(DestroyWithChance), "Start")]
internal static class WorldSnapshot_DestroyWithChanceStart {
    static bool Prefix(DestroyWithChance __instance) {
#if TESTING
        if (StructureCoverageRegression.Enabled && __instance.GetComponentInParent<CoverageFixtureRoot>() != null) return false;
#endif
        return !Util.GameIsFull() || __instance.GetComponentInParent<WorldSnapshotRestored>() == null;
    }
}
[HarmonyPatch(typeof(LifepodPump), "Awake")]
internal static class WorldSnapshot_PumpAwake {
    static bool Prefix() {
#if TESTING
        if (StructureCoverageRegression.Placing) return false;
#endif
        return !WorldObjectSnapshot.Instantiating;
    }
}
#endif
