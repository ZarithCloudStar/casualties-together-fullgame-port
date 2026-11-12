#if FULLGAME && TESTING
using CasualtiesTogetherUtils;
using HarmonyLib;
using System;
using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Together;

internal static class DropChipRegression {
    internal static bool Active => (Environment.GetEnvironmentVariable("CT_ENDING_TEST_SCENARIO") ?? "").StartsWith("drop-chip");
    internal static string Folder => Path.GetDirectoryName(Environment.GetEnvironmentVariable("CT_ENDING_TEST_SAVE"));
    internal static string FileFor(string name) => Path.Combine(Folder, name);
    internal static void Check(bool ok, string message) {
        if (!ok) throw new Exception("[drop-test] FAIL " + message);
        log.l("[drop-test] PASS " + message);
    }
    internal static IEnumerator Wait(Func<bool> ready, string message) {
        float end = Time.realtimeSinceStartup + 80f;
        while (!ready()) {
            if (Time.realtimeSinceStartup > end) throw new Exception("[drop-test] FAIL timeout " + message);
            yield return null;
        }
    }
    internal static IEnumerator Run() {
        if (Environment.GetEnvironmentVariable("CT_ENDING_TEST_SCENARIO").Contains("verify") || Reloading) {
            yield return DropSourceRegression.Run();
            yield break;
        }
        if (Net.IsServer) {
            yield return Wait(() => ScavPlayer.ClientIdToPlayerDict.Values.Count(p => p?.body != null) == 2, "two bodies");
            yield return new WaitForSecondsRealtime(3f);
            var host = ScavPlayer.LocalPlayer;
            var remote = ScavPlayer.ClientIdToPlayerDict.Values.First(p => !p.IsLocal);
            if (Environment.GetEnvironmentVariable("CT_ENDING_TEST_SCENARIO").Contains("sources")) {
                var point = host.pos + Vector2.right * 40f;
                remote.Server_TeleportCharacter(point + Vector2.left * 2f);
                remote.body.forceNoMovement = true;
                yield return new WaitForSecondsRealtime(1f);
                foreach (var building in Resources.FindObjectsOfTypeAll<BuildingEntity>().Where(b => b.itemsDropOnDestroy.Length > 0 || b.guaranteedDropAmount > 0 || b.alwaysDrop.Length > 0).Take(50))
                    log.l($"[drop-test] prefab id={building.id} health={building.GetHealth()} guaranteed={building.guaranteedDropAmount} categories={string.Join(",", building.itemCategoriesToAdd)}");
                var crate = Utils.Create("dropcapsule", point, 0f).GetComponent<BuildingEntity>();
                yield return null;
                var previous = UnityEngine.Object.FindObjectsOfType<Item>().Select(i => i.GetInstanceID()).ToHashSet();
                log.l($"[drop-test] opening remote crate hostDistance={Vector2.Distance(host.pos, point)} guestDistance={Vector2.Distance(remote.pos, point)}");
                crate.SetHealth(0f);
                yield return null;
                foreach (var loot in UnityEngine.Object.FindObjectsOfType<Item>().Where(i => !previous.Contains(i.GetInstanceID()))) {
                    log.l($"[drop-test] genuine crate loot id={loot.id} fresh={loot.GetComponent<FreshItemDrop>() != null} pos={loot.transform.position}");
                    loot.gameObject.AddComponent<DropFrameProbe>();
                }
                yield return new WaitForSecondsRealtime(12f);
                File.WriteAllText(FileFor("server.done"), "PASS");
                yield break;
            }
            remote.Server_TeleportCharacter(host.pos + Vector2.right * 3f);
            Item.itemFloating = true;
            var drop = Utils.Create("waterbottle", host.pos + Vector2.up * 3f, 0f);
            drop.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
            drop.AddComponent<FreshItemDrop>();
            NetObjectRegistry.Server_EnsureItemIsNetworkRegistered(drop);
            yield return null;
            Check(NetObjectRegistry.TryGetSyncInfo(drop, out var info), "drop registered");
            File.WriteAllText(FileFor("drop.id"), info.syncId.id.ToString());
            drop.AddComponent<DropFrameProbe>();
            yield return Wait(() => File.Exists(FileFor("client.done")), "client observation");
            Check(drop.GetComponent<FreshItemDrop>() == null, "host expired");
            File.WriteAllText(FileFor("server.done"), "PASS");
        } else {
            if (Environment.GetEnvironmentVariable("CT_ENDING_TEST_SCENARIO").Contains("sources")) {
                yield return Wait(() => File.Exists(FileFor("server.done")), "source drop observations");
                File.WriteAllText(FileFor("client.done"), "PASS");
                yield break;
            }
            yield return Wait(() => File.Exists(FileFor("drop.id")), "drop id");
            var id = (knetid)ushort.Parse(File.ReadAllText(FileFor("drop.id")));
            yield return Wait(() => NetObjectRegistry.TryGetSyncInfo(id, out var si) && si.go != null, "client object");
            NetObjectRegistry.TryGetSyncInfo(id, out var info);
            var drop = info.go;
            drop.AddComponent<DropFrameProbe>();
            float y = drop.transform.position.y;
            yield return new WaitForSecondsRealtime(3f);
            log.l($"[drop-test] displacement after 3s={drop.transform.position.y - y:0.000}");
            Check(Mathf.Abs(drop.transform.position.y - y) < .5f, "client actually hovers for three seconds");
            yield return new WaitForSecondsRealtime(8f);
            Check(drop.GetComponent<FreshItemDrop>() == null, "client expired");
            File.WriteAllText(FileFor("client.done"), "PASS");
        }
    }

