#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Together;

internal static partial class EndingFlow {
    static float reportSurgeryAt;
    internal static bool LocalSurgeon => State.phase == EndingPhase.Surgery && State.surgeryOperators.Contains(Key(ScavPlayer.LocalPlayer));
    static readonly Dictionary<CoUtils, (float time, float remaining)> godAnesthesia = new();

    internal static void HealForGodmode(Body body, Action heal) {
        if (!Active || State.phase != EndingPhase.Surgery || body != KeyPlayer?.body) {
            if (State.phase != EndingPhase.Surgery) godAnesthesia.Clear();
            heal(); return;
        }
        var effects = body.GetCoUtilsInstance();
        const string id = "localanestheticDownTorso";
        float remaining = effects.DurationOf(id);
        // CancelAll restarts the coroutine every frame. Charge game time here
        // so repeated god healing cannot freeze the anesthetic's countdown.
        if (godAnesthesia.TryGetValue(effects, out var previous))
            remaining = Mathf.Max(0f, remaining - Mathf.Max(0f, Time.time - previous.time - Mathf.Max(0f, previous.remaining - remaining)));
        heal();
        // ResetHealth cancels all CoUtils effects. Preserve only this required
        // surgery effect during the existing godmode loop, with vanilla decay.
        if (remaining > 0f && effects.DurationOf(id) < remaining) {
            var limb = body.limbs[2];
            effects.DoTimedOp(id, () => {
                limb.pain = 0f;
                limb.forceMult = effects.DurationOf(id) > 2f ? 0f : 1f;
            }, remaining - effects.DurationOf(id));
        }
        godAnesthesia[effects] = (Time.time, effects.DurationOf(id));
    }

    static partial void HandleSurgery(ScavPlayer p, EndingRequest action, Vector2 position, int value) {
        if (action == EndingRequest.Anesthesia) {
            // Retain the wire enum for save/protocol compatibility. Only the
            // validated medical RPC's actual server CoUtils effect can commit.
            log.l("[anesth] ignored legacy anesthesia claim; awaiting server patient effect");
            return;
        }
        if (action != EndingRequest.Surgery && action != EndingRequest.SurgeryProgress && action != EndingRequest.SurgeryComplete) return;
        var lab = EndLabScript.instance;
        var patient = KeyPlayer;
        if (State.phase != EndingPhase.Surgery || lab == null || patient?.body == null || !patient.IsAlive()) return;
        if (Vector2.Distance(p.pos, lab.surgicalDevice.transform.position) > 12f ||
            Vector2.Distance(patient.pos, lab.surgicalDevice.transform.position) > 12f) {
            log.l($"[ending] surgery out of range operator={p.pos} carrier={patient.pos} device={(Vector2)lab.surgicalDevice.transform.position}");
            Notice(p, "ending_surgery_near"); return;
        }
        if (action == EndingRequest.Surgery) {
            if (!State.surgeryReady) { Notice(p, "ending_surgery_invalid"); return; }
            State.surgeon = Key(p);
            if (!State.surgeryOperators.Contains(Key(p))) State.surgeryOperators.Add(Key(p));
            Changed($"[ending] surgery operator={State.surgeon} patient={State.cargoOwner} mask={State.surgeryMask}");
            BroadcastEvent(EndingEvent.Surgery);
            return;
        }
        if (!State.surgeryOperators.Contains(Key(p))) return;
        if (action == EndingRequest.SurgeryProgress) {
            if (value < 0 || value > 6 || float.IsNaN(position.x) || float.IsNaN(position.y) ||
                float.IsInfinity(position.x) || float.IsInfinity(position.y) || Mathf.Abs(position.x) > 4000f || Mathf.Abs(position.y) > 4000f) return;
            int bit = 1 << value;
            if ((State.surgeryMask & bit) != 0) return;
            // Validate each original CargoMinigame milestone, not a client claim
            // that the whole surgery completed. All seven steps are monotonic.
            bool valid = value < 4
                ? State.surgeryStage == 0 && Mathf.Abs(Mathf.Abs(position.x) - 288f) < 25f && Mathf.Abs(position.y - (value < 2 ? 248f : -365f)) < 25f
                : value == 4
                    ? State.surgeryStage == 1 && Mathf.Abs(position.x) > 980f
                    : State.surgeryStage == 2 && Vector2.Distance(position, new Vector2(value == 5 ? 555f : -555f, -63f)) < 55f;
            if (!valid) { Notice(p, "ending_surgery_invalid"); return; }
            State.surgeryMask |= bit;
            if ((State.surgeryMask & 15) == 15 && State.surgeryStage == 0) State.surgeryStage = 1;
            if (value == 4) {
                State.surgeryStage = 2; State.hasCargo = false;
                var limb = patient.body.limbs[2];
                limb.skinHealth = limb.muscleHealth = 0f; limb.bleedAmount = 100f;
                if (patient.body.TryGetNetBody(out var nb)) MedicalSync.Server_QueueSendCharacterHealth(nb, true);
            }
            Changed($"[cargo] surgery milestone operator={Key(p)} step={value} stage={State.surgeryStage} mask={State.surgeryMask} cargo={State.hasCargo}");
            return;
        }
        // Completion follows the seven vanilla milestones, with no extra timer.
        if (State.surgeryMask != 127 || State.hasCargo) {
            Notice(p, "ending_surgery_invalid"); return;
        }
        var torso = patient.body.limbs[2];
        torso.muscleHealth = torso.skinHealth = 100f; torso.bleedAmount = 0f; torso.infectionAmount = 0f;
        if (patient.body.TryGetNetBody(out var healed)) MedicalSync.Server_QueueSendCharacterHealth(healed, true);
        State.phase = EndingPhase.Escaping; State.surgeon = "";
        lab.surgeryDoor.forceDisable = false; lab.doTimer = false;
        Changed("[ending] cargo surgery completed; gate unlocked for any living teammate");
        BroadcastEvent(EndingEvent.Surgery);
    }

