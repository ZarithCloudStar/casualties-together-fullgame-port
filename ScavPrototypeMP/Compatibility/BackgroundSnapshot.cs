#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.Tilemaps;
using Object = UnityEngine.Object;

namespace Together;

internal static class BackgroundSnapshot {
    const string Tag = BuildingEntity_Start_MultiplayerPatch.identified_bg_prefix + "SNAP:";
    static readonly HashSet<string> loggedTemplates = new();
    static readonly HashSet<string> unknownTemplates = new();
    static readonly HashSet<string> legacyUncaptured = new();

    internal sealed class Batch {
        internal WorldGeneration world;
        internal object session;
        internal int cleared, skipped;
        internal Dictionary<string, int> keptDynamic = new();
        internal List<(WorldBackgroundRecord record, GameObject go)> spawned = new();
    }

    internal static bool IsResourceId(string id) => id != null && id.StartsWith(Tag, StringComparison.Ordinal);

    // Called from the existing prefab registration, on both transports/machines.
    // The root resource AND child path distinguish multiple backgrounds in one structure.
    internal static bool TryRegisterPrefab(Transform child) {
        if (!Util.GameIsFull() || child == null || child.gameObject.scene.IsValid() || child.GetComponent<Tilemap>() == null) return false;
        var building = child.GetComponent<BuildingEntity>();
        if (building == null) return false;
        var root = child.root.gameObject;
        string resource = FindResource(root);
        if (resource == null) {
            if (unknownTemplates.Add(root.name)) log.warn($"LOAD: background template skipped root={root.name} reason=Resources path unresolved");
            return false;
        }
        var path = new List<int>();
        for (var t = child; t != root.transform; t = t.parent) path.Add(t.GetSiblingIndex());
        path.Reverse();
        string id = Tag + Uri.EscapeDataString(resource) + "|" + string.Join("/", path);
        if (string.IsNullOrEmpty(building.id)) legacyUncaptured.Add(id);
        Register(id, child.gameObject, resource);
        foreach (var nested in child.GetComponentsInChildren<Tilemap>(true))
            if (nested.transform != child && nested.GetComponent<BuildingEntity>() != null) TryRegisterPrefab(nested.transform);
        return true;
    }

    static void Register(string id, GameObject prefab, string resource) {
        prefab.GetComponent<BuildingEntity>().id = id;
        var tracker = prefab.GetComponent<TrackerBuildingEntityBackgroundified>() ?? prefab.AddComponent<TrackerBuildingEntityBackgroundified>();
        tracker.is_a_background_tilemap = true;
        ScavMPBuildingSynchronizer.known_entities_with_nonunique_id[id] = prefab;
        ScavMPBuildingSynchronizer.TEMP_known_entities_with_nonunique_id_to_og[id] = resource;
        if (loggedTemplates.Add(id)) log.l($"LOAD: background template registered id={id} resource={resource}");
    }

    // SyncInfo uses the same reference on clients even when their newly generated
    // map never happened to spawn this particular structure. WS1 restore and the
    // regular spatial entity stream resolve the same precise prefab child.
    internal static bool TryResolveNetworkResource(string id, out GameObject prefab) {
        prefab = null;
        if (!Util.GameIsFull() || !IsResourceId(id)) return false;
        try {
            string[] parts = id.Substring(Tag.Length).Split('|');
            if (parts.Length != 2) throw new InvalidDataException("Bad background resource reference.");
            string resource = Uri.UnescapeDataString(parts[0]);
            var root = Resources.Load<GameObject>(resource);
            if (root == null) throw new InvalidDataException("Background root resource not found: " + resource);
            var child = root.transform;
            if (parts[1].Length != 0) foreach (var step in parts[1].Split('/')) child = child.GetChild(int.Parse(step));
            if (child.GetComponent<Tilemap>() == null || child.GetComponent<BuildingEntity>() == null)
                throw new InvalidDataException("Referenced child is not a building Tilemap.");
            prefab = child.gameObject;
            Register(id, prefab, resource);
            return true;
        } catch (Exception ex) {
            log.warn($"LOAD: background resource skipped id={id} reason={ex.Message}");
            return false;
        }
    }

