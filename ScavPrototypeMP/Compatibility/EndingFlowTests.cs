#if FULLGAME && TESTING
using CasualtiesTogetherUtils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Together;

// Opt-in integration fixture. No shipping commands or production cheats.
// Uses the real server handlers/transport; assertions also run on the client.
internal static class EndingFlowTests {
    static string Scenario => Environment.GetEnvironmentVariable("CT_ENDING_TEST_SCENARIO") ?? "cargo";
    static bool started;
    static bool startingWorld;
    static bool preferencesCleared;
    static bool quitRequested;
    // Scene preparation must leave the story under the player's control.
    internal static bool AutoNarrative => EndingTestPreferences.Enabled &&
        Environment.GetCommandLineArgs().Contains("--mp-auto-narrative");
    static float lobbyReadyAt;
    internal static void Tick() {
        if (!Environment.GetCommandLineArgs().Contains("--mp-ending-test")) return;
        // Isolated, user-driven sessions keep the fixture save/preferences but
        // must never start a run, change layers or execute scripted assertions.
        if (Scenario == "manual-structure") { SteamManager.disableAchievements = true; return; }
        if (Scenario == "port551") { Port551Smoke.Tick(); return; }
        Release14EndingTests.Tick();
        Release18EndingTests.Tick();
        if (DropChipRegression.Active) DropSourceRegression.Tick();
        SteamManager.disableAchievements = true; // fixtures must not grant platform achievements
        if (!preferencesCleared) {
            preferencesCleared = true;
            foreach (string key in new[] { "wonchar0", "wonchar1", "didbiopod", "didescape", "didmerc" }) PlayerPrefs.SetInt(key, 0);
        }
        if (Scenario.StartsWith("quit-")) {
            string side = Net.IsServer ? "host" : "client";
            string folder = Path.GetDirectoryName(Environment.GetEnvironmentVariable("CT_ENDING_TEST_SAVE"));
            if (!quitRequested && File.Exists(Path.Combine(folder, side + ".quit"))) {
                quitRequested = true;
                log.l("[quit-test] invoking exit side=" + side + " inWorld=" + Util.IsInWorld());
                if (Util.IsInWorld()) MultiplayerQuit.Request();
                else {
                    var button = UnityEngine.Object.FindObjectsOfType<AdaptiveButton>(true).First(b => b.action == AdaptiveButton.MenuAction.Quit);
                    HarmonyLib.AccessTools.Method(typeof(AdaptiveButton), "Clicked").Invoke(button, null);
                }
            }
        }
        if (started) return;
        if (Scenario == "quit-lobby") {
            if (Net.IsClient && CharacterLobby.ShouldShowPanel) CharacterLobby.ConfirmPanel();
            return;
        }
        if (Net.IsRunning && !Util.IsInWorld()) {
            if (Net.IsClient && CharacterLobby.ShouldShowPanel) {
                if (DropChipRegression.Active) DropChipRegression.TickPanel();
                else CharacterLobby.ConfirmPanel();
            }
            if (Net.IsServer && !startingWorld && !CharacterReadyGate.Blocked &&
                ScavPlayer.ClientIdToPlayerDict.Count >= (DropChipRegression.Reloading ? 1 : 2) && PreRunScript.instance != null) {
                if (Scenario.StartsWith("r14-") || Scenario.StartsWith("r18-") || Scenario.StartsWith("r19-") || Scenario.StartsWith("escape")) {
                    if (lobbyReadyAt == 0f) lobbyReadyAt = Time.unscaledTime + 3f;
                    if (Time.unscaledTime < lobbyReadyAt) return;
                }
                startingWorld = true;
                if (DropChipRegression.Reloading) ChipState.Choose(true); // conflicting lobby draft must not overwrite saved host=false
                else if (Scenario.Contains("host-unchipped")) ChipState.Choose(true);
                if (Scenario == "reload" || DropChipRegression.Reloading) PreRunScript.instance.LoadRun(); else PreRunScript.instance.StartRun();
            }
        }
        if (!Net.IsRunning || !Util.IsWorldGenerated() || ScavPlayer.LocalPlayer?.body == null) return;
        started = true;
        if (Scenario.StartsWith("r18-")) { Util.StartCoroutine(Release18EndingTests.Run()); return; }
        if (Scenario.StartsWith("r14-") || Scenario.StartsWith("r19-") || Scenario.StartsWith("escape")) { Util.StartCoroutine(Release14EndingTests.Run()); return; }
        if (DropChipRegression.Active) { Util.StartCoroutine(DropChipRegression.Run()); return; }
        Util.StartCoroutine(Scenario == "reload" ? Reload() : Net.IsServer ? Server() : Client());
    }
    static void Check(bool value, string test) {
        if (!value) throw new Exception("[ending-test] FAIL " + test);
        log.l("[ending-test] PASS " + test);
    }
    static IEnumerator Reload() {
        yield return new WaitUntil(() => !DeferredPlayerSaveRestore.InProgress && EndingFlow.IsFinished);
        yield return new WaitForSecondsRealtime(3);
        Check(EndingFlow.State.completed && EndingFlow.State.ending == 1, "reload retained completion marker");
        Check(PlayerCamera.main.endScreen.gameObject.activeSelf, "reload forces settlement instead of exploration");
        UIInGame.StartSpectatorMode();
        Check(!UIInGame.SpectatorModeActive && PlayerCamera.main.endScreen.gameObject.activeSelf, "result blocks spectator hiding settlement");
        Check(ScavPlayer.ClientIdToPlayerDict.Values.Count(p => p?.body != null && p.body.hasCargo) == 1, "reload retained unique cargo");
        if (Net.IsClient) {
            PlayerCamera.main.ToMainMenu();
            yield return new WaitForSecondsRealtime(1);
            Check(Util.IsInWorld() && Net.IsConnected, "client cannot leave settlement independently");
        } else {
            yield return new WaitForSecondsRealtime(10);
            PlayerCamera.main.ToMainMenu();
        }
        yield return new WaitUntil(() => Util.IsInMainMenu());
        Check(Net.IsRunning && Net.IsConnected, "settlement returns to lobby without disconnect");
        if (Net.IsServer) Check(File.Exists(Path.Combine(SavesystemPatch.mpsavefolder, "save.sv")), "return retained mp_save");
        log.l("[ending-test] reload fixture complete side=" + (Net.IsServer ? "host" : "client"));
    }
    static IEnumerator Client() {
        if (Scenario == "quit-world") { log.l("[quit-test] world ready side=client"); yield break; }
        if (Scenario.StartsWith("full-route")) { yield return EndingRouteRegression.Client(); yield break; }
        var heartFixture = UnityEngine.Object.Instantiate(Resources.Load<GameObject>("Structures/BlueHeartCont"),
            ScavPlayer.LocalPlayer.pos + Vector2.right * 150f, Quaternion.identity);
        Check(BlueHeart.main != null, "client has a blue heart before ownership sync");
        yield return new WaitUntil(() => EndingFlow.State.hasCargo);
        yield return new WaitForSecondsRealtime(2);
        Check(ScavPlayer.ClientIdToPlayerDict.Values.Count(p => p?.body != null && p.body.hasCargo) == 1, "client cargo unique");
        Check(BlueHeart.main == null, "client heart removed");
        Check(!string.IsNullOrEmpty(EndingFlow.State.cargoOwner), "client owner replicated");
        UnityEngine.Object.Destroy(heartFixture);
        if (Scenario == "merc") {
            yield return new WaitUntil(() => EndingFlow.IsFinished);
            yield return new WaitForSecondsRealtime(2);
            Check(EndingFlow.State.ending == 3 && PlayerCamera.main.endScreen.gameObject.activeSelf, "client forced Merc settlement");
            Check(PlayerPrefs.GetInt("didmerc") == 1 && PlayerPrefs.GetInt("wonchar1") == 1, "client Merc metaprogress");
        }
        if (Scenario == "travel") {
            yield return new WaitUntil(() => WorldGeneration.world.biomeDepth == 11);
            Check(WorldGeneration.world.biomeDepth == 11, "client followed layer10 breach into lab");
            yield return new WaitUntil(() => EndingFlow.IsFinished);
            yield return new WaitForSecondsRealtime(2);
            Check(PlayerCamera.main.endScreen.gameObject.activeSelf, "client forced settlement visible");
            Check(EndingFlow.State.ending == 1, "client received expected ending type");
            Check(PlayerPrefs.GetInt("wonchar1") == 1 && PlayerPrefs.GetInt("didbiopod") == 1, "client local metaprogress");
        }
    }
    static IEnumerator Server() {
        if (Scenario == "quit-world") { log.l("[quit-test] world ready side=host"); yield break; }
        if (Scenario.StartsWith("full-route")) { yield return EndingRouteRegression.Server(); yield break; }
        yield return new WaitUntil(() => ScavPlayer.ClientIdToPlayerDict.Values.Count(p => p?.body != null && p.IsAlive()) >= 2);
        yield return new WaitForSecondsRealtime(5);
        var world = WorldGeneration.world;
        int depth = world.biomeDepth;
        // Isolated interaction fixture on a generated world. Full terrain/cutscene
        // playthroughs remain separate from this handler/network check.
        world.biomeDepth = 9;
        var prefab = Resources.Load<GameObject>("Structures/BlueHeartCont");
        var fixture = UnityEngine.Object.Instantiate(prefab, ScavPlayer.LocalPlayer.pos + Vector2.right * 100f, Quaternion.identity);
        var heart = fixture.GetComponentInChildren<BlueHeart>();
        Check(heart != null, "heart fixture exists");
        heart.transform.position = ScavPlayer.LocalPlayer.pos;
        var host = ScavPlayer.LocalPlayer;
        host.body.standing = true;
        EndingFlow.Request(EndingRequest.Heart, heart.transform.position);
        yield return new WaitForSecondsRealtime(6);
        Check(EndingFlow.State.hasCargo && EndingFlow.KeyPlayer == host, "server first heart request wins");
        Check(ScavPlayer.ClientIdToPlayerDict.Values.Count(p => p?.body != null && p.body.hasCargo) == 1, "server cargo unique");
        Check(BlueHeart.main == null, "server heart removed");
        EndingFlow.Request(EndingRequest.Heart, host.pos);
        Check(EndingFlow.KeyPlayer == host, "repeat request does not steal cargo");
        PlayerCheckpoint.WriteOne(host, false);
        Check(File.Exists(Path.Combine(SavesystemPatch.mpsavefolder, "ending_flow.json")), "cargo persisted");
        host.body.brainHealth = 0f;
        yield return new WaitForSecondsRealtime(2);
        Check(EndingFlow.KeyPlayer != host && EndingFlow.State.hasCargo, "dead carrier transfers cargo");
        if (Scenario == "disconnect") {
            var disconnectedId = EndingFlow.KeyPlayer.playerId;
            host.body.brainHealth = 100f; host.body.Heal();
            Net.Server_Kick(disconnectedId, "ending-test disconnect fixture");
            yield return new WaitForSecondsRealtime(3);
            Check(EndingFlow.KeyPlayer == host && EndingFlow.State.hasCargo, "disconnect transfers cargo to living teammate");
            host.body.brainHealth = 0f; EndingFlow.Tick();
            Check(EndingFlow.State.phase == EndingPhase.Failed && EndingFlow.State.ending == 0 && !EndingFlow.State.completed, "team wipe awards no ending");
            log.l("[ending-test] disconnect fixture complete"); yield break;
        }
        if (Scenario == "merc") {
            var remote = EndingFlow.KeyPlayer;
            host.body.brainHealth = 100f; host.body.Heal(); host.body.consciousness = 100f;
            EndingFlow.State.cargoOwner = host.GetPersistentId();
            var scarf = Utils.Create("mercscarf", remote.pos, 0f).GetComponent<Item>();
            remote.body.PickUpItem(scarf, 0, true);
            Check(remote.body.FindByIdThorough("mercscarf", out _), "scarf held by non-key teammate");
            PlayerPrefs.SetInt("didbiopod", 0); PlayerPrefs.SetInt("didescape", 0);
            Check(!EndingFlow.TestTryMerc(), "Merc checks host prerequisite");
            PlayerPrefs.SetInt("didbiopod", 1);
            world.biomeDepth = 10;
            host.body.transform.position = new Vector2(0, world.halfHeight - 4f);
            EndingFlow.Tick();
            yield return new WaitUntil(() => EndingFlow.IsFinished);
            yield return new WaitForSecondsRealtime(2);
            Check(EndingFlow.IsFinished && EndingFlow.State.ending == 3, "key breach plus remote scarf gives Merc");
            Check(PlayerPrefs.GetInt("didmerc") == 1 && PlayerPrefs.GetInt("wonchar0") == 1, "host Merc metaprogress");
            log.l("[ending-test] merc fixture complete");
            yield break;
        }
        if (Scenario == "travel") {
            host.body.brainHealth = 100f; host.body.Heal(); host.body.consciousness = 100f;
            var pod = fixture.GetComponentInChildren<CargoDrillPod>();
            Check(pod != null, "drill fixture exists");
            host.body.transform.position = pod.transform.position;
            EndingFlow.Request(EndingRequest.Drill, pod.transform.position);
            Check(!world.doingRegen, "drill waits for cargo carrier inside");
            EndingFlow.State.cargoOwner = host.GetPersistentId();
            EndingFlow.Request(EndingRequest.Drill, pod.transform.position);
            Check(world.doingRegen, "drill host-authoritative departure");
            yield return new WaitUntil(() => !world.generatingWorld && !world.doingRegen && Util.IsWorldGenerated());
            Check(world.biomeDepth == 10, "drill arrived layer10");
            yield return new WaitForSecondsRealtime(3);
            host.body.transform.position = new Vector2(0, world.halfHeight - 4f);
            EndingFlow.Tick();
            Check(EndingFlow.State.phase == EndingPhase.Breach, "key player starts global breach");
            yield return new WaitUntil(() => world.biomeDepth == 11 && !world.generatingWorld && Util.IsWorldGenerated());
            yield return new WaitForSecondsRealtime(3);
            Check(EndingFlow.State.phase == EndingPhase.Laboratory, "breach enters laboratory");
            host.body.transform.position = EndLabScript.instance.exitPoint;
            EndingFlow.Tick();
            yield return new WaitForSecondsRealtime(2);
            Check(EndingFlow.IsFinished && EndingFlow.State.ending == 1, "key exit triggers biopod party result");
            Check(PlayerCamera.main.endScreen.gameObject.activeSelf, "host forced settlement visible");
            Check(PlayerPrefs.GetInt("wonchar0") == 1 && PlayerPrefs.GetInt("didbiopod") == 1, "host local metaprogress");
            log.l("[ending-test] travel/result fixture complete");
            yield break;
        }
        world.biomeDepth = depth;
        UnityEngine.Object.Destroy(fixture);
        log.l("[ending-test] cargo fixture complete; terminate test instances without saving");
    }
}
#endif
