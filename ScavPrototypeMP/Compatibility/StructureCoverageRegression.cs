#if FULLGAME && TESTING
using Newtonsoft.Json;
using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;
using Object = UnityEngine.Object;

namespace Together;

internal sealed class CoverageFixtureRoot : MonoBehaviour { }
internal static class StructureCoverageRegression {
    internal static bool Placing;
    internal static bool Enabled => Environment.GetCommandLineArgs().Contains("--mp-structure-coverage");
    internal static void Inventory() {
        if (!Enabled) return;
        var structures = Resources.LoadAll<GameObject>("Structures");
        var all = Resources.LoadAll<GameObject>("");
        var inventory = all.Where(p => p.GetComponentsInChildren<BuildingEntity>(true).Length != 0 || structures.Contains(p))
            .Select(p => new { name = p.name, structure = structures.Contains(p), path = ResourcePath(p),
                nodes = p.GetComponentsInChildren<BuildingEntity>(true).Select(b => new { name = b.name, id = b.id,
                    tilemap = b.GetComponent<UnityEngine.Tilemaps.Tilemap>() != null,
                    components = b.GetComponents<MonoBehaviour>().Where(c => c != null).Select(c => c.GetType().Name).ToArray(),
                    reason = WorldObjectSnapshot.CoverageReason(b.gameObject) }).ToArray() }).ToArray();
        File.WriteAllText(Path.Combine(SavesystemPatch.mpsavefolder, "resource-inventory.json"), JsonConvert.SerializeObject(inventory, Formatting.Indented));
        log.l($"[structure-coverage] inventory structures={structures.Length} buildingRoots={inventory.Length}");
    }
    internal static string ResourcePath(GameObject p) {
        foreach (var prefix in new[] { "", "Structures/", "Special/", "Buildings/" })
            if (Resources.Load<GameObject>(prefix + p.name) == p) return prefix + p.name;
        return null;
    }
    internal sealed class Node {
        public string resource;
        public float x, y;
        public bool active;
        public string[] components;
    }
    internal sealed class Row {
        public string template, route;
        public float terrainX, terrainY;
        public string terrainHash;
        public List<Node> nodes = new();
        [JsonIgnore] internal List<GameObject> placed = new();
        public int restored;
        public bool identity = true, position = true, active = true, components = true;
        public bool terrain = true;
    }
    static List<Row> rows;
    static string Folder => SavesystemPatch.mpsavefolder;
    internal static IEnumerator PlaceAll() {
        if (!Enabled) yield break;
        Inventory();
        rows = new List<Row>();
        var structures = Resources.LoadAll<GameObject>("Structures");
        var all = Resources.LoadAll<GameObject>("");
        var templates = structures.Concat(all.Where(p =>
            (p.GetComponent<BuildingEntity>() != null && !WorldObjectSnapshot.IsDynamicNode(p.transform)) ||
            p.GetComponentsInChildren<Tilemap>(true).Any(m => m.GetComponent<BuildingEntity>() != null)))
            .Concat(ScavMPBuildingSynchronizer.known_entities_with_nonunique_id.Values.Select(p => p.transform.root.gameObject))
            .Distinct().Where(p => ResourcePath(p) != null && p.name != "EndLab").OrderBy(ResourcePath).ToArray();
        int i = 0;
        foreach (var prefab in templates) {
            Placing = true;
            var row = new Row { template = ResourcePath(prefab) };
            rows.Add(row);
            var pos = new Vector2(-420 + (i % 13) * 65, 340 - (i / 13) * 65); i++;
            if (prefab.GetComponent<BuildingEntity>() != null) {
                string reason = WorldObjectSnapshot.CoverageReason(prefab);
                if (reason != "ordinary snapshot" && prefab.GetComponent<Tilemap>() == null) {
                    row.route = "nested-only: " + reason;
                    Placing = false; continue;
                }
                var go = Object.Instantiate(prefab, pos, Quaternion.identity);
                go.AddComponent<CoverageFixtureRoot>();
                row.placed.Add(go); row.route = "object";
                if (go.GetComponent<Tilemap>() != null) go.transform.SetParent(WorldGeneration.world.worldGrid.transform);
                if (go.TryGetComponent<CrystalBehaviour>(out var crystal)) {
                    crystal.effects = new List<CrystalEffect>();
                    // An effectful crystal, with a deterministic saved cooldown.
                    if (crystal.crystalSize >= .4f) {
                        var effect = new CrystalHealing { crystal = crystal }; effect.Start();
                        crystal.effects.Add(effect);
                    }
                }
            } else {
                row.route = "terrain+children";
                row.terrainX = pos.x; row.terrainY = pos.y;
                var terrain = prefab.GetComponent<Tilemap>() ?? prefab.GetComponentsInChildren<Tilemap>(true).FirstOrDefault(m => m.GetComponent<BuildingEntity>() == null);
                if (terrain != null) WorldGeneration.world.GenerateObjectAtPos(WorldGeneration.world.WorldToBlockPos(pos), terrain, 1f, genMode: true);
                var before = new HashSet<int>(Object.FindObjectsOfType<BuildingEntity>(true).Select(b => b.GetInstanceID()));
                WorldGeneration.world.GenerateEntityAtPos(pos, prefab);
                row.placed.AddRange(Object.FindObjectsOfType<BuildingEntity>(true).Where(b => !before.Contains(b.GetInstanceID()) &&
                    b.GetComponentInParent<BuildingEntity>() == b).Select(b => b.gameObject));
                foreach (var go in row.placed) go.AddComponent<CoverageFixtureRoot>();
            }
            Placing = false;
            yield return null;
        }
        yield return null;
        foreach (var row in rows.Where(r => r.route.StartsWith("nested-only"))) {
            var prefab = Resources.Load<GameObject>(row.template);
            var nested = rows.SelectMany(r => r.placed).Where(g => g != null).SelectMany(g => g.GetComponentsInChildren<BuildingEntity>(true))
                .FirstOrDefault(b => b.id == prefab.GetComponent<BuildingEntity>().id);
            if (nested != null) { row.placed.Add(nested.gameObject); row.route += " (owner background)"; }
        }
        foreach (var row in rows) foreach (var go in row.placed.Where(g => g != null)) {
            if (go.TryGetComponent<Rigidbody2D>(out var rb)) rb.simulated = false;
            if (go.TryGetComponent<Climbable>(out var climb) && (climb.points == null || climb.points.Count < 2)) {
                climb.points = new List<Vector2> { (Vector2)go.transform.position + Vector2.down * 4, (Vector2)go.transform.position + Vector2.up * 4 };
                climb.CalculateTotalLength();
            }
        }
        File.WriteAllText(Path.Combine(Folder, "coverage-placement.json"), JsonConvert.SerializeObject(rows, Formatting.Indented));
        log.l($"[structure-coverage] placed templates={rows.Count} nodes={rows.Sum(r => r.placed.Count)}");
    }
    internal static void BeforeSave() {
        if (!Enabled) return;
        foreach (var row in rows) {
            if (row.route == "terrain+children") row.terrainHash = TerrainHash(row.terrainX, row.terrainY);
            foreach (var go in row.placed) {
                if (go == null) throw new Exception("Coverage instance vanished before saving: " + row.template);
                row.nodes.Add(new Node { resource = SyncInfo.GetItemID(go), x = go.transform.position.x, y = go.transform.position.y,
                    active = ChunkSleep.SnapshotActive(go), components = Components(go) });
            }
        }
        File.WriteAllText(Path.Combine(Folder, "coverage-expected.json"), JsonConvert.SerializeObject(rows, Formatting.Indented));
    }
    static string[] Components(GameObject go) => go.GetComponentsInChildren<MonoBehaviour>(true)
        .Where(c => c != null && c.GetType().Assembly == typeof(BuildingEntity).Assembly).Select(c => c.GetType().Name).OrderBy(x => x).ToArray();
    internal static void Applied(WorldObjectDocument doc) {
        if (!Enabled) return;
        var expected = JsonConvert.DeserializeObject<List<Row>>(File.ReadAllText(Path.Combine(Folder, "coverage-expected.json")));
        var live = Object.FindObjectsOfType<BuildingEntity>(true).Where(b => b.gameObject.scene.IsValid() && b.GetComponentInParent<WorldSnapshotRestored>() != null).ToArray();
        foreach (var row in expected) foreach (var node in row.nodes) {
            var candidates = live.Where(m => SyncInfo.GetItemID(m.gameObject) == node.resource && Vector2.Distance(m.transform.position, new Vector2(node.x, node.y)) < .01f).ToArray();
            if (candidates.Length != 1) { row.identity = false; continue; }
            row.restored++;
            var go = candidates[0].gameObject;
            row.active &= ChunkSleep.SnapshotActive(go) == node.active;
            row.components &= Components(go).SequenceEqual(node.components);
        }
        foreach (var row in expected.Where(r => r.terrainHash != null)) row.terrain = TerrainHash(row.terrainX, row.terrainY) == row.terrainHash;
        var side = Net.IsServer ? "host" : "client";
        File.WriteAllText(Path.Combine(Folder, "coverage-" + side + ".json"), JsonConvert.SerializeObject(expected, Formatting.Indented));
        bool pass = expected.All(r => r.restored == r.nodes.Count && r.identity && r.active && r.components && r.terrain);
        log.l($"[structure-coverage] {(pass ? "PASS" : "FAIL")} side={side} templates={expected.Count} restored={expected.Sum(r => r.restored)}/{expected.Sum(r => r.nodes.Count)}");
        if (!pass) throw new Exception("Structure coverage failed; see coverage-" + side + ".json");
    }
    static string TerrainHash(float x, float y) {
        var world = WorldGeneration.world; var center = world.WorldToBlockPos(new Vector2(x, y));
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
        for (int iy = center.y - 32; iy < center.y + 32; iy++) for (int ix = center.x - 40; ix < center.x + 40; ix++)
            if (ix >= 0 && iy >= 0 && ix < world.width && iy < world.height) writer.Write(world.worldBlocks[ix, iy]);
        return WorldSnapshotTransfer.Hash(bytes.ToArray());
    }
    static int clientStage = -1;
    static bool verifyingClient;
    internal static void Tick() {
        if (!Enabled || !Net.IsClient || !CasualtiesTogetherUtils.Util.IsWorldGenerated() || verifyingClient) return;
        string stage = Path.Combine(Folder, "coverage-stage.txt");
        if (!File.Exists(stage) || !int.TryParse(File.ReadAllText(stage), out int index) || index <= clientStage) return;
        clientStage = index; verifyingClient = true;
        CasualtiesTogetherUtils.Util.StartCoroutine(ClientApproach(index));
    }
    internal static IEnumerator ApproachAll() {
        if (!Enabled) yield break;
        var expected = JsonConvert.DeserializeObject<List<Row>>(File.ReadAllText(Path.Combine(Folder, "coverage-expected.json")));
        for (int index = 0; index < expected.Count; index++) {
            var row = expected[index];
            if (row.nodes.Count > 0) foreach (var player in ScavPlayer.ClientIdToPlayerDict.Values)
                player.Server_TeleportCharacter(new Vector2(row.nodes[0].x, row.nodes[0].y + 1));
            File.WriteAllText(Path.Combine(Folder, "coverage-stage.txt"), index.ToString());
            float until = Time.realtimeSinceStartup + 12;
            string ack = Path.Combine(Folder, "coverage-stage-" + index + ".json");
            while (!File.Exists(ack) && Time.realtimeSinceStartup < until) yield return null;
            if (!File.Exists(ack)) throw new Exception("Coverage approach timed out: " + row.template);
            var data = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(ack));
            if (data.Value<bool>("pass") != true) throw new Exception("Coverage approach failed: " + row.template + " " + data);
        }
        log.l($"[structure-coverage] PASS approached and exercised all {expected.Count} templates over real transport");
    }
    static IEnumerator ClientApproach(int index) {
        var row = JsonConvert.DeserializeObject<List<Row>>(File.ReadAllText(Path.Combine(Folder, "coverage-expected.json")))[index];
        yield return new WaitForSecondsRealtime(.75f);
        bool pass = true; int visible = 0, registered = 0, staticStream = 0, uses = 0;
        string error = null;
        try {
            foreach (var node in row.nodes) {
                var go = Object.FindObjectsOfType<BuildingEntity>(true).Where(b => b.GetComponentInParent<WorldSnapshotRestored>() != null)
                    .FirstOrDefault(b => SyncInfo.GetItemID(b.gameObject) == node.resource && Vector2.Distance(b.transform.position, new Vector2(node.x, node.y)) < 1)?.gameObject;
                if (go == null) throw new Exception("Missing approached node " + node.resource);
                if (node.active && !go.activeInHierarchy) throw new Exception("Sleeping node failed to wake " + node.resource);
                if (go.GetComponentsInChildren<Renderer>(true).Any(r => r.enabled && r.gameObject.activeInHierarchy)) visible++;
                var root = go.GetComponentInParent<WorldSnapshotRestored>().gameObject;
                if (NetObjectRegistry.ObjectShouldNotBeSynced(root)) staticStream++;
                else {
                    if (!NetObjectRegistry.IsRegistered(root)) throw new Exception("Missing network registration " + node.resource);
                    registered++;
                }
                if (go.GetComponent<Climbable>() is var climb && climb != null && (climb.points == null || climb.points.Count < 2 || climb.totalLength <= 0))
                    throw new Exception("Invalid climb path " + node.resource);
                if (go.GetComponent<UsableObject>() != null) {
                    if (!go.GetComponents<MonoBehaviour>().Any(c => c != null && HarmonyLib.AccessTools.Method(c.GetType(), "OnUse", Type.EmptyTypes) != null))
                        throw new Exception("Usable has no OnUse receiver " + node.resource);
                    go.SendMessage("OnUse", SendMessageOptions.RequireReceiver); uses++;
                }
            }
        } catch (Exception ex) { pass = false; error = ex.ToString(); }
        if (ConsoleScript.instance != null && ConsoleScript.instance.active) ConsoleScript.instance.ToggleActiveState();
        PlayerCamera.main.SetTimeScale(PlayerCamera.SpeedType.Normal, false);
        File.WriteAllText(Path.Combine(Folder, "coverage-stage-" + index + ".json"), JsonConvert.SerializeObject(new { row.template, pass, visible, registered, staticStream, uses, error }));
        log.l($"[structure-coverage] {(pass ? "PASS" : "FAIL")} approach template={row.template} visible={visible} registered={registered} static={staticStream} uses={uses} error={error}");
        verifyingClient = false;
    }
}
#endif