    static string FindResource(GameObject root) {
        foreach (string prefix in new[] { "", "Structures/", "structures/", "Special/" }) {
            string candidate = prefix + root.name;
            if (Resources.Load<GameObject>(candidate) == root) return candidate;
        }
        return null; // Never instantiate the whole structure as a guessed background.
    }

    internal static void Capture(WorldObjectDocument document) {
        int skipped = 0;
        int sleeping = 0;
        foreach (var map in SceneBackgrounds()) {
            try {
                var building = map.GetComponent<BuildingEntity>();
                string id = building.id;
                if (!IsResourceId(id) && ScavMPBuildingSynchronizer.known_entities_with_nonunique_id.TryGetValue(id, out var old) && old != null && TryRegisterPrefab(old.transform))
                    id = old.GetComponent<BuildingEntity>().id;
                if (!TryResolveNetworkResource(id, out var prefab)) throw new InvalidDataException("No precise background template reference.");
                SafeTemplate(prefab);
                string shape = Shape(map);
                if (shape != Shape(prefab.GetComponent<Tilemap>())) throw new InvalidDataException("Runtime tile contents/origin differ from the template.");
                var matrix = ToArray(map.transform.localToWorldMatrix);
                if (!WorldBackgroundPose.TryDecompose(matrix, out _, out _, out _)) throw new InvalidDataException("Unsupported tilted/sheared background transform.");
                var color = map.color;
                var renderer = map.GetComponent<TilemapRenderer>();
                var tracker = map.GetComponent<TrackerBuildingEntityBackgroundified>();
                if (ChunkSleep.IsSleeping(map.gameObject)) sleeping++;
                var components = new List<WorldObjectComponent>();
                WorldObjectSnapshot.CaptureFields(map.transform, "", components);
                document.backgrounds.Add(new WorldBackgroundRecord {
                    components = components, hasPump = map.GetComponentInChildren<LifepodPump>(true) != null,
                    networkKey = WorldSnapshotTransfer.SnapshotMode ? WorldSnapshotTransfer.Key(map.gameObject) : 0,
                    template = id, shape = shape, matrix = matrix, color = new[] { color.r, color.g, color.b, color.a },
                    health = building.GetHealth(), active = ChunkSleep.SnapshotActive(map.gameObject), visible = renderer == null || renderer.enabled,
                    backgroundified = tracker != null && tracker.is_backgroundified
                });
                log.l($"SAVE-EXIT: background captured template={id} pos=({matrix[3]:F3},{matrix[7]:F3}) origin={map.origin} size={map.size} health={building.GetHealth():F3}");
            } catch (Exception ex) {
                skipped++;
                log.warn($"SAVE-EXIT: background skipped name={map.name} reason={ex.Message}");
            }
        }
        log.l($"SAVE-EXIT: backgrounds captured count={document.backgrounds.Count} skipped={skipped} sleeping={sleeping} logicalActive={document.backgrounds.Count(b => b.active)} layer={document.biome}");
        if (skipped != 0) throw new InvalidDataException($"Cannot save the complete layer: {skipped} background structures could not be captured. Staying in the world.");
    }

    internal static void ValidateForRestore(WorldObjectDocument document) {
        if (document.version == 1) throw new InvalidDataException("Legacy v1 checkpoint contains no background poses. This layer cannot be restored completely; the save has been kept. / 旧版 v1 存档缺少建筑背景位置，无法完整恢复本层，原存档已保留。");
        foreach (var record in document.backgrounds) {
            if (!TryResolveNetworkResource(record.template, out var prefab)) throw new InvalidDataException("Background template unavailable: " + record.template);
            SafeTemplate(prefab);
            WorldObjectSnapshot.ValidateFields(prefab, record.components);
            if (Shape(prefab.GetComponent<Tilemap>()) != record.shape) throw new InvalidDataException("Background template changed: " + record.template);
        }
    }

