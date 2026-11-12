#if FULLGAME && TESTING
using CasualtiesTogetherUtils;
using LiteNetLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using Object = UnityEngine.Object;
using static GunScript;

namespace Together;

// Two real game instances, generated lab, production RPCs and vanilla drag
// physics. Fixture placement/health changes are explicit, never shipping cheats.
internal static class Release14EndingTests {
    static string Scenario => (Environment.GetEnvironmentVariable("CT_ENDING_TEST_SCENARIO") ?? "") switch {
        "escape-success" => "r14-escape", "escape-killed" => "r14-coredeath",
        "escape-down" => "r14-capture", "escape-timeout" => "r14-timeout",
        "r14-god-assist" => "r14-remote",
        "r19-noncore-host" or "r19-core-client" => "r14-remote",
        "r19-noncore-client" or "r19-core-host" => "r14-escape",
        "r19-merc" => "r14-merc",
        var scenario => scenario
    };
    static string Folder => Path.GetDirectoryName(Environment.GetEnvironmentVariable("CT_ENDING_TEST_SAVE"));
    static string FileFor(string name) => Path.Combine(Folder, name);
    static void Mark(string name) => File.WriteAllText(FileFor(name), "PASS");
    static void Check(bool pass, string message) {
        if (!pass) throw new Exception("[r14-test] FAIL " + message);
        log.l("[r14-test] PASS " + message);
    }
    static IEnumerator Wait(Func<bool> predicate, string reason, float seconds = 100f) {
        float until = Time.realtimeSinceStartup + seconds;
        while (!predicate()) {
            if (Time.realtimeSinceStartup > until) throw new Exception("[r14-test] FAIL timeout " + reason);
            yield return null;
        }
    }
    static Vector2? hold;
    static float skipAt;
    static string lastStep;
    static bool faded;
    static bool roomObserved;
    internal static void Tick() {
        if (!Scenario.StartsWith("r14-") || !Util.IsInWorld()) return;
        var local = ScavPlayer.LocalPlayer;
        // Release placement before this frame can overwrite the production
        // teleport. The fixture must observe the received pose, not impose it.
        if (EndingFlow.State.phase == EndingPhase.Surgery && !roomObserved) { roomObserved = true; hold = null; }
        if (hold.HasValue && local?.body != null && !EndingFlow.SettlementLocked) {
            local.netBody.SetBodyPosition(hold.Value); local.body.SetVelocity(Vector2.zero);
        }
        var lab = EndLabScript.instance;
        if (lab != null && EndingFlow.State.phase == EndingPhase.Laboratory && Time.unscaledTime > skipAt) {
            skipAt = Time.unscaledTime + .25f; lab.scientistTalker.Skip(); local?.body?.talker.Skip();
        }
        if (EndingFlow.State.phase == EndingPhase.Narrative) {
            string step = EndingFlow.NarrativeStep;
            if (step == "initial-fade-start" && GlobalDark.main.image.color.a > .15f && GlobalDark.main.image.color.a < .95f) faded = true;
            if (step != lastStep) {
                lastStep = step;
                if (!string.IsNullOrEmpty(step)) {
                    Check(!PlayerCamera.main.endScreen.gameObject.activeSelf, "narrative is not covered by an earlier death result");
                    if (step.Contains("note")) Check(ScrollableText.textExists || step.Contains("fade"), "local narrative stage " + step);
                    ScreenCapture.CaptureScreenshot(FileFor((Net.IsServer ? "host-" : "client-") + step + ".png"));
                    Util.StartCoroutine(CaptureLater(step));
                }
            }
        }
    }
    static IEnumerator CaptureLater(string step) {
        yield return new WaitForSecondsRealtime(.8f);
        if (EndingFlow.NarrativeStep == step) ScreenCapture.CaptureScreenshot(FileFor((Net.IsServer ? "host-" : "client-") + step + "-visible.png"));
    }
    static void Place(ScavPlayer player, Vector2 position) {
        player.body.Heal(); player.body.ForceStand(); player.Server_TeleportCharacter(position);
        player.body.forceNoMovement = true; player.body.SetVelocity(Vector2.zero);
        if (player.IsLocal) hold = position;
    }
    internal static IEnumerator Run() {
        Release19EndingTests.Prepare();
        yield return Wait(() => ScavPlayer.ClientIdToPlayerDict.Values.Count(p => p?.body != null) == 2 && !DeferredPlayerSaveRestore.InProgress, "two bodies");
        yield return Wait(() => !WorldGeneration.world.generatingWorld && !WorldGeneration.world.doingRegen && Util.IsWorldGenerated(), "local startup world complete");
        if (Net.IsClient) Mark("startup.client");
        else yield return Wait(() => File.Exists(FileFor("startup.client")) && ScavPlayer.ClientIdToPlayerDict.Values.Where(p => !p.IsHost).All(p => p.ServerPlayerState.finished_worldgen), "client startup generation acknowledged");
        yield return new WaitForSecondsRealtime(3);
        if (Net.IsServer) yield return Server(); else yield return Client();
    }
    static IEnumerator Server() {
        var world = WorldGeneration.world;
        var host = ScavPlayer.LocalPlayer;
        var guest = ScavPlayer.ClientIdToPlayerDict.Values.First(p => !p.IsHost);
        if (Scenario == "r14-merc") {
            PlayerPrefs.SetInt("didbiopod", 1);
            EndingFlow.State = new EndingSaveData { phase = EndingPhase.Cargo, cargoTaken = true, hasCargo = true, cargoOwner = host.GetPersistentId() };
            guest.body.PickUpItem(Utils.Create("mercscarf", guest.pos, 0f).GetComponent<Item>(), 0, true);
            Check(guest.body.FindByIdThorough("mercscarf", out _), "Merc non-core teammate holds scarf");
            world.biomeDepth = 10; Place(host, new Vector2(0, world.halfHeight - 4));
            yield return Wait(() => EndingFlow.State.phase == EndingPhase.Narrative, "Merc begins narrative");
            Check(!EndingFlow.IsFinished, "Merc does not skip directly to result");
            yield return Settlement(3); yield break;
        }
        bool remoteCarrier = Scenario == "r14-remote";
        var carrier = remoteCarrier ? guest : host;
        world.biomeDepth = 10;
        EndingFlow.State = new EndingSaveData { phase = EndingPhase.Breach, cargoTaken = true, hasCargo = true, cargoOwner = carrier.GetPersistentId() };
        yield return world.RegenerateWorld();
        yield return Wait(() => world.biomeDepth == 11 && !world.doingRegen && Util.IsWorldGenerated() && EndLabScript.instance != null, "generated laboratory");
        yield return new WaitForSecondsRealtime(3);
        var lab = EndLabScript.instance;
        Place(host, new Vector2(12, 234)); Place(guest, new Vector2(13, 234));
        float vanillaSurgerySeconds = lab.timeLeft;
        Mark("lab.ready");
        yield return Wait(() => File.Exists(FileFor("lab.client")), "client generated the laboratory");
        yield return Wait(() => EndingFlow.State.labDialogueComplete, "vanilla scientist main dialogue", 100f);
        Check(EndingFlow.State.labEntered && EndingFlow.State.labDialogueComplete, "scientist dialogue completed through production coroutine");
        // Reject a chipped carrier even with an unchipped host. Remote carrier
        // then proves a chipped host does not veto the final accepted route.
        ChipState.ApplyServer(carrier, false, false);
        TurretScript_Shoot_MultiplayerPatch.LastActiveShooter = guest.netBody;
        EndingFlow.ScientistShot();
        TurretScript_Shoot_MultiplayerPatch.LastActiveShooter = null;
        Check(EndingFlow.State.phase == EndingPhase.Laboratory, "chipped cargo carrier rejected");
        if (Scenario != "r14-chipped") ChipState.ApplyServer(carrier, true, false);
        ChipState.ApplyServer(remoteCarrier ? host : guest, false, false);
        Vector2 shotOrigin = (Vector2)lab.scientistAnim.transform.position + Vector2.left * 3f;
        Place(guest, shotOrigin + Vector2.down);
        var pistol = Utils.Create("pistol", shotOrigin, 0f);
        guest.body.PickUpItem(pistol.GetComponent<Item>(), 0, true);
        var gun = pistol.GetComponent<GunScript>(); gun.roundInChamber = RoundInChamber.Round; gun.roundsInMag = 8; gun.racked = true;
        var si = NetObjectRegistry.Server_EnsureItemIsNetworkRegistered(pistol);
        File.WriteAllText(FileFor("gun.id"), si.syncId.id.ToString()); Mark("shot.ready");
        if (Scenario == "r14-chipped") {
            yield return Wait(() => File.Exists(FileFor("shot.sent")), "client chipped shot sent");
            yield return new WaitForSecondsRealtime(2);
            Check(EndingFlow.State.phase == EndingPhase.Laboratory && !lab.doingEscape, "chipped carrier actual gun RPC rejected without escape visuals");
            Mark("chip.rejected");
            yield return Wait(() => File.Exists(FileFor("chip.client")), "client observed chipped rejection");
            hold = lab.exitPoint; Place(carrier, lab.exitPoint);
            yield return Settlement(1); yield break;
        }
        yield return Wait(() => EndingFlow.DuringEscape, "authenticated client shot Scientists collider");
        Check(ChipState.IsUnchipped(carrier), "T1 carrier chip eligibility used");
        Check(remoteCarrier || guest.IsAlive(), "T1 chipped non-core shooter not killed");
        if (remoteCarrier) Check(!ChipState.HostIsUnchipped, "chipped host permits unchipped remote cargo carrier");
        else Check(EndingFlow.KeyPlayer != guest, "T1 non-core client gun RPC starts escape");
        Mark("escape.ready");
        yield return Wait(() => File.Exists(FileFor("escape.client")), "client escape observed");
        Place(carrier, new Vector2(250, 234)); Mark("entrance.ready");
        if (carrier.IsLocal) hold = new Vector2(250, 234);
        yield return Wait(() => EndingFlow.State.phase == EndingPhase.Surgery, "entrance room commit");
        hold = null;
        Check(EndingFlow.State.secondsLeft > vanillaSurgerySeconds - 2f && EndingFlow.State.secondsLeft <= vanillaSurgerySeconds,
            $"T2 authoritative vanilla {vanillaSurgerySeconds}s starts at room teleport");
        yield return Wait(() => File.Exists(FileFor("surgery.client")), "client room timer checked");
        yield return Wait(() => ScavPlayer.ClientIdToPlayerDict.Values.All(p => Vector2.Distance(p.pos, new Vector2(287, 243)) < 3f), "all teleport poses converge", 5f);
        Check(ScavPlayer.ClientIdToPlayerDict.Values.All(p => Vector2.Distance(p.pos, new Vector2(287, 243)) < 3f), "T2 all players teleported into room");
        hold = host.pos;
        Mark("surgery.ready");
        if (Scenario == "r14-coredeath" || Scenario == "r14-capture") {
            if (Scenario == "r14-capture") {
                carrier.body.Ragdoll(); EndingFlow.GuardKnockedDownCarrier(); EndingFlow.State.downSeconds = 99f;
                yield return new WaitForSecondsRealtime(2);
                Check(!EndingFlow.SettlementLocked, "T5 guard knockdown is injury, no invented capture deadline");
            }
            if (Scenario == "r14-capture") {
                carrier.body.bloodPressure = 0f; carrier.body.consciousness = 0f;
                Check(carrier.body.brainDying, "T5 original brainDying threshold reached");
                EndingFlow.Tick();
            } else carrier.body.brainHealth = 0f;
            yield return Settlement(1); yield break;
        }
        if (Scenario == "r14-timeout") {
            // Position at a valid vanilla spawn distance; no direct CreateGuard.
            float d = (lab.endGuardSpawner.minDistanceToSpawn + lab.endGuardSpawner.maxDistanceToSpawn) * .5f;
            hold = (Vector2)lab.endGuardSpawner.transform.position + Vector2.right * d;
            Place(carrier, hold.Value); EndingFlow.State.secondsLeft = .1f;
            yield return Wait(() => EndingFlow.State.timerExpired, "vanilla timeout");
            Check(!EndingFlow.SettlementLocked, "timeout releases guards instead of awarding Biopod");
            yield return Wait(() => EndingFlow.State.guardsSpawned == 5, "five server guards", 50f);
            Mark("guards.ready");
            yield return Wait(() => File.Exists(FileFor("guards.client")), "client replicated guards");
            carrier.body.brainHealth = 0f; yield return Settlement(1); yield break;
        }
        if (Scenario == "r14-noncoredeath") {
            guest.body.brainHealth = 0f; Mark("death.ready");
            yield return Wait(() => File.Exists(FileFor("death.client")), "non-core death seen");
            Check(EndingFlow.State.phase == EndingPhase.Surgery && EndingFlow.State.hasCargo, "T6 non-core death preserves surgery phase/cargo");
            hold = lab.surgicalDevice.transform.position; Place(host, hold.Value);
            yield return InjectThroughWoundView(host.body);
            Check(EndingFlow.State.surgeryReady, "core self injection immediately ready in same frame");
            EndLabScript.instance.surgicalDevice.GetComponent<SurgicalDeviceBehavior>().OnUse();
            yield return Wait(() => EndingFlow.LocalSurgeon && MinigameBase.main.currentMinigame is CargoMinigame, "surviving core opens surgery");
            yield return Operate();
            yield return Wait(() => EndingFlow.State.phase == EndingPhase.Escaping, "surgery continues while non-core remains dead");
            Check(!guest.IsAlive() && !EndingFlow.State.hasCargo, "T6 non-core remains dead through cargo removal");
            hold = new Vector2(367f, 243f);
            yield return new WaitForSecondsRealtime(1);
            Check(EndingFlow.State.phase == EndingPhase.Escaping && EndingFlow.State.exitFalling, "solo trapdoor does not finish ending");
            hold = new Vector2(367f, 271f);
            yield return Settlement(2); yield break;
        }
        hold = (Vector2)lab.surgicalDevice.transform.position + Vector2.left * 3f;
        if (Environment.GetEnvironmentVariable("CT_ENDING_TEST_SCENARIO") == "r14-god-assist") {
            Multiplayer.rules.sv_cheats = true;
            Con.RunCommand("godmode 1"); Check(Con._DEV_GODMODE && Con.CanCheat(), "godmode active during helper injection");
        }
        Place(host, hold.Value);
        Place(guest, (Vector2)lab.surgicalDevice.transform.position);
        yield return Wait(() => File.Exists(FileFor("operator.client")) && Vector2.Distance(guest.pos, lab.surgicalDevice.transform.position) < 2f, "client owns its real operator pose");
        EndingFlow.Request(EndingRequest.Surgery, lab.surgicalDevice.transform.position);
        Check(string.IsNullOrEmpty(EndingFlow.State.surgeon), "T3 surgery denied before torso anesthetic");
        if (Scenario == "r14-escape") {
            Mark("inject.remote");
            yield return Wait(() => EndingFlow.State.surgeryReady, "client non-core injection immediately enables server", 5f);
        } else {
            yield return InjectThroughWoundView(carrier.body);
            Check(EndingFlow.State.surgeryReady, "12ml injection immediately ready in same frame");
        }
        Mark("assist.ready");
        if (Scenario == "r14-escape" || Scenario == "r14-remote") {
            lab.surgicalDevice.GetComponent<SurgicalDeviceBehavior>().OnUse();
            yield return Wait(() => EndingFlow.LocalSurgeon && MinigameBase.main.currentMinigame is CargoMinigame, "host simultaneous panel");
            yield return Operate(true);
        }
        yield return Wait(() => EndingFlow.State.phase == EndingPhase.Escaping, "client completes all seven vanilla surgery steps");
        Check(!EndingFlow.State.hasCargo && EndingFlow.State.surgeryMask == 127, "T3 authoritative cargo removed and seven milestones committed");
        Check(!lab.surgeryDoor.forceDisable && !lab.doTimer, "T3 surgery door open and timer stopped");
        Mark("exit.ready");
        if (Release19EndingTests.Active) yield return Release19EndingTests.ExitServer(value => hold = value);
        yield return Settlement(2);
    }
    static IEnumerator Client() {
        if (Scenario == "r14-merc") { yield return Settlement(3); yield break; }
        yield return Wait(() => File.Exists(FileFor("lab.ready")), "server lab ready");
        yield return Wait(() => WorldGeneration.world.biomeDepth == 11 && !WorldGeneration.world.generatingWorld && !WorldGeneration.world.doingRegen && EndLabScript.instance?.scientistAnim != null, "client laboratory scene bindings ready");
        hold = new Vector2(13, 234);
        Mark("lab.client");
        yield return Wait(() => File.Exists(FileFor("shot.ready")), "server prepared gun");
        hold = (Vector2)EndLabScript.instance.scientistAnim.transform.position + new Vector2(-3f, -1f);
        yield return new WaitForSecondsRealtime(1);
        knetid id = (knetid)ushort.Parse(File.ReadAllText(FileFor("gun.id")));
        yield return Wait(() => NetObjectRegistry.TryGetSyncInfo(id, out var item) && item.go != null, "client registered pistol");
        // Use the production gun message. Server authenticates sender, reach,
        // ammunition and origin, then raycasts the genuine Scientists collider.
        var writer = Net.CreateWriter(NetmsgId.SERVER_PlayerBodyShoot);
        writer.Put(Net.ServerTick); writer.Put(id);
        writer.Put((Vector2)EndLabScript.instance.scientistAnim.transform.position + Vector2.left * 3f);
        writer.Put(Vector2.right); writer.Put(true); writer.Put((byte)0); writer.Put((byte)0);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        Mark("shot.sent");
        if (Scenario == "r14-chipped") {
            yield return Wait(() => File.Exists(FileFor("chip.rejected")), "server rejected chipped shot");
            Check(EndingFlow.State.phase == EndingPhase.Laboratory && !EndLabScript.instance.doingEscape, "client remains laboratory after chipped shot");
            Mark("chip.client"); yield return Settlement(1); yield break;
        }
        yield return Wait(() => EndingFlow.DuringEscape, "T1 client observed gun-triggered escape");
        Check(ScavPlayer.LocalPlayer.IsAlive(), "T1 chip-bearing teammates not executed"); Mark("escape.client");
        yield return Wait(() => File.Exists(FileFor("entrance.ready")), "carrier entering");
        if (Scenario == "r14-remote") hold = new Vector2(250, 234);
        yield return Wait(() => EndingFlow.State.phase == EndingPhase.Surgery, "T2 client Surgery state");
        hold = null;
        yield return Wait(() => Vector2.Distance(ScavPlayer.LocalPlayer.pos, new Vector2(287, 243)) < 2f, "client natural teleport pose", 5f);
        Check(EndingFlow.State.secondsLeft > 57, "T2 client room teleport and shared countdown");
        hold = ScavPlayer.LocalPlayer.pos;
        Check(EndLabScript.instance.timerUi.color.a <= .5f, "vanilla countdown transparency");
        Check(EndLabScript.instance.timerUi.enabled, "T2 client countdown visible"); Mark("surgery.client");
        if (Scenario == "r14-escape" || Scenario == "r14-remote") {
            yield return Wait(() => File.Exists(FileFor("surgery.ready")), "server operator placement");
            hold = EndLabScript.instance.surgicalDevice.transform.position;
            yield return new WaitForSecondsRealtime(.25f);
            Mark("operator.client");
        }
        if (Scenario == "r14-coredeath" || Scenario == "r14-capture") { yield return Settlement(1); yield break; }
        if (Scenario == "r14-timeout") {
            yield return Wait(() => File.Exists(FileFor("guards.ready")), "server five guards");
            yield return new WaitForSecondsRealtime(2);
            Check(Object.FindObjectsOfType<GuardScript>().Count(g => g.myBody != null && g.myBody.enabled) >= 5, "client five synchronized visible guards");
            Mark("guards.client"); yield return Settlement(1); yield break;
        }
        if (Scenario == "r14-noncoredeath") {
            yield return Wait(() => File.Exists(FileFor("death.ready")), "server non-core kill");
            yield return new WaitForSecondsRealtime(2);
            Check(EndingFlow.State.phase == EndingPhase.Surgery, "T6 client non-core death does not finish route"); Mark("death.client");
            yield return Settlement(2);
            Check(!ScavPlayer.LocalPlayer.IsAlive(), "T6 dead non-core sees Escape narrative and settlement");
            yield break;
        }
        if (Scenario == "r14-escape") {
            yield return Wait(() => File.Exists(FileFor("inject.remote")), "remote injection fixture");
            hold = EndLabScript.instance.surgicalDevice.transform.position;
            yield return new WaitForSecondsRealtime(.5f);
            yield return InjectThroughWoundView(EndingFlow.KeyPlayer.body);
            yield return Wait(() => EndingFlow.State.surgeryReady, "client injection ready within network latency", 3f);
        }
        yield return Wait(() => File.Exists(FileFor("assist.ready")), "anesthesia ready");
        hold = EndLabScript.instance.surgicalDevice.transform.position;
        yield return new WaitForSecondsRealtime(1);
        Check(EndLabScript.instance.canStartSurgery, "client lab.canStartSurgery follows authoritative readiness");
        EndLabScript.instance.surgicalDevice.GetComponent<SurgicalDeviceBehavior>().OnUse();
        yield return Wait(() => EndingFlow.LocalSurgeon && MinigameBase.main.currentMinigame is CargoMinigame, "remote surgical OnUse opened minigame");
        yield return Operate(Scenario == "r14-escape" || Scenario == "r14-remote");
        yield return Wait(() => EndingFlow.State.phase == EndingPhase.Escaping, "T3 client surgery commit");
        Check(!EndingFlow.State.hasCargo, "T3 client cargo removal replicated");
        yield return Wait(() => File.Exists(FileFor("exit.ready")), "gate ready");
        if (Release19EndingTests.Active) {
            yield return Release19EndingTests.ExitClient(value => hold = value);
            yield return Settlement(2); yield break;
        }
        hold = new Vector2(354f, 243f);
        yield return new WaitForSecondsRealtime(1);
        Check(!string.IsNullOrEmpty(EndingFlow.State.exitWalker), "T4 any teammate x>353 starts exit walk");
        hold = new Vector2(367f, 243f);
        yield return new WaitForSecondsRealtime(1);
        Check(EndingFlow.State.phase == EndingPhase.Escaping && EndingFlow.State.exitFalling, "trapdoor fall waits for true exit");
        hold = new Vector2(367f, 271f);
        yield return Settlement(2);
    }
    static IEnumerator InjectThroughWoundView(Body patient) {
        var item = EndLabScript.instance.injector.GetComponent<Item>();
        log.l($"[anesth] fixture wound-view helper={ScavPlayer.LocalPlayer.body.name} patient={patient.name} item={item.id} contents={EndLabScript.instance.injector.CurrentTotal}");
        PlayerCamera.main.selectedLimb = patient.limbs[2];
        PlayerCamera.main.ApplyWoundItem(item);
        yield return Wait(() => MinigameBase.main.currentMinigame is SyringeMinigame, "wound-view opened native syringe");
        var syringe = (SyringeMinigame)MinigameBase.main.currentMinigame;
        // Vanilla Update invokes OnUse(depth * deltaTime). Reproduce incremental
        // injection, including its production server syringe RPC, never Inject(12).
        for (int i = 0; i < 60; i++) {
            syringe.OnUse(.0025f); // lab's saline syringe action injects 80 * mult = 0.2ml
            SyringeMinigame_Update_MultiplayerPatch.Client_SendCurrentInjectedAmount(false);
            yield return new WaitForSecondsRealtime(.01f);
            if (i == 39) {
                yield return new WaitForSecondsRealtime(.2f);
                Check(!EndingFlow.State.surgeryReady, "8ml incremental dose remains below vanilla threshold");
            }
        }
        if (Net.IsServer) Check(EndingFlow.State.surgeryReady, "incremental torso dose commits immediately on authoritative server");
        ScreenCapture.CaptureScreenshot(FileFor((Net.IsServer ? "host" : "client") + "-injection.png"));
        MinigameBase.main.EndMinigame();
    }
    static IEnumerator Operate(bool cooperative = false) {
        var game = (CargoMinigame)MinigameBase.main.currentMinigame;
        Check(game.limb.body == EndingFlow.KeyPlayer.body, "T3 surgeon targets cargo carrier torso");
        if (cooperative) {
            yield return Wait(() => EndingFlow.State.surgeryOperators.Count == 2, "both surgery participants admitted");
            Check(MinigameBase.main.currentMinigame == game, "teammate join preserves existing panel");
        }
        for (int i = 0; i < 4; i++) {
            if (cooperative && (i % 2 == 0) != Net.IsServer) {
                int bit = 1 << i;
                yield return Wait(() => (EndingFlow.State.surgeryMask & bit) != 0, "shared teammate clamp " + i);
                yield return new WaitForSecondsRealtime(.1f);
                Check(!game.clamps[i].GetComponent<UnityEngine.UI.Image>().raycastTarget, "teammate clamp reflected on panel");
                continue;
            }
            game.held = game.clamps[i]; game.index = i; game.heldOffset = Vector2.zero;
            MinigameBase.main.handPos = new Vector2(300f, i < 2 ? 248f : -365f);
            game.PhysicsUpdate(1f / 60f); yield return new WaitForSecondsRealtime(.6f);
        }
        yield return Wait(() => game.stage >= 1, "shared stage1");
        Check(game.stage >= 1, "T3 native clamp drag advances stage1");
        if (!cooperative || Net.IsServer) {
        game.held = game.cargo; game.heldOffset = Vector2.zero; MinigameBase.main.handPos = new Vector2(1100, 0);
        game.PhysicsUpdate(1f / 60f); game.Update(new List<RaycastResult>()); yield return new WaitForSecondsRealtime(.6f);
        }
        yield return Wait(() => game.stage == 2, "shared cargo removed stage2");
        for (int i = 0; i < 2; i++) {
            if (cooperative && (i == 0) != Net.IsServer) {
                int bit = 1 << (i + 5);
                yield return Wait(() => (EndingFlow.State.surgeryMask & bit) != 0, "shared teammate hose " + i);
                continue;
            }
            game.held = game.hoses[i]; game.index = i; game.heldOffset = Vector2.zero;
            MinigameBase.main.handPos = new Vector2(i == 0 ? 555 : -555, -63);
            game.PhysicsUpdate(1f / 60f); game.Update(new List<RaycastResult>()); yield return new WaitForSecondsRealtime(.6f);
        }
    }
    static IEnumerator Settlement(byte ending) {
        if (Release19EndingTests.Active) yield return Release19EndingTests.ReadNarrative(ending);
        yield return Wait(() => EndingFlow.IsFinished, "all-player narrative and settlement", 120f);
        yield return new WaitForSecondsRealtime(3);
        if (Scenario != "r14-chipped") Check(faded, "real GlobalDark alpha changed through intermediate values");
        Check(EndingFlow.State.ending == ending && PlayerCamera.main.endScreen.gameObject.activeSelf, "T4/T5 expected party settlement type=" + ending);
        Check(PlayerPrefs.GetInt(ending == 3 ? "didmerc" : ending == 2 ? "didescape" : "didbiopod") == 1, "local ending metaprogress saved");
        Mark(Net.IsServer ? "server.done" : "client.done");
        log.l("[r14-test] complete " + Scenario + " side=" + (Net.IsServer ? "host" : "client"));
        if (Net.IsServer) log.l("[ending-test] " + Environment.GetEnvironmentVariable("CT_ENDING_TEST_SCENARIO") + " fixture complete");
        else log.l("[ending-test] PASS client local metaprogress");
    }
}
#endif
