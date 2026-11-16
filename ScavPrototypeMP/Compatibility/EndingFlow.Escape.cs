#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using System.Collections;
using System.Linq;
using UnityEngine;

namespace Together;

internal static partial class EndingFlow {
    static readonly Vector2 SurgeryEntrance = new(250f, 234f);
    static readonly Vector2 SurgeryRoom = new(287f, 243f);
    static float doorRequestAt;
    static EndLabScript escapePresentation;
    static bool roomApplied;
    static bool entranceNotified;
    static bool entranceFadeApplied, surgeryPresentation, exitPresentation;
    static float escapeVisualElapsed;
    static float exitLogAt;
    static bool localExitFalling;
    static float escapeHintUntil;
    internal static bool DuringEscape => Active && (State.phase == EndingPhase.Escape || State.phase == EndingPhase.Surgery || State.phase == EndingPhase.Escaping);

    internal static void ScientistShot() {
        if (!Active) return;
        if (!EnableEscape) {
            if (Net.IsServer) Changed("[ending] escape ending disabled in this build; ignoring the scientist shot");
            return;
        }
        var shooter = TurretScript_Shoot_MultiplayerPatch.LastActiveShooter;
        // The existing gun protocol replays each authenticated player's shot on
        // the server. Client BeginEscapeSequence calls carry no authority.
        if (!Net.IsServer || shooter == null || shooter.plr == null || !Stable || WorldGeneration.world.biomeDepth != 11) return;
        if (State.phase != EndingPhase.Laboratory || !State.labDialogueComplete) return;
        if (!State.hasCargo || KeyPlayer == null || !KeyPlayer.IsAlive()) { Notice(shooter.plr, "ending_wait_carrier"); return; }
        bool carrierUnchipped = ChipState.IsUnchipped(KeyPlayer);
        log.l($"[escape] scientist shot shooter={Key(shooter.plr)} carrier={State.cargoOwner} carrierUnchipped={carrierUnchipped} hostUnchipped={ChipState.HostIsUnchipped}");
        if (!carrierUnchipped) { Notice(shooter.plr, "ending_escape_host_chip"); return; }
        // Capture the scene's vanilla deadline before our presentation takes over.
        // This also follows resource changes without imposing another time limit.
        State.phase = EndingPhase.Escape; State.secondsLeft = EndLabScript.instance.timeLeft;
        State.escapeElapsed = 0f; State.entranceFading = State.timerExpired = State.surgeryReady = false; escapeHintUntil = 0f;
        State.downSeconds = 0; State.guardDown = false;
        State.surgeon = ""; State.surgeryOperators.Clear(); State.exitWalker = ""; State.exitFalling = false; State.surgeryMask = State.surgeryStage = State.guardsSpawned = 0;
        Changed($"[escape] started shooter={Key(shooter.plr)} carrierUnchipped=true hostUnchipped={ChipState.HostIsUnchipped}; no teammate chip punishment");
        BroadcastEvent(EndingEvent.Escape);
    }

    static partial void HandleLabRequest(ScavPlayer p, EndingRequest action, Vector2 position, int value) {
        var lab = EndLabScript.instance;
        if (lab == null || WorldGeneration.world.biomeDepth != 11) return;
        if (action == EndingRequest.Door) {
            if (State.phase != EndingPhase.Escape || State.escapeElapsed < 5f) return;
            if (p != KeyPlayer) { Notice(p, "ending_door_carrier_only"); return; }
            if (Vector2.Distance(p.pos, SurgeryEntrance) >= 2f) { Notice(p, "ending_invalid_interaction"); return; }
            if (!State.entranceFading) {
                State.entranceFading = true; State.entranceReady.Clear();
                Changed("[escape] carrier reached entrance; party fading before room teleport");
            }
            return;
        }
        HandleSurgery(p, action, position, value);
    }
    static partial void HandleSurgery(ScavPlayer p, EndingRequest action, Vector2 position, int value);

    static partial void CargoFailedInEscape() => BeginNarrative(1, "cargo carrier brain dying/killed during escape", "biopodnotealt2");

