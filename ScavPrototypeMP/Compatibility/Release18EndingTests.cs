#if FULLGAME && TESTING
using CasualtiesTogetherUtils;
using System;
using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Together;

// Explicitly opt-in, in two real game processes. No shipping setup/cheat path.
internal static class Release18EndingTests {
    static string Scenario => Environment.GetEnvironmentVariable("CT_ENDING_TEST_SCENARIO") ?? "";
    static string Folder => Path.GetDirectoryName(Environment.GetEnvironmentVariable("CT_ENDING_TEST_SAVE"));
    static string PathFor(string name) => Path.Combine(Folder, name);
    static bool Seen(string name) => File.Exists(PathFor(name));
    static void Mark(string name) => File.WriteAllText(PathFor(name), "PASS");
    static Vector2? hold;
    static void Check(bool pass, string message) {
        if (!pass) throw new Exception("[r18-test] FAIL " + message);
        log.l("[r18-test] PASS " + message);
    }
    static IEnumerator Wait(Func<bool> predicate, string message, float seconds = 120f) {
        float until = Time.realtimeSinceStartup + seconds;
        while (!predicate()) {
            if (Time.realtimeSinceStartup > until) throw new Exception("[r18-test] FAIL timeout " + message);
            yield return null;
        }
    }
    internal static void Tick() {
        if (!Scenario.StartsWith("r18-") || !Util.IsInWorld()) return;
        var local = ScavPlayer.LocalPlayer;
        if (hold.HasValue && local?.body != null && !WorldGeneration.world.doingRegen) {
            local.netBody.SetBodyPosition(hold.Value);
            local.body.SetVelocity(Vector2.zero);
        }
    }
    static void Place(ScavPlayer p, Vector2 at) {
        p.body.Heal(); p.body.ForceStand(); p.Server_TeleportCharacter(at);
        p.body.forceWalk = false; p.body.forceNoMovement = true;
        p.body.SetVelocity(Vector2.zero);
        if (p.IsLocal) hold = at;
    }
    static bool Generated(int depth) => WorldGeneration.world.biomeDepth == depth &&
        !WorldGeneration.world.doingRegen && !WorldGeneration.world.generatingWorld && Util.IsWorldGenerated();
    static void Screenshot(string name) => ScreenCapture.CaptureScreenshot(PathFor((Net.IsServer ? "host-" : "client-") + name + ".png"));

