#if FULLGAME && TESTING
using CasualtiesTogetherUtils;
using System;
using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Together;

// Real generated layers, real client ownership, real RPCs. File markers only
// coordinate observations; they never set production state or skip dialogue.
internal static class EndingRouteRegression {
    static string Folder => Path.GetDirectoryName(Environment.GetEnvironmentVariable("CT_ENDING_TEST_SAVE"));
    static bool HostCarrier => Environment.GetEnvironmentVariable("CT_ENDING_TEST_SCENARIO") == "full-route-host";
    static string PathFor(string name) => Path.Combine(Folder, name);
    static void Mark(string name) => File.WriteAllText(PathFor(name), "PASS");
    static void Check(bool pass, string message) {
        if (!pass) throw new Exception("[route-test] FAIL " + message);
        log.l("[route-test] PASS " + message);
    }
    static IEnumerator Wait(Func<bool> ready, string reason, float seconds = 90f) {
        float deadline = Time.realtimeSinceStartup + seconds;
        while (!ready()) {
            if (Time.realtimeSinceStartup > deadline) throw new Exception("[route-test] FAIL timeout " + reason);
            yield return null;
        }
    }
    static bool Ready(int layer) => WorldGeneration.world.biomeDepth == layer && Util.IsWorldGenerated() &&
        !WorldGeneration.world.generatingWorld && !WorldGeneration.world.doingRegen;
    static void Place(ScavPlayer p, Vector2 position) {
        p.body.Heal(); p.body.ForceStand(); p.body.forceNoMovement = true;
        p.Server_TeleportCharacter(position);
        p.body.SetVelocity(Vector2.zero);
    }
    static IEnumerator ObserveHeart(string side) {
        var heart = BlueHeart.main;
        float radius = heart.mat.GetFloat("_DesiredDistance");
        yield return new WaitForSecondsRealtime(.12f);
        Check(Mathf.Abs(heart.mat.GetFloat("_DesiredDistance") - radius) > .1f, side + " blue ring expands over rendered frames");
        Color color = heart.mat.GetColor("_Color");
        Check(color.r < .01f && color.g > .99f && color.b > .99f && color.a > 0f, side + " pulse material is visible cyan");
        Check(heart.wasClose && MusicManager.main.cutsceneVolMult < .01f, side + " native approach dialogue and music attenuation");
        ScreenCapture.CaptureScreenshot(PathFor(side + "-blue-ring-1.png"));
        yield return new WaitForSecondsRealtime(.2f);
        ScreenCapture.CaptureScreenshot(PathFor(side + "-blue-ring-2.png"));
        log.l($"[route-test] {side} pulse radius={heart.mat.GetFloat("_DesiredDistance"):0.0} edge={heart.mat.GetFloat("_EdgeSize"):0.00} color={color} camera={PlayerCamera.main.customLookPos}");
    }
    internal static IEnumerator Server() {
        yield return Wait(() => ScavPlayer.ClientIdToPlayerDict.Values.Count(p => p?.body != null && p.IsAlive()) == 2, "two bodies");
        yield return new WaitForSecondsRealtime(4);
        var world = WorldGeneration.world;
        var host = ScavPlayer.LocalPlayer;
        var remote = ScavPlayer.ClientIdToPlayerDict.Values.First(p => !p.IsLocal);
        var carrier = HostCarrier ? host : remote;
        var companion = HostCarrier ? remote : host;
        world.biomeDepth = 8;
        yield return world.RegenerateWorld();
        yield return Wait(() => Ready(9) && BlueHeart.main != null, "generated layer9");
        var heartPosition = (Vector2)BlueHeart.main.transform.position;
        File.WriteAllText(PathFor("heart-position"), heartPosition.x + "," + heartPosition.y);
        log.l($"[route-test] server generated heart={heartPosition} origin={EndingFlow.State.heartSceneX},{EndingFlow.State.heartSceneY}");
        Mark("layer9.ready");
        yield return Wait(() => File.Exists(PathFor("heart.client")), "client same heart position");
        Check(EndingFlow.State.heartSceneReady, "server captured genuine BlueHeartCont generation");
        foreach (Transform child in Resources.Load<GameObject>("Structures/BlueHeartCont").transform)
            log.l($"[route-test] prefab child={child.name} local={child.localPosition} components={string.Join(",", child.GetComponents<Component>().Select(c => c.GetType().Name))}");
        var pod = Object.FindObjectsOfType<CargoDrillPod>(true).First();
        var col = pod.GetComponent<Collider2D>();
        log.l($"[route-test] genuine drill origin={pod.transform.position} bounds={col?.bounds}");

        // Drop through the same component/registry path as crate/mining loot.
        Place(remote, host.pos + Vector2.right * 4f);
        var drop = Utils.Create("waterbottle", host.pos + Vector2.up * 3f, 0f);
        Item.itemFloating = true;
        drop.AddComponent<FreshItemDrop>();
        NetObjectRegistry.Server_EnsureItemIsNetworkRegistered(drop);
        yield return null;
        Check(NetObjectRegistry.TryGetSyncInfo(drop, out var sync), "fresh drop registered");
        File.WriteAllText(PathFor("drop.id"), sync.syncId.id.ToString());
        Mark("drop.ready");
        yield return Wait(() => File.Exists(PathFor("drop.client")), "client observed hover and expiry", 30f);
        Check(drop.GetComponent<FreshItemDrop>() == null && Mathf.Abs(drop.GetComponent<Rigidbody2D>().gravityScale - 1f) < .01f, "server drop expires with gravity restored");

        Place(host, heartPosition + Vector2.left * 20f);
        Place(remote, heartPosition + Vector2.right * 20f);
        yield return new WaitForSecondsRealtime(2f);
        yield return ObserveHeart("host");
        Mark("visual.ready");
        yield return Wait(() => File.Exists(PathFor("visual.client")), "client ring visual observation");
        Place(carrier, heartPosition + Vector2.right * 5f);
        yield return Wait(() => EndingFlow.State.cargoAcquiring, "server reserved pickup animation");
        Check(!EndingFlow.State.hasCargo && BlueHeart.main.cutsceneStarted, "pickup is reserved before embed and animates once");
        float heartY = BlueHeart.main.origPos.y;
        yield return new WaitForSecondsRealtime(1.5f);
        Check(BlueHeart.main != null && BlueHeart.main.origPos.y > heartY + .3f && BlueHeart.main.pulseSpeedAdd > 1f, "host sees native rising and accelerating pickup animation");
        yield return Wait(() => EndingFlow.State.hasCargo && EndingFlow.KeyPlayer == carrier, "selected player acquired genuine heart");
        Check(carrier.body.hasCargo && !companion.body.hasCargo, "selected player is unique authoritative cargo carrier");
        Mark("cargo.ready");
        yield return Wait(() => File.Exists(PathFor("cargo.client")), "client cargo verified");
        Vector2 inside = col != null ? (Vector2)col.bounds.center : (Vector2)pod.transform.position;
        Place(carrier, inside);
        Place(companion, inside + Vector2.right * .4f);
        yield return new WaitForSecondsRealtime(2);
        log.l($"[route-test] active drill origin={pod.transform.position} collider={col?.bounds} carrier={carrier.pos}");
        Check(EndingFlow.InPod(carrier, pod), "real prefab collider accepts carrier in pod");
        if (HostCarrier) pod.OnUse();
        else {
            // The current drill rule requires the cargo carrier to interact.
            // A host-side OnUse would impersonate the non-carrier host.
            Mark("drill.use");
            yield return Wait(() => world.doingRegen, "guest carrier used drill through network");
        }
        Check(world.doingRegen, "pod OnUse starts server regeneration");
        yield return Wait(() => Ready(10), "arrive layer10");
        Check(host.pos.y < 0 && remote.pos.y < 0, "both server bodies spawn at layer10 bottom");
        Mark("layer10.ready");
        yield return Wait(() => File.Exists(PathFor("layer10.client")), "client bottom spawn");
        Place(companion, new Vector2(0, world.halfHeight - 4f));
        yield return new WaitForSecondsRealtime(2);
        Check(EndingFlow.State.phase == EndingPhase.Cargo, "non-carrier at surface cannot start breach");
        Mark("waiting.ready");
        yield return new WaitForSecondsRealtime(2);
        GlobalDark.main.SetDarkAmount(0f);
        Place(carrier, new Vector2(0, world.halfHeight - 4f));
        yield return Wait(() => EndingFlow.State.phase == EndingPhase.Breach, "carrier starts breach");
        yield return new WaitForSecondsRealtime(.15f);
        Check(GlobalDark.main.image.color.a > 0f && GlobalDark.main.image.color.a < 1f && !ScrollableText.textExists, "host breach fades before narrative cover");
        yield return Wait(() => Ready(11) && EndingFlow.State.phase == EndingPhase.Laboratory, "arrive lab");
        var lab = EndLabScript.instance;
        var spawn = GameObject.Find("ENDLABSPAWN");
        Check(spawn != null && Vector2.Distance(host.pos, spawn.transform.position) < 3f && Vector2.Distance(remote.pos, spawn.transform.position) < 3f, "all players at server ENDLABSPAWN");
        Mark("lab.ready");
        yield return Wait(() => File.Exists(PathFor("lab.client")), "client lab spawn");
        Place(carrier, lab.exitPoint);
        yield return new WaitForSecondsRealtime(2);
        Check(!EndingFlow.IsFinished && !EndingFlow.State.labDialogueComplete, "early carrier exit cannot end dialogue");
        // Entry must still be witnessed at x<17; this is the native gate.
        Place(carrier, new Vector2(16f, spawn.transform.position.y));
        yield return Wait(() => EndingFlow.State.labEntered, "key enters scientist room");
        Place(carrier, new Vector2(20f, spawn.transform.position.y));
        yield return Wait(() => EndingFlow.State.labDialogueComplete, "natural scientist dialogue", 200f);
        Check(!EndingFlow.IsFinished, "dialogue completion alone does not finish");
        Place(companion, lab.exitPoint);
        yield return new WaitForSecondsRealtime(2);
        Check(!EndingFlow.IsFinished, "non-key exit after dialogue cannot finish");
        Place(carrier, lab.exitPoint);
        yield return Wait(() => EndingFlow.IsFinished, "key exit after dialogue");
        Check(EndingFlow.State.ending == 1, "biopod result");
        Mark("server.done");
        log.l("[route-test] server full-route complete");
    }