    internal static Batch Stage(WorldObjectDocument document) {
        ValidateForRestore(document); // before clearing any generated background
        var batch = new Batch { world = WorldGeneration.world, session = WorldSnapshotTransfer.Session };
        var valid = new List<(WorldBackgroundRecord record, GameObject prefab)>();
        if (document.version >= 2) foreach (var record in document.backgrounds) {
            try {
                if (!TryResolveNetworkResource(record.template, out var prefab)) throw new InvalidDataException("Template unavailable.");
                SafeTemplate(prefab);
                if (Shape(prefab.GetComponent<Tilemap>()) != record.shape) throw new InvalidDataException("Template tile origin/content changed.");
                if (document.version < 3 && !record.active && prefab.activeSelf) record.active = true;
                valid.Add((record, prefab));
            } catch (Exception ex) { batch.skipped++; log.warn($"LOAD: background skipped template={record.template} reason={ex.Message}"); }
        }
        if (batch.skipped != 0) throw new InvalidDataException($"Cannot restore {batch.skipped} background templates; generated structures have been retained.");
        // These are the newly GENERATED backgrounds, not the saved ones. Ordinary
        // object cleanup intentionally excludes Tilemaps; they must be replaced here.
        foreach (var map in SceneBackgrounds().OrderByDescending(x => Depth(x.transform)).ToArray()) {
            if (document.structureCatalog == 0 && legacyUncaptured.Contains(map.GetComponent<BuildingEntity>().id) &&
                !document.backgrounds.Any(b => b.template == map.GetComponent<BuildingEntity>().id)) {
                // Old captures never contained these empty-ID poses. Retain
                // their seed baseline as fix6 did, then include it in WS1.
                log.warn($"LOAD: legacy baseline retained for previously uncaptured background {map.GetComponent<BuildingEntity>().id}");
                continue;
            }
            foreach (var kept in WorldObjectSnapshot.DetachGeneratedDynamics(map.gameObject)) WorldObjectSnapshot.CountKept(batch.keptDynamic, kept);
            if (WorldObjectSnapshot.IsDynamicNode(map.transform)) {
                // Rare unsupported root: keep its controller but hide the stale
                // background mesh instead of destroying the NPC or guessing a pose.
                if (map.TryGetComponent<TilemapRenderer>(out var renderer)) renderer.enabled = false;
                WorldObjectSnapshot.CountKept(batch.keptDynamic, map.gameObject);
                log.warn($"LOAD: background dynamic root retained, renderer hidden id={map.GetComponent<BuildingEntity>().id}");
                continue;
            }
            Remove(map.gameObject);
            batch.cleared++;
        }
        log.l($"LOAD: background cleared={batch.cleared} saved={valid.Count} format={document.version}");
        foreach (var entry in valid) {
            GameObject go = null;
            try {
                // Instantiate only the exact Tilemap child, never the whole structure
                // (which would also recreate chests, supplies and sibling entities).
                WorldObjectSnapshot.Instantiating = true;
                try { go = Object.Instantiate(entry.prefab, Vector3.zero, Quaternion.identity); }
                finally { WorldObjectSnapshot.Instantiating = false; }
                int key = Net.IsServer ? -(batch.spawned.Count + 1) : entry.record.networkKey;
                if (key == 0) throw new InvalidDataException("Checkpoint lacks background network identity.");
                WorldSnapshotTransfer.Track(go, key);
                StripNestedBackgrounds(go);
                Apply(go, entry.record, batch.world);
                batch.spawned.Add((entry.record, go));
            } catch (Exception ex) {
                batch.skipped++; if (go != null) Remove(go);
                log.warn($"LOAD: background spawn skipped template={entry.record.template} reason={ex.Message}");
            }
        }
        return batch;
    }

