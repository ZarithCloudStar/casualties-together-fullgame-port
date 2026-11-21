#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Together;

internal static partial class EndingFlow {
    static bool localResultStarted;
    static PlayerCamera resultCamera;
    internal static bool IsFinished => Active && initialized && State.phase == EndingPhase.Finished;
    internal static bool SettlementLocked => IsFinished || (Active && State.phase == EndingPhase.Narrative);

    internal static void Finish(byte type, string reason) {
        if (!Active || !Net.IsServer || State.phase == EndingPhase.Finished || State.phase == EndingPhase.Failed || type < 1 || type > 3) return;
        State.phase = EndingPhase.Finished; State.ending = type; State.completed = true;
        State.guardKilled |= GuardScript.anyGuardKilled;
        Changed($"[ending] finished type={type} reason={reason}; forcing settlement for every player including spectators");
        // Keep the multiplayer save. Checkpoints carry the completion marker so
        // loading this run cannot farm an ending or silently resume exploration.
        foreach (var p in Players()) if (p.body != null) PlayerCheckpoint.WriteOne(p, false);
        PlayerCheckpoint.WriteRules(true);
        BroadcastEvent(EndingEvent.Finish);
    }

    static partial void ApplyEndingEvent(EndingEvent kind) {
        if (kind == EndingEvent.Finish) ApplyResult();
        ApplyEscapeEvent(kind);
    }
    static partial void ApplyEscapeEvent(EndingEvent kind);

    static partial void TickLaboratory() {
        var lab = EndLabScript.instance;
        var key = KeyPlayer;
        if (State.phase == EndingPhase.Laboratory && lab != null && key?.body != null) {
            if (!State.labEntered && key.IsAlive() && key.pos.x < 17f) {
                State.labEntered = true;
                Changed($"[ending] laboratory entered by carrier={Key(key)}; scientist dialogue unlocked");
            }
            if (State.labDialogueComplete && key.IsAlive() && Vector2.Distance(key.pos, lab.exitPoint) < 4f)
                BeginNarrative(1, "cargo carrier reached laboratory exit", "biopodnote");
        }
        TickEscape();
        if (WorldGeneration.world.biomeDepth == 11 && !IsFinished) EndingGuardSync.Tick();
    }
    static partial void TickEscape();

    static partial void ApplyLaboratory() {
        ApplyNarrative();
        if (!IsFinished) localResultStarted = false;
        if (IsFinished) ApplyResult();
        ApplyEscape();
    }
    static partial void ApplyEscape();

    static void ApplyResult() {
        var cam = PlayerCamera.main;
        var world = WorldGeneration.world;
        if (!IsFinished || cam == null || world == null || !Util.IsInWorld() || !Stable) return;
        world.cutsceneActive = true;
        foreach (var p in Players()) if (p.body != null) {
            p.body.forceNoMovement = true; p.body.forceWalk = false;
            p.body.moveDir = Vector2.zero;
            p.body.rb.velocity = Vector2.zero;
        }
        cam.blockItemUsage = true;
        if (UIInGame.SpectatorModeActive) UIInGame.StopSpectatorMode();
        if (ScavPlayer.LocalPlayer?.body != null) cam.body = ScavPlayer.LocalPlayer.body;
        if (resultCamera != cam) { resultCamera = cam; localResultStarted = false; }
        if (localResultStarted) return;
        if (ScavPlayer.LocalPlayer?.body == null) return; // retry when a late join's own body is created
        localResultStarted = true;
        UIInGame.StopSpectatorMode();
        cam.body = ScavPlayer.LocalPlayer.body;
        if (EndLabScript.instance != null) EndLabScript.instance.StopAllCoroutines();
        if (cam.finishCoroutine != null) cam.StopCoroutine(cam.finishCoroutine);
        if (MinigameBase.main?.currentMinigame != null) MinigameBase.main.EndMinigame();
        ScrollableText.ForceClose();
        cam.didDeathScreen = true;
        PlayerCamera_ToggleWoundView_MultiplayerPatch.CloseHealthPanel(cam);
        cam.craftingPanel.SetActive(false); cam.tradeMenu.SetActive(false); cam.itemCustomizer.SetActive(false);
        cam.body.forceNoMovement = true;
        int chr = cam.body.charType;
        string flag = State.ending == 1 ? "didbiopod" : State.ending == 2 ? "didescape" : "didmerc";
        try {
            PlayerPrefs.SetInt("wonchar" + chr, 1);
            PlayerPrefs.SetInt(flag, 1);
            SteamManager.TryUnlockAchievement("CHARWIN" + chr);
            SteamManager.TryUnlockAchievement(State.ending == 1 ? "BIOPOD" : State.ending == 2 ? "COMPANYSWORST" : "TOBECONTINUED");
            if (State.ending == 2 && !State.guardKilled) SteamManager.TryUnlockAchievement("PACIFIST");
            PlayerPrefs.Save();
            log.l($"[ending] local progress saved type={State.ending} char={chr} flag={flag} pacifist={!State.guardKilled}");
        } catch (System.Exception ex) { log.error($"[ending] local progress write failed: {ex}"); }
        GlobalDark.main.SetStayDark(false); GlobalDark.main.SetSpeedMultiplier(State.ending == 3 ? .05f : 1f);
        world.cutsceneOver.gameObject.SetActive(false);
        world.cutsceneUnder.sprite = world.cutsceneImages[State.ending == 1 ? 7 : State.ending == 2 ? 12 : 11];
        ScrollableText.closeType = ScrollableText.TextCloseType.Instant;
        MusicManager.main.specialLoop = false;
        MusicManager.main.PlaySong(Resources.Load<AudioClip>(State.ending == 2 ? "Sounds/music/freedom" : "Sounds/biopod"));
        cam.finishCoroutine = cam.StartCoroutine(cam.EndSequence(State.ending));
        log.l($"[ending] local EndSequence({State.ending}) started spectator/dead={(!cam.body.alive)}");
    }

