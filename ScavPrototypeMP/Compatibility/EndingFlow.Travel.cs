#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using System.Collections;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Together;

internal static partial class EndingFlow {
    static float breachUntil;
    static WorldGeneration presentationWorld;
    static Coroutine presentation;
    static bool drillDeparting;
    static bool loggedLayer10Bottom;

    static void BroadcastEvent(EndingEvent kind) {
        Publish(); // events are ordered behind their authoritative state
        var w = Net.CreateWriter(NetmsgId.CLIENT_EndingEvent); w.Put((byte)kind);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, w, ServerMain.AllClientIdsExceptHost);
        ApplyEvent(kind);
    }

    static partial void ReceiveFlowEvent(EndingEvent kind, NetDataReader reader) => ApplyEvent(kind);
    static void ApplyEvent(EndingEvent kind) {
        if (kind == EndingEvent.Drill)
            Sound.Play("drillpoduse", Vector2.zero, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: true);
        if (kind == EndingEvent.Breach) BeginBreachPresentation();
        if (kind == EndingEvent.HeartPickup) ApplyHeartPickup();
        ApplyEndingEvent(kind);
    }
    static partial void ApplyEndingEvent(EndingEvent kind);

    // The prefab origin is not its usable interior. Vanilla OnUse only checks
    // didReady/started; use the real collider, allowing a body's half-width.
    internal static bool InPod(ScavPlayer p, CargoDrillPod pod) {
        if (p?.body == null || !p.IsAlive() || pod == null) return false;
        var collider = pod.GetComponent<Collider2D>();
        if (collider != null) {
            var bounds = collider.bounds;
            bounds.Expand(new Vector3(1.5f, 2f, 0f));
            Vector2 pos = p.netBody != null ? p.netBody.pos : (Vector2)p.body.transform.position;
            if (pos.x >= bounds.min.x && pos.x <= bounds.max.x && pos.y >= bounds.min.y && pos.y <= bounds.max.y) return true;
        }
        Vector2 bodyPosition = p.netBody != null ? p.netBody.pos : (Vector2)p.body.transform.position;
        return Vector2.Distance(bodyPosition, pod.transform.position) < SharedMain.max_player_interaction_distance;
    }

    static partial void HandleFlow(ScavPlayer p, EndingRequest action, Vector2 position, int value) {
        if (action != EndingRequest.Drill) { HandleLabRequest(p, action, position, value); return; }
        if (!State.hasCargo) { Notice(p, "ending_drill_locked"); return; }
        if (WorldGeneration.world.biomeDepth != 9 || drillDeparting || WorldGeneration.world.doingRegen) return;
        var pod = Object.FindObjectsOfType<CargoDrillPod>(true).FirstOrDefault(x =>
            Vector2.Distance(x.transform.position, position) < 1f && Vector2.Distance(p.pos, x.transform.position) < SharedMain.max_player_interaction_distance);
        if (pod == null) { Notice(p, "ending_invalid_interaction"); return; }
        // The carrier triggers the pod by interacting with it, not by standing inside.
        if (Key(p) != State.cargoOwner) {
            log.l($"[cargo] drill waiting owner={State.cargoOwner} requester={Key(p)} resolved={Key(KeyPlayer)} hasCargo={KeyPlayer?.body?.hasCargo} carrier={KeyPlayer?.body?.transform.position} pod={pod.transform.position}");
            Notice(p, "ending_wait_carrier"); return;
        }
        drillDeparting = true;
        Changed($"[ending] drill departure requested={Key(p)} carrier={State.cargoOwner}");
        BroadcastEvent(EndingEvent.Drill);
        PlayerCheckpoint.WriteLayerTransition();
        WorldGeneration.world.StartCoroutine(WorldGeneration.world.RegenerateWorld());
    }
    static partial void HandleLabRequest(ScavPlayer p, EndingRequest action, Vector2 position, int value);

    static partial void TickFlow() {
        if (State.phase == EndingPhase.Narrative) { TickNarrative(); return; }
        var world = WorldGeneration.world;
        if (world.biomeDepth != 10) loggedLayer10Bottom = false;
        if (world.biomeDepth == 10 && !loggedLayer10Bottom && Players().Any(p => p.body != null && p.pos.y < -(float)world.halfHeight + 3.1f)) {
            loggedLayer10Bottom = true;
            log.l($"[layer] transition denied source=EndingFlow from=10 reason=bottom topRequired={world.halfHeight - 5f}");
        }
        if (world.biomeDepth == 10 && State.phase == EndingPhase.Cargo) {
            foreach (var p in Players()) {
                bool atTop = p != KeyPlayer && p.body != null && p.IsAlive() && p.pos.y > world.halfHeight - 5f;
                if (atTop && topWaiters.Add(p.playerId)) Notice(p, "ending_wait_carrier_surface");
                if (!atTop) topWaiters.Remove(p.playerId);
            }
        } else topWaiters.Clear();
        if (world.biomeDepth != 9) drillDeparting = false;
        if (State.phase == EndingPhase.Cargo && world.biomeDepth == 10 && KeyPlayer?.body != null && KeyPlayer.IsAlive() &&
            KeyPlayer.pos.y > world.halfHeight - 5f && !world.doingRegen) {
            if (TryMercEnding()) return;
            State.phase = EndingPhase.Breach;
            log.l($"[layer] transition source=EndingFlow.Breach from=10 to=11 carrier={State.cargoOwner} y={KeyPlayer.pos.y} topRequired={world.halfHeight - 5f}");
            breachUntil = Time.unscaledTime + 19f;
            Changed($"[ending] layer10 breach carrier={State.cargoOwner}; all players entering cutscene");
            BroadcastEvent(EndingEvent.Breach);
        }
        if (State.phase == EndingPhase.Breach) {
            if (world.biomeDepth == 11) {
                State.phase = EndingPhase.Laboratory;
                world.cutsceneActive = false;
                foreach (var p in Players()) if (p.body != null) { p.body.forceNoMovement = false; p.body.forceWalk = true; }
                Changed("[ending] all players entered laboratory");
            } else if (world.biomeDepth == 10 && !world.doingRegen) {
                // A save made during the presentation replays it from the start.
                if (breachUntil <= 0f) { breachUntil = Time.unscaledTime + 19f; BroadcastEvent(EndingEvent.Breach); }
                if (Time.unscaledTime >= breachUntil) {
                    StopBreachPresentation();
                    world.cutsceneActive = false;
                    PlayerCheckpoint.WriteLayerTransition();
                    log.l("[layer] transition source=EndingFlow.RegenerateWorld from=10 to=11 after=breach-presentation");
                    world.StartCoroutine(world.RegenerateWorld());
                }
            }
        }
        TickLaboratory();
    }
    static partial void TickLaboratory();
    static readonly System.Collections.Generic.HashSet<knetid> topWaiters = new();
    // Merc qualification is evaluated before choosing the laboratory branch.
    static bool TryMercEnding() { bool handled = false; CheckMercEnding(ref handled); return handled; }
    static partial void CheckMercEnding(ref bool handled);

    static partial void ApplyFlow() {
        var world = WorldGeneration.world;
        if (State.phase == EndingPhase.Breach && world.biomeDepth == 10) {
            world.cutsceneActive = true;
            foreach (var p in Players()) if (p.body != null) p.body.forceNoMovement = true;
            if (PlayerCamera.main != null) PlayerCamera.main.blockItemUsage = true;
            if (presentationWorld != world) BeginBreachPresentation();
        } else if (presentationWorld != null) {
            StopBreachPresentation();
            if (State.phase != EndingPhase.Finished) {
                world.cutsceneActive = false;
                if (PlayerCamera.main?.body != null) PlayerCamera.main.body.forceNoMovement = false;
            }
        }
        ApplyLaboratory();
    }
    static partial void ApplyLaboratory();

    static void BeginBreachPresentation() {
        var world = WorldGeneration.world;
        if (world == null || !Stable || world.doingRegen || world.biomeDepth != 10 || (presentationWorld == world && presentation != null)) return;
        presentationWorld = world;
        presentation = world.StartCoroutine(BreachPresentation(world));
    }
    static IEnumerator BreachPresentation(WorldGeneration world) {
        // Timed together by the host: an unconscious/spectating player or a
        // client who does not click cannot leave the party stuck in a cutscene.
        Time.timeScale = 1f;
        GlobalDark.main.SetStayDark(false);
        GlobalDark.main.SetSpeedMultiplier(1f);
        GlobalDark.main.Darken();
        log.l("[ending] breach fade started before cover");
        yield return new WaitUntil(() => !GlobalDark.main.IsDarkening());
        log.l("[ending] breach fade completed; showing narrative cover");
        ScrollableText.closeType = ScrollableText.TextCloseType.OnFade;
        ScrollableText.CreateText(Locale.GetOther("surfacebreachnote"), cover: true);
        MusicManager.main.PlaySong(Resources.Load<AudioClip>("Sounds/music/surfacebreachA"));
        yield return new WaitForSecondsRealtime(6f);
        ScrollableText.ForceClose();
        world.cutsceneUnder.gameObject.SetActive(true); world.cutsceneUnder.sprite = world.cutsceneImages[6];
        if (PlayerCamera.main?.body != null) {
            world.cutsceneOver.gameObject.SetActive(true);
            world.cutsceneOver.sprite = world.cutsceneImages[PlayerCamera.main.body.charType];
        }
        MusicManager.main.PlaySong(Resources.Load<AudioClip>("Sounds/music/surfacebreachB"));
        yield return new WaitForSecondsRealtime(4f);
        GlobalDark.main.Darken();
        yield return new WaitUntil(() => !GlobalDark.main.IsDarkening());
        world.cutsceneUnder.gameObject.SetActive(false); world.cutsceneOver.gameObject.SetActive(false);
        ScrollableText.CreateText(Locale.GetOther("labenternote"), cover: true);
        yield return new WaitForSecondsRealtime(5f);
        ScrollableText.ForceClose();
    }
    static void StopBreachPresentation() {
        if (presentationWorld != null) {
            if (presentation != null) presentationWorld.StopCoroutine(presentation);
            presentationWorld.cutsceneUnder.gameObject.SetActive(false);
            presentationWorld.cutsceneOver.gameObject.SetActive(false);
        }
        presentation = null; presentationWorld = null; breachUntil = 0f;
        ScrollableText.ForceClose();
        MusicManager.main.specialLoop = false;
    }

    internal static bool DrillUpdate(CargoDrillPod pod) {
        if (!Active) return true;
        if (pod == null) return false;
        if (State.hasCargo && !pod.didReady) {
            pod.didReady = true;
            if (pod.bld == null) pod.bld = pod.GetComponent<BuildingEntity>();
            var usable = pod.GetComponent<UsableObject>();
            try { if (usable != null) usable.toggleString = Locale.GetOther("finaltravel"); }
            catch (System.Exception e) { log.warn("[drill] ready label: " + e.Message); }
            var building = pod.GetComponent<BuildingEntity>();
            try { if (building != null) building.description = Locale.GetBuilding("highpowereddrillpoddsc2"); }
            catch (System.Exception e) { log.warn("[drill] ready description: " + e.Message); }
        }
        return false;
    }
    internal static IEnumerator Empty() { yield break; }
}

[HarmonyPatch(typeof(CargoDrillPod), "Update")]
internal static class CargoDrill_Update_EndingPatch { static bool Prefix(CargoDrillPod __instance) => EndingFlow.DrillUpdate(__instance); }
[HarmonyPatch(typeof(CargoDrillPod), nameof(CargoDrillPod.OnUse))]
internal static class CargoDrill_Use_EndingPatch {
    static bool Prefix(CargoDrillPod __instance) {
        if (!EndingFlow.Active) return true;
        EndingFlow.Request(EndingRequest.Drill, __instance.transform.position); return false;
    }
}
[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.SurfaceBreachCutscene))]
internal static class SurfaceBreach_EndingPatch {
    static bool Prefix(ref IEnumerator __result) {
        if (!EndingFlow.Active) return true;
        __result = EndingFlow.Empty(); return false;
    }
}
[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.ContinueRun))]
internal static class Ending_NoNormalLayerExitPatch {
    [HarmonyPriority(Priority.First)]
    static bool Prefix() => !EndingFlow.Active || WorldGeneration.world == null || WorldGeneration.world.biomeDepth < 9 || WorldGeneration.world.biomeDepth > 11;
}
#endif