    // The ordinary object coordinator calls this after its existing Start frame.
    internal static void Finish(Batch batch) {
        if (batch == null) return;
        int restored = 0, registered = 0;
        if (!Net.IsRunning || batch.world == null || !ReferenceEquals(batch.world, WorldGeneration.world) ||
            !ReferenceEquals(batch.session, WorldSnapshotTransfer.Session)) {
            log.warn("LOAD: background restore cancelled; world/session changed");
            return;
        }
        foreach (var entry in batch.spawned) {
            try {
                if (entry.go == null) throw new InvalidDataException("Background disappeared during Start.");
                if (Shape(entry.go.GetComponent<Tilemap>()) != entry.record.shape) throw new InvalidDataException("Background shape changed during Start.");
                Apply(entry.go, entry.record, batch.world, true);
                var map = entry.go.GetComponent<Tilemap>();
                if (Net.IsServer && entry.go.activeInHierarchy && NetObjectRegistry.ShouldKeepRegistered(entry.go)) {
                    var sync = NetObjectRegistry.Server_EnsureItemIsNetworkRegistered(entry.go);
                    if (sync == null) throw new InvalidOperationException("Background SyncInfo registration failed.");
                    sync.WriteObjectIntoPacket(); registered++;
                }
                log.l($"LOAD: background restored template={entry.record.template} pos=({entry.record.matrix[3]:F3},{entry.record.matrix[7]:F3}) origin={map.origin} size={map.size} health={entry.record.health:F3}");
                restored++;
            } catch (Exception ex) {
                batch.skipped++; if (entry.go != null) Remove(entry.go);
                log.warn($"LOAD: background restore skipped template={entry.record.template} reason={ex.Message}");
            }
        }
        log.l($"LOAD: background restored={restored} skipped={batch.skipped} cleared={batch.cleared} registered={registered}");
        if (batch.skipped != 0) throw new InvalidDataException("Incomplete background restore.");
    }

    static void Apply(GameObject go, WorldBackgroundRecord record, WorldGeneration world, bool final = false) {
        if (world.worldGrid == null) throw new InvalidOperationException("World Grid is unavailable.");
        var parent = world.worldGrid.transform;
        var desired = ToMatrix(record.matrix);
        var local = ToArray(parent.worldToLocalMatrix * desired);
        if (!WorldBackgroundPose.TryDecompose(local, out var position, out var rotation, out var scale)) throw new InvalidDataException("Background parent introduces unsupported shear/tilt.");
        go.transform.SetParent(parent, false);
        go.transform.localPosition = new Vector3(position[0], position[1], position[2]);
        go.transform.localRotation = new Quaternion(rotation[0], rotation[1], rotation[2], rotation[3]);
        go.transform.localScale = new Vector3(scale[0], scale[1], scale[2]);
        if (ToArray(go.transform.localToWorldMatrix).Where((value, i) => Math.Abs(value - record.matrix[i]) > 0.001f).Any())
            throw new InvalidDataException("Background world transform failed verification.");
        var building = go.GetComponent<BuildingEntity>();
        building.id = record.template;
        AccessTools.Field(typeof(BuildingEntity), "_health").SetValue(building, record.health);
        var tracker = go.GetComponent<TrackerBuildingEntityBackgroundified>() ?? go.AddComponent<TrackerBuildingEntityBackgroundified>();
        tracker.is_a_background_tilemap = true;
        tracker.is_backgroundified = record.backgroundified;
        if (record.backgroundified) building.Backgroundify();
        go.GetComponent<Tilemap>().color = new Color(record.color[0], record.color[1], record.color[2], record.color[3]);
        if (go.TryGetComponent<TilemapRenderer>(out var renderer)) renderer.enabled = record.visible;
        WorldObjectSnapshot.ApplyFields(go, record.components, final);
        if (final && record.hasPump == false) foreach (var pump in go.GetComponentsInChildren<LifepodPump>(true)) Object.DestroyImmediate(pump.gameObject);
        if (final && record.components != null && go.GetComponentInChildren<LifepodController>(true) is var pod && pod != null) {
            pod.heatState--; pod.ToggleHeatState();
        }
        go.SetActive(record.active);
    }

    // Include sleeping objects across the WHOLE scene, independent of cameras/net IDs.
    static Tilemap[] SceneBackgrounds() => Object.FindObjectsOfType<Tilemap>(true).Where(map => {
        var building = map.GetComponent<BuildingEntity>();
        if (!map.gameObject.scene.IsValid() || !map.gameObject.scene.isLoaded || building == null || map.GetComponentInParent<Body>() != null) return false;
        var tracker = map.GetComponent<TrackerBuildingEntityBackgroundified>();
        return building.id == "background" || (building.id ?? "").StartsWith(BuildingEntity_Start_MultiplayerPatch.identified_bg_prefix) || (tracker != null && tracker.is_a_background_tilemap);
    }).ToArray();