    internal static bool InterceptNativeEnding(string method, ref IEnumerator result) {
        if (!Active) return true;
        // The vanilla caller is local-camera based. Only TickLaboratory/escape/
        // Merc authority may choose a party ending, never this local coroutine.
        result = Empty(); return false;
    }

    internal static void LabStarted(EndLabScript lab) {
        if (!Active) return;
        if (lab.scientistCoroutine != null) lab.StopCoroutine(lab.scientistCoroutine);
        lab.scientistCoroutine = lab.StartCoroutine(LabDialogue(lab));
    }
    static IEnumerator LabDialogue(EndLabScript lab) {
        yield return new WaitUntil(() => Stable && State.phase == EndingPhase.Laboratory);
        MusicManager.main.PlaySong(MusicManager.main.biomeMusic[11].songs[0]);
        MusicManager.main.specialLoop = true;
        Sound.Play("shuttleOpen", Vector2.zero, twoDimensional: true, pitchShift: false);
        lab.scientistAnimType = 1;
        PlayerCamera.main?.body?.talker.Talk(Locale.GetCharacter("labenter"), null, force: false, resetTalkTimer: true);
        // Vanilla EndLabScript.ScientistCutscene: x<17, ends c1..9,
        // player responses/thoughts, then the exit test. Lines 10..13 are
        // optional reminders after that gate, not additional prerequisites.
        yield return new WaitUntil(() => State.labEntered);
        if (!State.labDialogueComplete) {
            log.l("[ending] scientist main dialogue started after carrier entry");
            lab.scientistTalker.Talk(Locale.GetOther("endsc1").Replace("<>", WoundView.view.cInfo[2].ToString()));
            yield return new WaitForSecondsRealtime(3f);
            lab.scientistTalker.Talk(Locale.GetOther("endsc2").Replace("<>", WoundView.view.cInfo[2].ToString()));
            lab.scientistAnimType = 0;
            yield return new WaitUntil(() => lab.scientistTalker.FinishedTalking());
            PlayerCamera.main?.body?.talker.Talk(Locale.GetCharacter("labresponse"), null, force: false, resetTalkTimer: true);
            yield return new WaitForSecondsRealtime(4f);
            for (int line = 3; line <= 8; line++) {
                if (State.phase != EndingPhase.Laboratory) yield break;
                lab.scientistAnimType = line == 3 || line == 7 ? 0 : 1;
                lab.scientistTalker.Talk(Locale.GetOther("endsc" + line));
                yield return new WaitUntil(() => lab.scientistTalker.FinishedTalking());
            }
            yield return new WaitForSecondsRealtime(1f);
            var talker = PlayerCamera.main?.body?.talker;
            if (talker != null) {
                talker.Talk(Locale.GetCharacter("labthink1"), null, force: false, resetTalkTimer: true);
                yield return new WaitUntil(() => talker.FinishedTalking());
                talker.Talk(Locale.GetCharacter(State.cargoUnchipped ? "labthink2" : "labthink2chip"), null, force: false, resetTalkTimer: true);
                yield return new WaitUntil(() => talker.FinishedTalking());
            }
            yield return new WaitForSecondsRealtime(5f);
            lab.scientistTalker.Talk(Locale.GetOther("endsc9"));
            yield return new WaitUntil(() => lab.scientistTalker.FinishedTalking());
            if (Net.IsServer && State.phase == EndingPhase.Laboratory) {
                State.labDialogueComplete = true;
                Changed("[ending] scientist main dialogue completed; carrier exit now armed");
            }
        }
        for (int line = 10; line <= 13; line++) {
            yield return new WaitForSeconds(14f);
            if (State.phase != EndingPhase.Laboratory) yield break;
            lab.scientistTalker.Talk(Locale.GetOther("endsc" + line));
        }
    }
    internal static bool LabUpdate(EndLabScript lab) {
        if (!Active) return true;
        if (!Stable) return false;
        var cam = PlayerCamera.main;
        if (cam == null) return false;
        if (SettlementLocked) {
            lab.thoughtUi.enabled = false; lab.timerUi.enabled = false; lab.announcerUi.enabled = false;
            return false;
        }
        lab.announcerUi.text = lab.announcer.text.text;
        if (!lab.doingEscape) {
            int frame = Mathf.FloorToInt(Time.time * 1.5f) % 2;
            lab.scientist.sprite = lab.scientistSprites[(lab.scientistAnimType == 1 ? 2 : 0) + frame];
        }
        if (State.phase == EndingPhase.Laboratory) {
            // Any teammate may shoot after the shared dialogue. Eligibility is
            // checked against the cargo owner's chip on the server.
            cam.blockItemUsage = !State.labDialogueComplete;
            if (cam.body != null) cam.body.forceWalk = true;
        }
        if (Net.IsServer) {
            // The original ForceBaseStats uses the camera body. Apply its safe
            // lower bounds to every participant without camera swapping.
            foreach (var p in Players()) if (p.body != null && p.IsAlive()) {
                p.body.thirst = Mathf.Max(p.body.thirst, 60f);
                p.body.hunger = Mathf.Max(p.body.hunger, 60f);
                p.body.energy = Mathf.Max(p.body.energy, 90f);
            }
        }
        LabEscapeVisuals(lab);
        return false;
    }
    static partial void LabEscapeVisuals(EndLabScript lab);
}