    static partial void TickEscape() {
        var lab = EndLabScript.instance;
        if (!DuringEscape || lab == null) return;
        var key = KeyPlayer;
        if (key?.body == null) return; // cargo transfer handles disconnected carriers first
        if (!key.IsAlive() || key.body.brainDying) { CargoFailedInEscape(); return; }
        State.escapeElapsed += Time.deltaTime;
        if (State.phase == EndingPhase.Escape && State.entranceFading) {
            if (Players().All(p => State.entranceReady.Contains(Key(p)))) {
                State.phase = EndingPhase.Surgery;
                State.entranceFading = false;
                foreach (var member in Players()) if (member.body != null) member.Server_TeleportCharacter(SurgeryRoom);
                lab.endGuardSpawner.openProgress = 1.45f;
                Changed($"[escape] party teleported to surgery; vanilla {State.secondsLeft}-second server clock started");
                BroadcastEvent(EndingEvent.Door);
            }
        }
        if (State.phase == EndingPhase.Escape && State.escapeElapsed >= 5f && key.IsConscious() && Vector2.Distance(key.pos, SurgeryEntrance) < 2f)
            Handle(key, EndingRequest.Door, SurgeryEntrance, 0);
        if (State.phase == EndingPhase.Surgery) {
            if (!State.timerExpired) State.secondsLeft = Mathf.Max(0f, State.secondsLeft - Time.deltaTime);
            if (State.secondsLeft <= 0f && !State.timerExpired) {
                State.timerExpired = true; lab.endGuardSpawner.openProgress = 5f;
                Changed("[escape] vanilla timer expired; releasing five guards, no automatic failure");
            }
            ObserveTorsoAnesthesia(key.body.GetCoUtilsInstance());
            TickSurgery();
        }
        // Keep vanilla spawn distance/cadence and the other hall spawners.
        // Only the deadline spawner gets the five extra guards on timeout.
        lab.endGuardSpawner.guardsToSpawn = State.timerExpired ? Mathf.Max(0, 5 - State.guardsSpawned) : 0;
        if (State.phase == EndingPhase.Escaping && !State.hasCargo) {
            // exitWalker is a presentation/log marker, never an authority gate.
            // The trapdoor is shared; every living player can reach the exit.
            var walkers = Players().Where(p => p.body != null && p.IsAlive() && p.pos.x > 353f).ToArray();
            if (string.IsNullOrEmpty(State.exitWalker) && walkers.Length > 0) {
                State.exitWalker = Key(walkers[0]);
                Changed($"[escape] exit walk triggered by teammate={State.exitWalker}");
            }
            foreach (var walker in walkers) {
                if (Time.unscaledTime >= exitLogAt) {
                    exitLogAt = Time.unscaledTime + 1f;
                    log.l($"[escape] exit walker={Key(walker)} pos={walker.pos} falling={State.exitFalling}");
                }
                walker.body.forceWalk = !State.exitFalling;
                if (!State.exitFalling && walker.pos.x > 366f) {
                    State.exitFalling = true;
                    Changed($"[escape] exit walker={Key(walker)} pos={walker.pos}; trapdoor fall, awaiting true exit y>270 x<370");
                }
            }
            // Vanilla's final threshold has no lower x bound: a player may
            // already have moved left out of the forced-walk section.
            var reached = State.exitFalling ? Players().FirstOrDefault(p => p.body != null && p.IsAlive() && p.pos.y > 270f && p.pos.x < 370f) : null;
            if (reached != null) {
                State.exitWalker = Key(reached);
                log.l($"[escape] exit reached by={Key(reached)} pos={reached.pos}");
                BeginNarrative(2, $"true exit walker={Key(reached)} pos={reached.pos}");
            }
        }
        EndingGuardSync.Tick();
    }
    static partial void TickSurgery();

    static partial void ApplyEscapeEvent(EndingEvent kind) {
        if (kind == EndingEvent.Escape) StartEscapePresentation();
        if (kind == EndingEvent.Door) {
            StartEscapePresentation();
            if (ScavPlayer.LocalPlayer?.body != null) {
                ScavPlayer.LocalPlayer.body.transform.position = SurgeryRoom;
                ScavPlayer.LocalPlayer.body.SetVelocity(Vector2.zero);
                PlayerCamera.main.transform.position = SurgeryRoom;
            }
            roomApplied = true;
            StartSurgeryPresentation();
        }
        if (kind == EndingEvent.Surgery) OpenSurgeryForOperator();
    }
    static partial void OpenSurgeryForOperator();