    static void SafeTemplate(GameObject prefab) {
        if (WorldObjectSnapshot.IsDynamicNode(prefab.transform)) throw new InvalidDataException("Background root owns a dynamic controller.");
        if (prefab.GetComponentInChildren<Body>(true) != null || prefab.GetComponentInChildren<Item>(true) != null)
            throw new InvalidDataException("Background contains body/item lifecycle children.");
    }

    // Each background node is its own SyncInfo and snapshot entry. Keep a parent
    // template from bringing a second copy of a separately restored/synced child.
    internal static void StripNestedBackgrounds(GameObject root) {
        WorldObjectSnapshot.StripDynamicChildren(root); // NPCs are independent live/generated entities, never background prefab copies.
        foreach (var map in root.GetComponentsInChildren<Tilemap>(true).Where(x => x.gameObject != root && IsBackground(x))
            .OrderByDescending(x => Depth(x.transform)).ToArray()) {
            Object.DestroyImmediate(map.gameObject);
        }
    }

    static bool IsBackground(Tilemap map) {
        var building = map.GetComponent<BuildingEntity>();
        var tracker = map.GetComponent<TrackerBuildingEntityBackgroundified>();
        return building != null && (building.id == "background" || (building.id ?? "").StartsWith(BuildingEntity_Start_MultiplayerPatch.identified_bg_prefix) ||
            (tracker != null && tracker.is_a_background_tilemap));
    }

    static string Shape(Tilemap map) {
        var bounds = map.cellBounds;
        if ((long)bounds.size.x * bounds.size.y * bounds.size.z > 1000000) throw new InvalidDataException("Oversized background tile bounds.");
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true)) {
            writer.Write(map.origin.x); writer.Write(map.origin.y); writer.Write(map.origin.z);
            writer.Write(map.size.x); writer.Write(map.size.y); writer.Write(map.size.z);
            writer.Write(map.tileAnchor.x); writer.Write(map.tileAnchor.y); writer.Write(map.tileAnchor.z);
            writer.Write((int)map.orientation);
            foreach (float value in ToArray(map.orientationMatrix)) writer.Write(value);
            foreach (var cell in bounds.allPositionsWithin) {
                var tile = map.GetTile(cell);
                writer.Write(tile == null ? "" : tile.name);
                if (tile == null) continue;
                foreach (float value in ToArray(map.GetTransformMatrix(cell))) writer.Write(value);
                var color = map.GetColor(cell);
                writer.Write(color.r); writer.Write(color.g); writer.Write(color.b); writer.Write(color.a);
            }
        }
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes.ToArray())).Replace("-", "");
    }

    static float[] ToArray(Matrix4x4 matrix) {
        var values = new float[16];
        for (int row = 0; row < 4; row++) for (int col = 0; col < 4; col++) values[row * 4 + col] = matrix[row, col];
        return values;
    }
    static Matrix4x4 ToMatrix(float[] values) {
        var matrix = new Matrix4x4();
        for (int row = 0; row < 4; row++) for (int col = 0; col < 4; col++) matrix[row, col] = values[row * 4 + col];
        return matrix;
    }
    static int Depth(Transform transform) { int depth = 0; for (var t = transform.parent; t != null; t = t.parent) depth++; return depth; }
    static void Remove(GameObject go) {
        if (go == null) return;
        go.SetActive(false);
        if (NetObjectRegistry.TryGetSyncInfo(go, out var sync) && sync != null && !sync.IsDestroyed) sync.Destroy();
        else if (go.TryGetComponent<SyncInfoGameObjectTracker>(out var tracker) && tracker.syncInfo != null && !tracker.syncInfo.IsDestroyed) tracker.syncInfo.Destroy();
        Object.Destroy(go);
    }
}
#endif