[HarmonyPatch]
internal static class World_NativeEndingPatch {
    static IEnumerable<MethodBase> TargetMethods() {
        foreach (string method in new[] { "BiopodEnding", "BiopodEndingAlt", "BiopodEndingAlt2", "EscapeEnding", "MercEnding" })
            yield return AccessTools.Method(typeof(WorldGeneration), method);
    }
    static bool Prefix(MethodBase __originalMethod, ref IEnumerator __result) => EndingFlow.InterceptNativeEnding(__originalMethod.Name, ref __result);
}
[HarmonyPatch(typeof(PlayerCamera), "HandleDeathScreen")]
internal static class Ending_KeepResultPatch { static bool Prefix() => !EndingFlow.SettlementLocked; }

[HarmonyPatch(typeof(UIInGame), nameof(UIInGame.StartSpectatorMode))]
internal static class Ending_NoSpectatorAfterResultPatch { static bool Prefix() => !EndingFlow.SettlementLocked; }

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.HandleInput))]
internal static class Ending_ResultInputPatch {
    static bool Prefix() {
        if (!EndingFlow.SettlementLocked) return true;
        // Keep the pause/menu route for the host's existing return broadcast.
        if (Input.GetKeyDown(KeyBinds.GetBind("pause"))) PauseHandler.TogglePause();
        return false;
    }
}
[HarmonyPatch]
internal static class Ending_ResultWorldActionsPatch {
    static IEnumerable<MethodBase> TargetMethods() {
        foreach (string method in new[] { "HandleDragging", "HandleAttacks", "HandleGunMenu" })
            yield return AccessTools.Method(typeof(PlayerCamera), method);
    }
    static bool Prefix() => !EndingFlow.SettlementLocked;
}

[HarmonyPatch(typeof(EndLabScript), "Start")]
internal static class Ending_LabStartPatch { static void Postfix(EndLabScript __instance) => EndingFlow.LabStarted(__instance); }
[HarmonyPatch(typeof(EndLabScript), "Update")]
internal static class Ending_LabUpdatePatch { static bool Prefix(EndLabScript __instance) => EndingFlow.LabUpdate(__instance); }

#endif