    static void StartEscapePresentation() {
        var lab = EndLabScript.instance;
        if (lab == null || escapePresentation == lab) return;
        if (lab.scientistAnim == null || lab.surgicalDevice == null) return;
        escapePresentation = lab;
        Time.timeScale = 1f;
        if (PlayerCamera.main != null) PlayerCamera.main.curTimeScale = PlayerCamera.SpeedType.Normal;
        lab.StopAllCoroutines(); lab.doingEscape = true;
        lab.thoughtUi.text = ""; lab.scientistTalker.Skip();
        lab.scientistAnim.enabled = true; lab.scientistAnim.Play("ScientistShot");
        lab.scientistAnim.GetComponent<Collider2D>().enabled = false;
        MusicManager.main.StopSong(); MusicManager.main.StartCoroutine(MusicManager.main.EscapeMusic());
        foreach (var p in Players()) if (p.body != null) p.body.forceWalk = false;
        escapeVisualElapsed = State.escapeElapsed;
        lab.StartCoroutine(EscapeAlert(lab));
        ScavPlayer.LocalPlayer?.body?.talker.Talk(Locale.GetCharacter("labshoot"), null, force: false, resetTalkTimer: true);
        Utils.Create("HumanBlood", lab.scientistAnim.transform.position, 0f);
        Sound.Play("cut5", Vector2.zero, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: true, ignoreMixer: true);
        log.l("[ending] local escape presentation started");
    }
    static IEnumerator EscapeAlert(EndLabScript lab) {
        yield return new WaitForSeconds(Mathf.Max(0f, 5f - State.escapeElapsed));
        if (DuringEscape) lab.announcer.Talk(Locale.GetOther("escapealert"));
    }
    static IEnumerator EntranceFade() {
        GlobalDark.main.SetStayDark(false); GlobalDark.main.SetSpeedMultiplier(1f); GlobalDark.main.Darken();
        log.l("[escape] local surgery entrance fade started");
        yield return new WaitUntil(() => !GlobalDark.main.IsDarkening());
        log.l("[escape] local surgery entrance fade completed");
        Request(EndingRequest.EntranceReady, Vector2.zero);
    }
    static void StartSurgeryPresentation() {
        if (surgeryPresentation) return;
        surgeryPresentation = true;
        var lab = EndLabScript.instance;
        lab.endGuardSpawner.openProgress = 1.45f;
        Sound.Play("endlabDoor", SurgeryRoom, twoDimensional: false, pitchShift: false);
        MusicManager.main.specialLoop = true; MusicManager.main.PlaySong(lab.end1);
        if (Net.IsServer && lab.injector.GetComponent<FreshItemDrop>() == null) lab.injector.gameObject.AddComponent<FreshItemDrop>().forceOn = true;
        log.l("[escape] local surgery room presentation end1/injector");
    }
    // DurationOf is remaining time. A 12ml torso injection supplies 72s immediately.
    internal static void NotifyAnesthesia(Limb limb, float ml) {
        if (!Active || State.phase != EndingPhase.Surgery) return;
        if (ml <= 0f) return;
        log.l($"[escape] injector used limb={limb?.name} body={limb?.body?.name} ml={ml:0.000} carrier={State.cargoOwner} ready={State.surgeryReady}");
        if (limb?.body != null) ObserveTorsoAnesthesia(limb.body.GetCoUtilsInstance());
    }
    // SyringeMinigame supplies many tiny doses; vanilla adds them to remaining
    // time in DoTimedOp. Commit from that real server-side effect, never a
    // client's dose claim or the helper's own torso timer.
    internal static void ObserveTorsoAnesthesia(CoUtils effects) {
        if (!Net.IsServer || !Active || State.phase != EndingPhase.Surgery || State.surgeryReady || KeyPlayer?.body == null ||
            effects != KeyPlayer.body.GetCoUtilsInstance()) return;
        float remaining = effects.DurationOf("localanestheticDownTorso");
        if (!Util.IsFinite(remaining) || remaining <= 50f) return;
        State.surgeryReady = true;
        Changed($"[escape] torso anesthesia carrier={State.cargoOwner} remaining={remaining:0.000}; surgery immediately ready (server effect)");
    }

