#if FULLGAME
using CasualtiesTogetherUtils;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace Together;

internal static partial class EndingFlow {
    static float cargoAcquireUntil;
    static Body pickupBody;
    static bool pickupWasFrozen;
    static BlueHeart visualHeart;
    static readonly HashSet<string> rewardedNearHeart = new();

    // BlueHeart.Update, fullgame lines 120-179, excluding only the native
    // local-camera Cutscene trigger. Formulas, material properties, range,
    // heartbeat audio, camera attraction and breathing are deliberately vanilla.
    static void TickHeartVisuals(BlueHeart heart) {
        var cam = PlayerCamera.main;
        var body = ScavPlayer.LocalPlayer?.body;
        if (cam == null || body == null || heart.light == null) return; // Start must have initialized origPos/light
        if (visualHeart != heart) {
            visualHeart = heart;
            rewardedNearHeart.Clear();
            log.l($"[cargo] vanilla blue-heart pulse active side={(Net.IsServer ? "host" : "client")} origin={heart.origPos}");
        }
        if (!heart.didTrapRemoval && Net.IsServer) {
            heart.didTrapRemoval = true;
            foreach (var collider in Physics2D.OverlapAreaAll(heart.transform.position + new Vector3(-64f, -64f),
                heart.transform.position + new Vector3(32f, 32f), LayerMask.GetMask("Ground")))
                if (collider.TryGetComponent<BuildingEntity>(out var building) && building.id != "highpowereddrillpod") Object.Destroy(building.gameObject);
        }
        float num = Vector2.Distance(heart.transform.position, body.transform.position) / 500f;
        float num2 = Mathf.Max(body.transform.position.y - heart.transform.position.y, 0f) / 500f;
        if (num < .075f)
            cam.customLookPos = Vector2.Lerp(heart.transform.position + Vector3.up * 3f, body.transform.position, num / .075f);
        if (!heart.wasClose && num < .06f) {
            heart.wasClose = true;
            body.happiness += 5f;
            body.talker.Talk(Locale.GetCharacter("cargo1"));
            log.l($"[cargo] local approach camera/dialogue/music active distance={num * 500f:0.0}");
        }
        // The server also owns the remote player's once-only proximity reward.
        if (Net.IsServer) foreach (var p in Players())
            if (!p.IsLocal && p.body != null && Vector2.Distance(p.pos, heart.transform.position) < 30f && rewardedNearHeart.Add(Key(p))) {
                p.body.happiness += 5f;
                if (p.body.TryGetNetBody(out var nb)) MedicalSync.Server_QueueSendCharacterHealth(nb, true);
            }
        MusicManager.main.cutsceneVolMult = Mathf.Clamp01((num - .1f) * 30f);
        float num3 = 1f + heart.pulseSpeedAdd;
        if (num < .5f) num3 += (.5f - num) * 6f;
        heart.timeSincePulsed += Time.deltaTime * num3;
        heart.pulseTime -= Time.deltaTime * num3 * num3;
        if (heart.pulseTime < 0f) {
            heart.Pulse();
            Sound.Play("blueheartbeat", heart.transform.position, twoDimensional: true, pitchShift: false, null, 1f - num);
        }
        float value = heart.timeSincePulsed * 50f;
        float value2 = .5f + num * 8f;
        float num4 = (1f - num2) * Mathf.Clamp01((10f - heart.timeSincePulsed) * .1f);
        heart.mat.SetFloat("_DesiredDistance", value);
        heart.mat.SetFloat("_EdgeSize", value2);
        heart.mat.SetColor("_Color", new Color(0f, 1f, 1f, num4 * num4 * num4));
        heart.transform.position = heart.origPos + Random.insideUnitCircle * .1f;
        heart.transform.localScale = new Vector2(1f + Mathf.Sin(Time.time * 3f) * .25f + Random.value * heart.pulseSpeedAdd, 1f);
    }

    static void TickHeartPickup() {
        if (!State.cargoAcquiring || State.phase != EndingPhase.Cargo) return;
        if (cargoAcquireUntil <= 0f) cargoAcquireUntil = Time.time + 5f * (1f - Mathf.Clamp01(State.cargoPickupProgress));
        if (Time.time < cargoAcquireUntil || KeyPlayer?.body == null || !KeyPlayer.IsAlive()) return;
        State.cargoAcquiring = false; State.hasCargo = true; State.cargoPickupProgress = 1f;
        KeyPlayer.body.EmbedCargo(); // the only authoritative embed, after five seconds
        if (KeyPlayer.body.TryGetNetBody(out var nb)) MedicalSync.Server_QueueSendCharacterHealth(nb, true);
        Changed($"[cargo] pickup committed owner={State.cargoOwner}; blue heart and halo removed globally");
    }

    static void ApplyHeartPickup() {
        if (State.cargoAcquiring && BlueHeart.main != null) {
            var body = KeyPlayer?.body;
            if (pickupBody != body) {
                ReleasePickupBody();
                pickupBody = body;
                if (body != null) { pickupWasFrozen = body.forceNoMovement; body.forceNoMovement = true; }
            }
            var heart = BlueHeart.main;
            if (!heart.cutsceneStarted && heart.light != null) {
                heart.cutsceneStarted = true; // state/events/repeated requests cannot replay it
                PlayerCamera.main?.SetTimeScale(PlayerCamera.SpeedType.Normal, switchSound: false, force: true);
                if (KeyPlayer?.IsLocal == true) pickupBody?.talker.Talk(Locale.GetCharacter("cargo2"));
                Sound.Play("blueHeartObtain", Vector2.zero, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: true, ignoreMixer: true);
                heart.StartCoroutine(HeartPickupPresentation(heart));
            }
        } else if (pickupBody != null) {
            if (State.hasCargo && KeyPlayer?.IsLocal == true && pickupBody == ScavPlayer.LocalPlayer?.body) {
                PlayerCamera.main.StartCoroutine(PlayerCamera.main.CommenceBlinding());
                SteamManager.TryUnlockAchievement("MILLIONSTOONE");
            }
            if (State.cargoTaken) MusicManager.main.cutsceneVolMult = 0f;
            ReleasePickupBody();
        }
    }

    static void ReleasePickupBody() {
        if (pickupBody != null) pickupBody.forceNoMovement = pickupWasFrozen;
        pickupBody = null;
    }

    // BlueHeart.Cutscene lines 56-68. Presentation only: no local EmbedCargo,
    // ownership decisions, destroys, or world regeneration.
    static IEnumerator HeartPickupPresentation(BlueHeart heart) {
        log.l($"[cargo] pickup animation started once side={(Net.IsServer ? "host" : "client")} owner={State.cargoOwner}");
        Sound.Play("blueheartsfx", heart.transform.position, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: true, ignoreMixer: true);
        Vector2 oldPos = heart.origPos;
        float timer = 5f * Mathf.Clamp01(State.cargoPickupProgress);
        while (heart != null && State.cargoAcquiring) {
            timer = Mathf.Min(5f, timer + Time.deltaTime);
            heart.pulseSpeedAdd = timer * 1.5f;
            heart.origPos = Vector2.Lerp(oldPos, oldPos + Vector2.up * 7f, Utils.InQuad(timer * .2f));
            heart.light.intensity = 3f + timer * 30f * Utils.InQuint(timer * .2f);
            if (Observer.main != null) Observer.main.distance = 50f - timer * 9f;
            yield return null;
        }
    }
}
#endif