    internal static IEnumerator Client() {
        yield return Wait(() => File.Exists(PathFor("layer9.ready")) && Ready(9) && BlueHeart.main != null, "client generated heart");
        var values = File.ReadAllText(PathFor("heart-position")).Split(',');
        var expected = new Vector2(float.Parse(values[0]), float.Parse(values[1]));
        Check(Vector2.Distance(BlueHeart.main.transform.position, expected) < .2f, "client sees server heart coordinates");
        Mark("heart.client");
        yield return Wait(() => File.Exists(PathFor("drop.ready")), "drop announced");
        var id = (knetid)ushort.Parse(File.ReadAllText(PathFor("drop.id")));
        yield return Wait(() => NetObjectRegistry.TryGetSyncInfo(id, out var si) && si.go != null && si.go.GetComponent<FreshItemDrop>() != null, "fresh drop replicated");
        NetObjectRegistry.TryGetSyncInfo(id, out var info);
        var drop = info.go;
        Check(drop.GetComponent<FreshItemDrop>().timeLeft > 8f && drop.GetComponent<Rigidbody2D>().gravityScale < .01f, "client fresh drop hovers with server remaining time");
        yield return new WaitForSecondsRealtime(8.3f);
        Check(drop.GetComponent<FreshItemDrop>() != null && drop.GetComponent<Rigidbody2D>().gravityScale > 0f, "client fresh drop ramps gravity near expiry");
        yield return new WaitForSecondsRealtime(3f);
        Check(drop.GetComponent<FreshItemDrop>() == null && Mathf.Abs(drop.GetComponent<Rigidbody2D>().gravityScale - 1f) < .01f, "client fresh drop expires once");
        Mark("drop.client");
        yield return Wait(() => File.Exists(PathFor("visual.ready")), "client native heart approach");
        yield return ObserveHeart("client");
        Mark("visual.client");
        yield return Wait(() => EndingFlow.State.cargoAcquiring && BlueHeart.main != null && BlueHeart.main.cutsceneStarted, "client authoritative pickup event");
        Check(!EndingFlow.State.hasCargo, "client cannot embed cargo locally during pickup");
        yield return new WaitForSecondsRealtime(1f);
        Check(BlueHeart.main != null && BlueHeart.main.pulseSpeedAdd > 1f, "client sees one accelerating pickup animation");
        yield return Wait(() => File.Exists(PathFor("cargo.ready")) && EndingFlow.State.hasCargo, "client cargo state");
        Check(EndingFlow.KeyPlayer.IsLocal == !HostCarrier && ScavPlayer.LocalPlayer.body.hasCargo == !HostCarrier && BlueHeart.main == null, "client carrier and heart removal agree");
        Mark("cargo.client");
        if (!HostCarrier) {
            yield return Wait(() => File.Exists(PathFor("drill.use")), "carrier drill interaction requested");
            Object.FindObjectsOfType<CargoDrillPod>(true).First().OnUse();
        }
        yield return Wait(() => File.Exists(PathFor("layer10.ready")) && Ready(10), "client layer10");
        Check(ScavPlayer.LocalPlayer.pos.y < 0, "client layer10 spawn is bottom");
        Mark("layer10.client");
        yield return Wait(() => EndingFlow.State.phase == EndingPhase.Breach, "client breach");
        yield return new WaitForSecondsRealtime(.15f);
        Check(GlobalDark.main.image.color.a > 0f && GlobalDark.main.image.color.a < 1f && !ScrollableText.textExists, "client breach fades before cover");
        yield return Wait(() => File.Exists(PathFor("lab.ready")) && Ready(11), "client lab");
        var spawn = GameObject.Find("ENDLABSPAWN");
        Check(spawn != null && Vector2.Distance(ScavPlayer.LocalPlayer.pos, spawn.transform.position) < 3f, "client authoritative lab spawn");
        Mark("lab.client");
        yield return Wait(() => EndingFlow.IsFinished, "client result after natural dialogue", 230f);
        Check(EndingFlow.State.labDialogueComplete && EndingFlow.State.ending == 1, "client receives gated biopod result");
        Mark("client.done");
        log.l("[route-test] client full-route complete");
    }
}
#endif