    static partial void ApplyEscape() {
        if (MinigameBase.main?.currentMinigame is CargoMinigame current && (!LocalSurgeon || State.phase != EndingPhase.Surgery))
            MinigameBase.main.EndMinigame();
        if (!DuringEscape) {
            escapePresentation = null; localExitFalling = false; roomApplied = false; entranceNotified = false;
            entranceFadeApplied = surgeryPresentation = exitPresentation = false; escapeHintUntil = 0f;
            return;
        }
        StartEscapePresentation();
        var lab = EndLabScript.instance;
        if (lab == null) return;
        if (State.entranceFading && !entranceFadeApplied) {
            entranceFadeApplied = true;
            lab.StartCoroutine(EntranceFade());
        }
        lab.doTimer = State.phase == EndingPhase.Surgery && !State.timerExpired;
        lab.timeLeft = State.secondsLeft;
        if (Net.IsClient) lab.endGuardSpawner.openProgress = State.guardDoorProgress + (Time.time - stateReceivedAt);
        lab.canStartSurgery = State.phase == EndingPhase.Surgery && State.surgeryReady;
        lab.surgeryDoor.forceDisable = State.phase != EndingPhase.Escaping;
        if (State.exitFalling && lab.doorJoint != null) {
            UnityEngine.Object.Destroy(lab.doorJoint);
            Sound.Play("robothit", ScavPlayer.LocalPlayer.pos, twoDimensional: false, pitchShift: false);
        }
        if (State.phase == EndingPhase.Escaping && !exitPresentation) {
            exitPresentation = true; MusicManager.main.PlaySong(lab.end2);
            log.l("[escape] local surgery complete end2/gate unlocked");
        }
        if (State.phase == EndingPhase.Escaping && ScavPlayer.LocalPlayer?.body != null &&
            ScavPlayer.LocalPlayer.IsAlive() && ScavPlayer.LocalPlayer.pos.x > 353f) {
            var body = ScavPlayer.LocalPlayer.body;
            body.forceWalk = !State.exitFalling;
            if (State.exitFalling && !localExitFalling) {
                localExitFalling = true; body.Ragdoll(); body.shock = 50f; body.Scream();
            }
        }
        lab.removedHeart.SetActive(!State.hasCargo);
        if (ScavPlayer.LocalPlayer?.body != null) {
            bool nearEntrance = Vector2.Distance(ScavPlayer.LocalPlayer.pos, SurgeryEntrance) < 2f;
            if (!nearEntrance) entranceNotified = false;
            if (State.phase == EndingPhase.Escape && nearEntrance && Time.unscaledTime >= doorRequestAt &&
                (!entranceNotified || Key(ScavPlayer.LocalPlayer) == State.cargoOwner)) {
                entranceNotified = true; doorRequestAt = Time.unscaledTime + 1f; Request(EndingRequest.Door, SurgeryEntrance);
            }
        }
        // Late joiners enter the room too; state broadcasts cannot teleport the
        // existing party repeatedly while they move or perform surgery.
        if (!roomApplied && (State.phase == EndingPhase.Surgery || State.phase == EndingPhase.Escaping)) ApplyEscapeEvent(EndingEvent.Door);
        PlayerCamera.main.blockItemUsage = false;
        if (LocalSurgeon && State.phase == EndingPhase.Surgery) OpenSurgeryForOperator();
    }
    static partial void LabEscapeVisuals(EndLabScript lab) {
        lab.timerUi.enabled = State.phase == EndingPhase.Surgery;
        if (lab.timerUi.enabled) {
            float remaining = State.secondsLeft;
            if (Net.IsClient && !State.timerExpired) remaining = Mathf.Max(0f, remaining - (Time.time - stateReceivedAt));
            lab.timerUi.color = Color.red.WithAlpha(lab.timerTransparency.Evaluate(remaining) * UnityEngine.Random.Range(.3f, .5f)); lab.timerUi.text = Mathf.CeilToInt(remaining).ToString();
        }
        if (!DuringEscape) return;
        escapeVisualElapsed += Time.deltaTime;
        lab.SetLightsColor(escapeVisualElapsed > 2f && escapeVisualElapsed < 6.25f ? Color.black : Color.Lerp(Color.red, new Color32(50, 0, 0, 255), Mathf.PingPong(Time.time * .66f, 1f)));
        if (escapeVisualElapsed > 6.25f && lab.turret != null) lab.turret.enabled = true;
        lab.canStartSurgery = State.phase == EndingPhase.Surgery && State.surgeryReady;
        if (State.phase == EndingPhase.Escape) {
            if (escapeHintUntil <= 0f) escapeHintUntil = Time.unscaledTime + 10f;
            lab.thoughtUi.text = Time.unscaledTime < escapeHintUntil ? Lang.Get("ending_reach_surgery", true) : "";
        } else if (State.phase == EndingPhase.Surgery) lab.thoughtUi.text = Locale.GetOther("cargosurgery" + (!State.surgeryReady ? 1 : string.IsNullOrEmpty(State.surgeon) ? 2 : !State.hasCargo ? 5 : State.surgeryStage == 0 ? 3 : 4));
        else lab.thoughtUi.text = Locale.GetOther("cargosurgery6");
    }
}

[HarmonyPatch(typeof(EndLabScript), nameof(EndLabScript.BeginEscapeSequence))]
internal static class Ending_BeginEscapePatch {
    static bool Prefix() { if (!EndingFlow.Active) return true; EndingFlow.ScientistShot(); return false; }
}
[HarmonyPatch(typeof(GuardSpawner), "Update")]
internal static class Ending_GuardSpawnerPatch { static bool Prefix() => !EndingFlow.Active || (Net.IsServer && !EndingFlow.SettlementLocked); }
[HarmonyPatch(typeof(GuardSpawner), nameof(GuardSpawner.CreateGuard))]
internal static class Ending_GuardSpawnCountPatch {
    static void Postfix(GuardSpawner __instance) {
        if (!EndingFlow.DuringEscape || !Net.IsServer || !EndingFlow.State.timerExpired || EndLabScript.instance?.endGuardSpawner != __instance) return;
        EndingFlow.State.guardsSpawned++;
        log.l($"[escape] authoritative deadline guard {EndingFlow.State.guardsSpawned}/5");
    }
}
#endif