    internal static IEnumerator Run() {
        yield return Wait(() => ScavPlayer.ClientIdToPlayerDict.Values.Count(p => p?.body != null) == 2 &&
            !DeferredPlayerSaveRestore.InProgress && Util.IsWorldGenerated(), "two loaded bodies");
        if (Net.IsClient) Mark("startup.client");
        else yield return Wait(() => Seen("startup.client") && ScavPlayer.ClientIdToPlayerDict.Values.Where(p => !p.IsHost).All(p => p.ServerPlayerState.finished_worldgen), "client startup acknowledged");
        yield return new WaitForSecondsRealtime(3f);
        if (Net.IsServer) yield return Server(); else yield return Client();
    }
    static IEnumerator Server() {
        var world = WorldGeneration.world;
        var host = ScavPlayer.LocalPlayer;
        var guest = ScavPlayer.ClientIdToPlayerDict.Values.First(p => !p.IsHost);
        if (Scenario == "r18-layer10") {
            world.biomeDepth = 9;
            EndingFlow.State = new EndingSaveData { phase = EndingPhase.Cargo, cargoTaken = true, hasCargo = true, cargoOwner = guest.GetPersistentId() };
            yield return world.RegenerateWorld();
            yield return Wait(() => Generated(10), "generated real layer10");
            Vector2 bottom = new(0f, -(float)world.halfHeight + 2f);
            Place(host, bottom); Place(guest, bottom + Vector2.right);
            world.savePanel.SetActive(false); Mark("bottom.ready");
            yield return Wait(() => Seen("bottom.client"), "client at layer10 bottom");
            yield return new WaitForSecondsRealtime(4f);
            Check(Generated(10) && EndingFlow.State.phase == EndingPhase.Cargo, "layer10 bottom stays layer10/Cargo for both players");
            Check(!ServerMain.IsLayerFinishPosition(bottom.y) && !ServerMain.CheckIfEnoughPeopleAreAtLayerFinish(), "bottom cannot satisfy generic finish/autocontinue");
            Check(!world.savePanel.activeSelf, "bottom does not open continue panel");
            Screenshot("layer10-bottom"); Mark("bottom.checked");
            yield return new WaitForSecondsRealtime(.25f);
            // Non-carrier at top must also wait; only the actual carrier breaches.
            Place(host, new Vector2(0f, world.halfHeight - 4f));
            yield return new WaitForSecondsRealtime(2f);
            Check(EndingFlow.State.phase == EndingPhase.Cargo && !world.doingRegen, "non-core top waits for cargo carrier");
            Mark("top.ready");
            yield return Wait(() => EndingFlow.State.phase == EndingPhase.Breach, "remote carrier top starts production breach");
            Check(ServerMain.IsLayerFinishPosition(world.halfHeight - 4f), "top satisfies vanilla layer10 position rule");
            Screenshot("layer10-top"); hold = null;
            yield return Wait(() => Generated(11) && EndLabScript.instance != null, "production breach regenerates layer11", 150f);
            yield return Wait(() => Seen("lab.client"), "client followed production breach");
            Check(EndingFlow.State.phase == EndingPhase.Laboratory, "party entered layer11 through top breach");
            Screenshot("layer11-arrived"); Mark("server.done");
            yield break;
        }
        world.biomeDepth = 10;
        EndingFlow.State = new EndingSaveData { phase = EndingPhase.Breach, cargoTaken = true, hasCargo = true, cargoOwner = guest.GetPersistentId() };
        yield return world.RegenerateWorld();
        yield return Wait(() => Generated(11) && EndLabScript.instance != null, "laboratory generated");
        yield return new WaitForSecondsRealtime(2f);
        log.l($"[r18-test] lab geometry spawnA={host.pos} spawnB={guest.pos} scientist={EndLabScript.instance.scientistAnim.transform.position} device={EndLabScript.instance.surgicalDevice.transform.position} exit={EndLabScript.instance.exitPoint}");
        foreach (float x in new[] { 18f, 22f, 30f, 40f, 50f }) {
            foreach (var hit in Physics2D.RaycastAll(new Vector2(x, 280f), Vector2.down, 800f).Where(h => h.collider != null && h.collider.GetComponentInParent<Body>() == null).Take(8))
                log.l($"[r18-test] entrance floor ray x={x} point={hit.point} normal={hit.normal} collider={hit.collider.name} type={hit.collider.GetType().Name} trigger={hit.collider.isTrigger}");
        }
        ChipState.ApplyServer(host, false, false);
        ChipState.ApplyServer(guest, true, false);
        Multiplayer.rules.sv_cheats = true;
        Con.RunCommand("godmode 1");
        foreach (string id in new[] { "shotgun", "boxof12gauge" }) {
            var item = Utils.Create(id, host.pos, 0f).GetComponent<Item>();
            NetObjectRegistry.Server_EnsureItemIsNetworkRegistered(item.gameObject);
            host.body.AutoPickUpItem(item);
        }
        // The scientist hall is near y=0; y=234 belongs to the later escape
        // corridor. Read the real scene binding instead of mixing those rooms.
        float entranceY = EndLabScript.instance.scientistAnim.transform.position.y;
        File.WriteAllText(PathFor("entrance.y"), entranceY.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Place(host, new Vector2(22f, entranceY)); Place(guest, new Vector2(23f, entranceY));
        Mark("setup.ready");
        yield return Wait(() => Seen("setup.client"), "client sees chip/cargo and entrance position");
        Check(!host.modUnchipped && guest.modUnchipped && EndingFlow.KeyPlayer == guest && guest.body.hasCargo && !host.body.hasCargo, "A chipped / B unchipped unique cargo carrier");
        Check(host.body.FindByIdThorough("shotgun", out _) && host.body.FindByIdThorough("boxof12gauge", out _), "A inventory contains shotgun and boxof12gauge");
        Check(Con._DEV_GODMODE && Con.CanCheat(), "godmode console command enabled and allowed");
        // Release fixture placement: the user can now walk across x<17 to trigger
        // the real scientist scene. No coroutine skips the dialogue in this mode.
        hold = null;
        foreach (var p in ScavPlayer.ClientIdToPlayerDict.Values) { p.body.forceNoMovement = false; p.body.forceWalk = false; }
        Mark("setup.release");
        yield return new WaitForSecondsRealtime(3f);
        Check(host.pos.x > 17f && guest.pos.x > 17f && Mathf.Abs(host.pos.y - entranceY) < 10f && Mathf.Abs(guest.pos.y - entranceY) < 10f &&
            Mathf.Abs(host.body.rb.velocity.y) < 1f && Mathf.Abs(guest.body.rb.velocity.y) < 1f && !EndingFlow.State.labEntered,
            "both players stand on real entrance floor with controls released before trigger");
        Screenshot("escape-ready");
        log.l($"[r18-test] READY A={host.GetPersistentId()} chip=True god={Con._DEV_GODMODE} pos={host.pos}; B={guest.GetPersistentId()} chip=False cargo={guest.body.hasCargo} pos={guest.pos}; trigger=x<17");
        Mark("server.ready");
    }
    static IEnumerator Client() {
        if (Scenario == "r18-layer10") {
            yield return Wait(() => Seen("bottom.ready") && Generated(10), "layer10 bottom fixture");
            hold = new Vector2(1f, -(float)WorldGeneration.world.halfHeight + 2f);
            yield return new WaitForSecondsRealtime(1f);
            Screenshot("layer10-bottom"); Mark("bottom.client");
            yield return Wait(() => Seen("bottom.checked"), "server checked bottom");
            Check(Generated(10) && EndingFlow.State.phase == EndingPhase.Cargo, "client bottom remains layer10");
            yield return Wait(() => Seen("top.ready"), "carrier top fixture");
            hold = new Vector2(1f, WorldGeneration.world.halfHeight - 4f);
            yield return Wait(() => EndingFlow.State.phase == EndingPhase.Breach, "client top breach received");
            Screenshot("layer10-top"); hold = null;
            yield return Wait(() => Generated(11) && EndLabScript.instance != null, "client generated layer11", 150f);
            Screenshot("layer11-arrived"); Mark("lab.client"); Mark("client.done");
            Check(true, "client followed top breach to layer11"); yield break;
        }
        yield return Wait(() => Seen("setup.ready") && Generated(11) && EndLabScript.instance != null, "server prepared entrance");
        float entranceY = float.Parse(File.ReadAllText(PathFor("entrance.y")), System.Globalization.CultureInfo.InvariantCulture);
        hold = new Vector2(23f, entranceY);
        yield return Wait(() => ScavPlayer.LocalPlayer.modUnchipped && EndingFlow.KeyPlayer == ScavPlayer.LocalPlayer && ScavPlayer.LocalPlayer.body.hasCargo, "client unchipped cargo replication");
        Mark("setup.client");
        yield return Wait(() => Seen("setup.release"), "release user controls");
        hold = null; ScavPlayer.LocalPlayer.body.forceNoMovement = false; ScavPlayer.LocalPlayer.body.forceWalk = false;
        yield return new WaitForSecondsRealtime(3f);
        Check(Mathf.Abs(ScavPlayer.LocalPlayer.pos.y - entranceY) < 10f && Mathf.Abs(ScavPlayer.LocalPlayer.body.rb.velocity.y) < 1f &&
            ScavPlayer.LocalPlayer.pos.x > 17f && !EndingFlow.State.labEntered, "client naturally settled at entrance after fixture release");
        Screenshot("escape-ready");
        Check(true, "B ready at lab entrance, unchipped cargo carrier"); Mark("client.ready");
    }
}
#endif