    static partial void TickSurgery() {
        bool removed = State.surgeryOperators.RemoveAll(id => {
            foreach (var p in Players()) if (Key(p) == id)
                return !p.IsAlive() || !p.IsConscious() || Vector2.Distance(p.pos, EndLabScript.instance.surgicalDevice.transform.position) > 12f;
            return true;
        }) > 0;
        if (removed) Changed("[ending] absent surgery participants removed; shared progress retained");
    }

    static partial void OpenSurgeryForOperator() {
        if (!LocalSurgeon || KeyPlayer?.body == null || MinigameBase.main == null || MinigameBase.main.currentMinigame != null) return;
        MinigameBase.main.StartMinigame(new CargoMinigame(KeyPlayer.body.limbs[2]), null);
    }
    internal static bool AllowCargoMinigame() => !Active || (LocalSurgeon && State.phase == EndingPhase.Surgery);

    internal static void ConfigureCargo(CargoMinigame game) {
        if (!Active || !LocalSurgeon) return;
        if (State.surgeryStage > game.stage) game.held = null;
        game.stage = Mathf.Max(game.stage, State.surgeryStage);
        int done = 0;
        for (int i = 0; i < 4; i++) if ((State.surgeryMask & (1 << i)) != 0) {
            var clamp = game.clamps[i];
            clamp.localPosition = new Vector2(clamp.localPosition.x > 0 ? 288 : -288, i < 2 ? 248 : -365);
            clamp.GetComponent<Image>().raycastTarget = false;
            if (game.held == clamp) game.held = null;
        }
        for (int i = 0; i < 4; i++) if (!game.clamps[i].GetComponent<Image>().raycastTarget) done++;
        game.clampsDone = game.stage == 0 ? done : 0;
        if (game.stage == 2) {
            game.cargo.localPosition = new Vector2(1100f, -1000f);
            for (int i = 0; i < 2; i++) if ((State.surgeryMask & (1 << (i + 5))) != 0) {
                game.hoses[i].localPosition = new Vector2(i == 0 ? 555 : -555, -63);
                game.hoses[i].GetComponent<Image>().raycastTarget = false;
                if (game.held == game.hoses[i]) game.held = null;
            }
        }
        if (game.stage == 2) for (int i = 0; i < 2; i++)
            if (!game.hoses[i].GetComponent<Image>().raycastTarget) game.clampsDone++;
    }
    internal static void ReportCargo(CargoMinigame game) {
        if (!LocalSurgeon || State.phase != EndingPhase.Surgery) return;
        ConfigureCargo(game);
        if (Time.unscaledTime < reportSurgeryAt) return;
        reportSurgeryAt = Time.unscaledTime + .25f;
        for (int i = 0; i < 4; i++) if ((State.surgeryMask & (1 << i)) == 0 && !game.clamps[i].GetComponent<Image>().raycastTarget) {
            Request(EndingRequest.SurgeryProgress, game.clamps[i].localPosition, i); return;
        }
        if (game.stage >= 2 && (State.surgeryMask & 16) == 0) { Request(EndingRequest.SurgeryProgress, game.cargo.localPosition, 4); return; }
        for (int i = 0; i < 2; i++) if ((State.surgeryMask & (1 << (i + 5))) == 0 && !game.hoses[i].GetComponent<Image>().raycastTarget) {
            Request(EndingRequest.SurgeryProgress, game.hoses[i].localPosition, i + 5); return;
        }
        if (State.surgeryMask == 127) Request(EndingRequest.SurgeryComplete, Vector2.zero);
    }
    internal static bool CargoMayEnd(CargoMinigame game) {
        if (!Active) return true;
        ReportCargo(game);
        // Wait for the server's final commit before closing the operator UI.
        return State.phase != EndingPhase.Surgery || !LocalSurgeon;
    }
}

[HarmonyPatch(typeof(SurgicalDeviceBehavior), nameof(SurgicalDeviceBehavior.OnUse))]
internal static class Ending_SurgicalUsePatch {
    static bool Prefix(SurgicalDeviceBehavior __instance) {
        if (!EndingFlow.Active) return true;
        EndingFlow.Request(EndingRequest.Surgery, __instance.transform.position); return false;
    }
}
[HarmonyPatch(typeof(CargoMinigame), nameof(CargoMinigame.Start))]
internal static class Ending_CargoStartPatch { static void Postfix(CargoMinigame __instance) => EndingFlow.ConfigureCargo(__instance); }
[HarmonyPatch(typeof(CargoMinigame), nameof(CargoMinigame.PhysicsUpdate))]
internal static class Ending_CargoProgressPatch { static void Postfix(CargoMinigame __instance) => EndingFlow.ReportCargo(__instance); }
[HarmonyPatch(typeof(Body), nameof(Body.RemoveCargo))]
internal static class Ending_CargoRemovalPatch { static bool Prefix() => !EndingFlow.Active; }
[HarmonyPatch(typeof(MinigameBase), "get_body")]
internal static class Ending_SurgeryPatientPatch {
    static void Postfix(MinigameBase __instance, ref Body __result) {
        if (EndingFlow.Active && __instance.currentMinigame is CargoMinigame && EndingFlow.KeyPlayer?.body != null) __result = EndingFlow.KeyPlayer.body;
    }
}
#endif
