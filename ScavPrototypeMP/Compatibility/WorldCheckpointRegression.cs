#if FULLGAME && TESTING
using CasualtiesTogetherUtils;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Tilemaps;
using Object = UnityEngine.Object;

namespace Together;

// Real generation -> current-layer save/exit -> menu LoadRun. Opt-in and isolated.
internal static class WorldCheckpointRegression {
    internal static bool Enabled => EndingTestPreferences.Enabled && Environment.GetCommandLineArgs().Contains("--mp-world-checkpoint-test");
    static bool starting, running, reloading;
    static string Folder => SavesystemPatch.mpsavefolder;
    static Vector2 target;
    static string[] beforeBackgrounds;
    static int layer;
    static uint width, height;
    static int saveId;
    static Vector2 spawn, disabledPosition;
    static bool Legacy => Environment.GetCommandLineArgs().Contains("--mp-world-checkpoint-legacy");
    static bool LoadOnly => Environment.GetCommandLineArgs().Contains("--mp-world-checkpoint-loadonly");
    internal static string Fault => Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--mp-world-snapshot-fault="))?.Split('=')[1] ?? "";
    static bool clientDone;
    static bool clientNewWorld;
    static float clientDeadline;
    internal static Vector2? CapturePoint;
    internal static void AlignCamera() {
        if (Enabled && CapturePoint.HasValue && Camera.main != null) {
            var p = CapturePoint.Value;
            Camera.main.transform.position = new Vector3(p.x, p.y, -3);
        }
    }
    internal static void RecordApplied(byte[][] parts) {
        if (!Enabled) return;
        var world = WorldGeneration.world;
        var doc = WorldObjectSnapshotData.Decode(parts[2], world.biomeDepth, world.width, world.height, WoundView.view.cInfo[2]);
        StructureCoverageRegression.Applied(doc);
        var live = Object.FindObjectsOfType<WorldSnapshotRestored>(true).Where(x => x.gameObject.scene.IsValid()).ToArray();
        int objects = live.Count(x => x.GetComponent<Tilemap>() == null), backgrounds = live.Count(x => x.GetComponent<Tilemap>() != null);
        Check(objects == doc.objects.Count && backgrounds == doc.backgrounds.Count, $"applied scene counts objects={objects}/{doc.objects.Count} backgrounds={backgrounds}/{doc.backgrounds.Count}");
        var evidence = new {
            side = Net.IsServer ? "host" : "client", layer = world.biomeDepth,
            objects, backgrounds,
            tiles = WorldSnapshotTransfer.Hash(WorldSnapshotTransfer.Inflate(parts[0], checked((int)(world.width * world.height)))),
            fluids = WorldSnapshotTransfer.Hash(WorldSnapshotTransfer.Inflate(parts[1], checked((int)(world.width * world.height)))),
            liveTiles = WorldSnapshotTransfer.Hash(WorldSnapshotTransfer.Inflate(SavesystemPatch.SerializeWorldBlocks(), checked((int)(world.width * world.height)))),
            liveFluids = WorldSnapshotTransfer.Hash(WorldSnapshotTransfer.Inflate(SavesystemPatch.SerializeWorldFluids(), checked((int)(world.width * world.height)))),
            identities = doc.objects.Select(o => o.networkKey).Concat(doc.backgrounds.Select(b => b.networkKey)).OrderBy(x => x).ToArray()
        };
        Check(evidence.tiles == evidence.liveTiles, "whole-layer live terrain equals authoritative snapshot");
        Check(evidence.fluids == evidence.liveFluids, "whole-layer live fluids equal authoritative snapshot");
        File.WriteAllText(Path.Combine(Folder, evidence.side + "-applied.json"), JsonConvert.SerializeObject(evidence));
    }
    internal static void RecordFailure(string reason) {
        if (!Enabled || !Net.IsClient) return;
        File.WriteAllText(Path.Combine(Folder, "failure-evidence.json"), JsonConvert.SerializeObject(new {
            reason, applied = WorldSnapshotTransfer.Applied,
            restoredObjects = Object.FindObjectsOfType<WorldSnapshotRestored>(true).Length
        }));
    }
    static void PrepareLoadOnly() {
        var doc = JsonConvert.DeserializeObject<WorldObjectDocument>(System.Text.Encoding.UTF8.GetString(Util.Decompress(File.ReadAllBytes(Path.Combine(Folder, "world_objects.bin")))));
        layer = doc.biome; width = doc.width; height = doc.height; saveId = doc.saveId;
        beforeBackgrounds = doc.backgrounds.Select(b => b.template + "@" + new Vector3(b.matrix[3], b.matrix[7], b.matrix[11]).ToString("F2")).OrderBy(x => x).ToArray();
        var pod = doc.backgrounds.First(b => b.template.Contains("SNAP:Lifepod|"));
        target = new Vector2(pod.matrix[3], pod.matrix[7] - 4);
        reloading = true;
    }
    static string Key(Tilemap m) => m.GetComponent<BuildingEntity>().id + "@" + m.transform.position.ToString("F2");
    static Tilemap[] Maps() => Object.FindObjectsOfType<Tilemap>(true).Where(m => m.GetComponent<BuildingEntity>() != null &&
        m.GetComponent<TrackerBuildingEntityBackgroundified>()?.is_a_background_tilemap == true && m.gameObject.scene.IsValid()).ToArray();
    static void Check(bool ok, string message) {
        log.l("[world-checkpoint-test] " + (ok ? "PASS " : "FAIL ") + message);
        if (!ok) throw new Exception(message);
    }
    internal static void Tick() {
        if (!Enabled || !Net.IsRunning) return;
        SteamManager.disableAchievements = true;
        if (Net.IsClient && CharacterLobby.ShouldShowPanel) CharacterLobby.ConfirmPanel();
        if (Net.IsServer && !starting && PreRunScript.instance != null && !CharacterReadyGate.Blocked && ScavPlayer.ClientIdToPlayerDict.Count >= (LoadOnly ? 1 : 2)) {
            starting = true;
            if (LoadOnly) { PrepareLoadOnly(); PreRunScript.instance.LoadRun(); }
            else PreRunScript.instance.StartRun();
        }
        if (Util.IsWorldGenerated()) {
            PlayerPlacementRegression.Tick();
            StructureCoverageRegression.Tick();
            if (Net.IsClient && !WorldSnapshotTransfer.SnapshotMode && !clientNewWorld) {
                clientNewWorld = true;
                Check(WorldGeneration.world.worldBlocks != null && SavesystemPatch.SerializeWorldBlocks().Length > 10000 && !WorldSnapshotTransfer.Applied, "new world client completed normal seed generation");
                File.WriteAllText(Path.Combine(Folder, "client-new-world-pass"), "PASS");
            }
            foreach (var p in ScavPlayer.ClientIdToPlayerDict.Values.Where(p => p.body != null)) {
                p.body.rb.simulated = false;
                foreach (var limb in p.body.limbs) if (limb != null && limb.rb != null) limb.rb.simulated = false;
            }
            if (ConsoleScript.instance != null && !ConsoleScript.instance.noClip) {
                ConsoleScript.instance.noClip = true;
                ConsoleScript.instance.noClipPos = ScavPlayer.LocalPlayer.body.transform.position;
            }
            // Fixtures keep the local test subject conscious while traversing
            // every trap. Remote replicas retain their real network poses.
            if (ScavPlayer.LocalPlayer?.body != null) ScavPlayer.LocalPlayer.body.ResetEntropy();
            if (Net.IsClient && !clientDone && File.Exists(Path.Combine(Folder, "client-target.json"))) {
                if (clientDeadline == 0) clientDeadline = Time.realtimeSinceStartup + 30;
                var xy = JsonConvert.DeserializeObject<float[]>(File.ReadAllText(Path.Combine(Folder, "client-target.json")));
                var point = new Vector2(xy[0], xy[1]);
                bool background = Maps().Any(m => m.gameObject.activeInHierarchy && Vector2.Distance(m.transform.position, point) < 25);
                bool pod = Object.FindObjectsOfType<LifepodController>().Any(p => Vector2.Distance(p.transform.position, point) < 25 && p.heater != null && p.heatButton != null);
                if (background && pod) {
                    clientDone = true;
                    Util.StartCoroutine(ClientVerify(point));
                    log.l("[world-checkpoint-test] PASS client received active unvisited background and functional lifepod after same-layer reload");
                } else if (Time.realtimeSinceStartup > clientDeadline) {
                    clientDone = true;
                    log.l($"[world-checkpoint-test] FAIL client region background={background} lifepod={pod}");
                }
            }
        }
        if (!Net.IsServer || running || !Util.IsWorldGenerated() || DeferredPlayerSaveRestore.InProgress) return;
        running = true;
        Util.StartCoroutine(reloading ? Verify() : Save());
    }
    static IEnumerator Save() {
        yield return StructureCoverageRegression.PlaceAll();
        layer = WorldGeneration.world.biomeDepth;
        width = WorldGeneration.world.width; height = WorldGeneration.world.height;
        saveId = WoundView.view.cInfo[2]; spawn = ScavPlayer.LocalPlayer.body.transform.position;
        // Actual structure templates, well outside both players' simulation windows.
        target = (Vector2)ScavPlayer.LocalPlayer.body.transform.position + Vector2.right * 180 + Vector2.down * 60;
        foreach (string resource in new[] { "Structures/MiniPod", "Structures/MedicalBuilding", "Lifepod" }) {
            var prefab = Resources.Load<GameObject>(resource);
            if (prefab == null) continue;
            log.l($"[world-checkpoint-test] template={resource} lifepods={prefab.GetComponentsInChildren<LifepodController>(true).Length}");
            WorldGeneration.world.GenerateEntityAtPos(target, prefab);
            target += Vector2.right * 35;
        }
        target -= Vector2.right * 70;
        yield return new WaitForSecondsRealtime(18);
        Check(File.Exists(Path.Combine(Folder, "client-new-world-pass")), "both instances completed new-world seed path");
        // Non-seed terrain edit: the old client path cannot reproduce these cells.
        for (int x = 40; x < 80; x++) for (int y = 40; y < 80; y++) WorldGeneration.world.worldBlocks[x, y] = (ushort)((x + y) % 2 == 0 ? 3 : 0);
        WorldGeneration.world.UpdateWorld();
        var sleepingPod = Maps().Where(m => !m.gameObject.activeSelf && m.GetComponent<BuildingEntity>().id.Contains("SNAP:Lifepod|"))
            .OrderBy(m => Vector2.Distance(m.transform.position, ScavPlayer.LocalPlayer.body.transform.position)).First();
        target = (Vector2)sleepingPod.transform.position + Vector2.down * 4;
        // An explicitly disabled root is distinct from optimisation sleep in v3.
        disabledPosition = target + Vector2.up * 15;
        var disabled = Object.Instantiate(Resources.Load<GameObject>("bandage"), disabledPosition, Quaternion.identity);
        disabled.SetActive(false);
        beforeBackgrounds = Maps().Select(Key).OrderBy(x => x).ToArray();
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "before-backgrounds.json"), JsonConvert.SerializeObject(beforeBackgrounds));
        log.l($"[world-checkpoint-test] before backgrounds={beforeBackgrounds.Length} inactive={Maps().Count(m => !m.gameObject.activeSelf)} lifepods={Object.FindObjectsOfType<LifepodController>(true).Length} target={target}");
        Check(Maps().Any(m => !m.gameObject.activeSelf), "unvisited backgrounds are sleeping before save");
        // Unsupported old data must fail before any destructive world mutation.
        var old = WorldObjectSnapshotData.Decode(SavesystemPatch.SerializeWorldObjects(), layer, width, height, saveId);
        int buildingsBefore = Object.FindObjectsOfType<BuildingEntity>(true).Length;
        old.version = 1;
        bool rejected = false;
        try { WorldObjectSnapshot.PreflightRestore(WorldObjectSnapshotData.Encode(old)); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && Object.FindObjectsOfType<BuildingEntity>(true).Length == buildingsBefore, "v1 lacking background poses rejected without removing structures");
        old.version = 3;
        old.backgrounds[0].template = "KMPSR_SNAP:MissingCheckpointResource|0";
        rejected = false;
        try { WorldObjectSnapshot.PreflightRestore(WorldObjectSnapshotData.Encode(old)); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && Object.FindObjectsOfType<BuildingEntity>(true).Length == buildingsBefore, "missing background resource rejected before cleanup");
        StructureCoverageRegression.BeforeSave();
        yield return PlayerPlacementRegression.BeforeSave();
        Check(SaveCurrentLayerExit.Request(), "current-layer save requested");
        yield return new WaitUntil(() => Util.IsInMainMenu() && !SaveCurrentLayerExit.IsSaving);
        foreach (string name in new[] { "world_tiles.bin", "world_fluid.bin", "world_objects.bin" })
            log.l($"[world-checkpoint-test] file={name} bytes={new FileInfo(Path.Combine(Folder, name)).Length}");
        if (Legacy) {
            string path = Path.Combine(Folder, "world_objects.bin");
            var doc = WorldObjectSnapshotData.Decode(File.ReadAllBytes(path), layer, width, height, saveId);
            doc.version = 2;
            foreach (var b in doc.backgrounds) if (Vector2.Distance(new Vector2(b.matrix[3], b.matrix[7]), spawn) > 80) b.active = false;
            foreach (var o in doc.objects) if (Vector2.Distance(new Vector2(o.position[0], o.position[1]), spawn) > 80) o.active = false;
            File.WriteAllBytes(path, WorldObjectSnapshotData.Encode(doc));
            log.l($"[world-checkpoint-test] legacy v2 fixture backgroundsInactive={doc.backgrounds.Count(b => !b.active)} objectsInactive={doc.objects.Count(o => !o.active)}");
        }
        reloading = true;
        PreRunScript.instance.LoadRun();
        running = false;
    }
    static IEnumerator Verify() {
        yield return new WaitForSecondsRealtime(4);
        Check(WorldGeneration.world.biomeDepth == layer, "same layer restored=" + layer);
        var after = Maps().Select(Key).OrderBy(x => x).ToArray();
        int missing = beforeBackgrounds.Except(after).Count();
        log.l($"[world-checkpoint-test] after backgrounds={after.Length} missing={missing} inactive={Maps().Count(m => !m.gameObject.activeSelf)} unwatchedInactive={Maps().Count(m => !m.gameObject.activeSelf && m.GetComponent<ChunkSleepAnchor>() == null)} lifepods={Object.FindObjectsOfType<LifepodController>(true).Length}");
        File.WriteAllText(Path.Combine(Folder, "after-backgrounds.json"), JsonConvert.SerializeObject(after));
        Check(beforeBackgrounds.SequenceEqual(after), "whole-layer background identities/poses identical");
        float ackDeadline = Time.realtimeSinceStartup + 35;
        while (!File.Exists(Path.Combine(Folder, "client-applied.json")) && Time.realtimeSinceStartup < ackDeadline) yield return null;
        Check(File.Exists(Path.Combine(Folder, "client-applied.json")), "client supplied complete application evidence");
        var hostApplied = JObject.Parse(File.ReadAllText(Path.Combine(Folder, "host-applied.json")));
        var clientApplied = JObject.Parse(File.ReadAllText(Path.Combine(Folder, "client-applied.json")));
        foreach (string key in new[] { "layer", "objects", "backgrounds", "tiles", "fluids", "identities" })
            Check(JToken.DeepEquals(hostApplied[key], clientApplied[key]), "host/client application identical: " + key);
        Check(Maps().All(m => m.gameObject.activeSelf || m.GetComponent<ChunkSleepAnchor>() != null), "all inactive restored backgrounds have a wake registration");
        yield return PlayerPlacementRegression.VerifyRestore();
        if (PlayerPlacementRegression.Minimal) {
            yield return PlayerPlacementRegression.NextLayer();
            File.WriteAllText(Path.Combine(Folder, "result.txt"), "MINIMAL OBSERVATIONS COMPLETE; inspect position errors separately");
            log.l("[world-checkpoint-test] complete minimal observations; position pass is recorded separately");
            yield break;
        }
        foreach (var p in ScavPlayer.ClientIdToPlayerDict.Values) p.Server_TeleportCharacter(target);
        yield return new WaitForSecondsRealtime(10);
        var nearby = Maps().Where(m => Vector2.Distance(m.transform.position, target) < 65).ToArray();
        int dead = nearby.Count(m => !m.gameObject.activeInHierarchy);
        log.l($"[world-checkpoint-test] approached backgrounds={nearby.Length} permanentlyInactive={dead}");
        Check(nearby.Length > 0 && dead == 0, "unvisited structures wake after approaching");
        var pods = Object.FindObjectsOfType<LifepodController>(true).Where(p => Vector2.Distance(p.transform.position, target) < 80).ToArray();
        Check(pods.Length > 0, "functional lifepod exists at unvisited placement");
        foreach (var pod in pods) {
            Check(pod.gameObject.activeInHierarchy && pod.heater != null && pod.heatButton != null, "lifepod controller/heater/button live");
            int state = pod.heatState;
            pod.ToggleHeatState();
            Check(pod.heatState == (state + 1) % 3, "lifepod heat interaction works");
            pod.ToggleHeatState(); pod.ToggleHeatState(); // reset before client round trip
        }
        if (!Legacy && !LoadOnly) {
            var disabled = Object.FindObjectsOfType<Item>(true).Where(i => i.id == "bandage" && Vector2.Distance(i.transform.position, disabledPosition) < .1f).ToArray();
            Check(disabled.Length == 1 && !disabled[0].gameObject.activeSelf, "v3 explicitly disabled object remains disabled after approach");
        }
        // Client had to follow the reload and then receive this previously cold
        // region from the host's spatial registry over the real transport.
        File.WriteAllText(Path.Combine(Folder, "client-target.json"), JsonConvert.SerializeObject(new[] { target.x, target.y }));
        yield return new WaitUntil(() => File.Exists(Path.Combine(Folder, "client-interaction.json")));
        var interaction = JObject.Parse(File.ReadAllText(Path.Combine(Folder, "client-interaction.json")));
        int networkKey = interaction.Value<int>("key"), expectedState = interaction.Value<int>("state");
        var authoritative = Object.FindObjectsOfType<WorldSnapshotRestored>(true).First(m => m.networkKey == networkKey).GetComponentInChildren<LifepodController>(true);
        yield return new WaitForSecondsRealtime(1);
        Check(authoritative.heatState == expectedState, "server observed actual client lifepod button interaction");
        File.WriteAllText(Path.Combine(Folder, "capture-at.txt"), DateTime.UtcNow.AddSeconds(4).Ticks.ToString());
        File.WriteAllText(Path.Combine(Folder, "server-interaction-pass"), "PASS");
        yield return Capture("host");
        yield return new WaitUntil(() => File.Exists(Path.Combine(Folder, "client-pass")));
        yield return StructureCoverageRegression.ApproachAll();
        yield return PlayerPlacementRegression.NextLayer();
        File.WriteAllText(Path.Combine(Folder, "result.txt"), "PASS");
        log.l("[world-checkpoint-test] complete");
    }
    static IEnumerator ClientVerify(Vector2 point) {
        var pod = Object.FindObjectsOfType<LifepodController>().First(p => Vector2.Distance(p.transform.position, point) < 25);
        var button = pod.heatButton.GetComponent<LifepodButton>();
        var root = button.transform.parent.gameObject;
        Check(NetObjectRegistry.IsRegistered(root), "client lifepod adopted by authoritative network entity");
        Check(root.GetComponent<WorldSnapshotRestored>() != null, "client lifepod retained checkpoint identity after spatial re-registration");
        int state = pod.heatState;
        log.l($"[world-checkpoint-test] client use body={ScavPlayer.LocalPlayer.body.transform.position} pod={pod.transform.position} sync={NetObjectRegistry.TryGetSyncInfo(root, out var debugSync)} id={debugSync?.syncId} key={root.GetComponent<WorldSnapshotRestored>().networkKey}");
        // Invoke the actual client use path; its existing patch relays the action.
        button.OnUse();
        yield return new WaitForSecondsRealtime(3);
        Check(pod.heatState == (state + 1) % 3, "client lifepod interaction returned through server");
        File.WriteAllText(Path.Combine(Folder, "client-interaction.json"), JsonConvert.SerializeObject(new { key = root.GetComponent<WorldSnapshotRestored>().networkKey, state = pod.heatState }));
        yield return new WaitUntil(() => File.Exists(Path.Combine(Folder, "server-interaction-pass")));
        File.WriteAllText(Path.Combine(Folder, "client-backgrounds.json"), JsonConvert.SerializeObject(Maps().Select(Key).OrderBy(x => x).ToArray()));
        yield return Capture("client");
        File.WriteAllText(Path.Combine(Folder, "client-pass"), "PASS");
    }
    static IEnumerator Capture(string side) {
        if (Con.IsConsoleOpen()) ConsoleScript.instance.ToggleActiveState();
        bool vision = Con._DEV_NO_VISION_EFFECT, fullBright = ConsoleScript.instance.fullBright;
        // Experiment and Milky intentionally have different night vision and
        // posterisation. Use identical render settings for terrain comparison.
        Con._DEV_NO_VISION_EFFECT = true; ConsoleScript.instance.fullBright = true;
        var xy = JsonConvert.DeserializeObject<float[]>(File.ReadAllText(Path.Combine(Folder, "client-target.json")));
        CapturePoint = new Vector2(xy[0], xy[1] + 3);
        var at = new DateTime(long.Parse(File.ReadAllText(Path.Combine(Folder, "capture-at.txt"))), DateTimeKind.Utc);
        while (DateTime.UtcNow < at) yield return null;
        yield return new WaitForEndOfFrame();
        ScreenCapture.CaptureScreenshot(Path.Combine(Folder, side + ".png"));
        var camera = Camera.main;
        log.l($"[world-checkpoint-test] screenshot side={side} camera={camera?.transform.position} size={camera?.orthographicSize} standardVision=true fullBright=true");
        yield return new WaitForSecondsRealtime(1);
        CapturePoint = null;
        Con._DEV_NO_VISION_EFFECT = vision; ConsoleScript.instance.fullBright = fullBright;
    }
}
[HarmonyPatch(typeof(PlayerCamera), "LateUpdate")]
internal static class WorldCheckpointRegressionCamera { static void Postfix() => WorldCheckpointRegression.AlignCamera(); }
[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.GenerateWorld))]
internal static class WorldCheckpointRegressionLayer {
    [HarmonyPriority(Priority.First)]
    static void Prefix(WorldGeneration __instance) {
        if (WorldCheckpointRegression.Enabled && Net.IsServer && !SaveSystem.loadedRun && Environment.GetCommandLineArgs().Contains("--mp-world-checkpoint-layer2"))
            __instance.biomeDepth = 2;
    }
}
[HarmonyPatch(typeof(Plugin), "Update")]
internal static class WorldCheckpointRegressionTick { static void Postfix() => WorldCheckpointRegression.Tick(); }
[HarmonyPatch(typeof(EndingFlowTests), nameof(EndingFlowTests.Tick))]
internal static class WorldCheckpointRegressionSkipEnding { static bool Prefix() => !WorldCheckpointRegression.Enabled; }
#endif