    internal static bool Reloading => (Environment.GetEnvironmentVariable("CT_ENDING_TEST_SCENARIO") ?? "").Contains("reload");
    static bool panelStarted;
    internal static void TickPanel() {
        if (panelStarted) return;
        panelStarted = true;
        Util.StartCoroutine(Panel());
    }
    static IEnumerator Panel() {
        yield return new WaitForSecondsRealtime(.6f);
        Check(!CharacterLobby.DraftUnchipped, "new session panel defaults to chipped");
        Locale.currentLangName = "EN";
        yield return new WaitForSecondsRealtime(.2f);
        ScreenCapture.CaptureScreenshot(FileFor("panel-en.png"));
        CharacterLobby.PreviewChip(!Reloading);
        CharacterLobby.Preview(1);
        CharacterLobby.Preview(2);
        Check(CharacterLobby.DraftUnchipped == !Reloading, "chip draft survives character switching");
        Locale.currentLangName = "zh-CN";
        yield return new WaitForSecondsRealtime(.3f);
        ScreenCapture.CaptureScreenshot(FileFor("panel-zh.png"));
        yield return new WaitForSecondsRealtime(.2f);
        Check(CharacterLobby.ConfirmPanel(), "panel confirms character and chip together");
    }
}

internal sealed class DropFrameProbe : MonoBehaviour {
    int frames;
    float next;
    void LateUpdate() {
        if (frames++ > 60 && Time.unscaledTime < next) return;
        next = Time.unscaledTime + .5f;
        var fresh = GetComponent<FreshItemDrop>();
        var rb = GetComponent<Rigidbody2D>();
        NetObjectRegistry.TryGetSyncInfo(gameObject, out var si);
        log.l($"[drop-debug] side={(Net.IsServer ? "host" : "client")} frame={Time.frameCount} go={GetInstanceID()} component={fresh?.GetInstanceID()} parent={transform.parent?.name ?? "null"} forceOn={fresh?.forceOn} timeLeft={fresh?.timeLeft:0.000} gravity={rb?.gravityScale} enabled={fresh?.enabled} setting={Item.itemFloating} sync={si?.syncId} fresh={si?.freshitemdrop.Value} tenths={si?.freshDropTenths.Value} pos={transform.position} velocity={rb?.velocity}");
    }
}

[HarmonyPatch(typeof(FreshItemDrop), "OnDestroy")]
internal static class DropDestroyProbe {
    static void Prefix(FreshItemDrop __instance) {
        if (DropChipRegression.Active) log.l($"[drop-debug] OnDestroy component={__instance.GetInstanceID()} timeLeft={__instance.timeLeft} forceOn={__instance.forceOn} parent={__instance.transform.parent?.name}");
    }
}

[HarmonyPatch(typeof(FreshItemDrop), "Start")]
internal static class DropStartProbe {
    static void Postfix(FreshItemDrop __instance) {
        if (!DropChipRegression.Active || !Net.IsServer) return;
        var nearest = ScavPlayer.GetDistanceToNearestLivingPlayer(__instance.transform.position);
        log.l($"[drop-debug] Start component={__instance.GetInstanceID()} nearest={Mathf.Sqrt(nearest.Item2)} nearestPos={nearest.Item1?.body?.transform.position} itemPos={__instance.transform.position} parent={__instance.transform.parent?.name} setting={Item.itemFloating}");
    }
}
#endif
