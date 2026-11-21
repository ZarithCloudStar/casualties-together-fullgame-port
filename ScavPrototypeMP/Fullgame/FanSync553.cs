using CasualtiesTogetherUtils;
using HarmonyLib;
using UnityEngine;
using Unity.VisualScripting;

namespace Together;

internal sealed class FanClientFollow : MonoBehaviour {
    internal float HostSpin = -1f;
    internal bool NotedDead;
}

internal static partial class FanTrap_OnWillRenderObject_HostSim_MultiplayerPatch {
    internal static void Write(FanTrap fan, SyncInfo si, bool force = false) {
        if (fan == null || si == null) return;
        int broken = (fan.fan1 != null && !fan.fan1.gameObject.activeSelf ? 1 : 0)
            | (fan.fan2 != null && !fan.fan2.gameObject.activeSelf ? 2 : 0);
        byte raw = (byte)(broken | (Mathf.Clamp(Mathf.RoundToInt(fan.speedMult * 50f), 0, 63) << 2));
        float packed = Mathf.Clamp(Mathf.RoundToInt(fan.spinSpeed * 100f), 0, 2000)
            + Mathf.Clamp(Mathf.RoundToInt(fan.desiredSpinSpeed * 100f), 0, 2000) * 4096f;
        if (force || si.specialdata1.Value != raw) si.specialdata1.Value = raw;
        if (force || Mathf.Abs(si.specialdata2.Value - packed) > 4f) si.specialdata2.Value = packed;
    }
    internal static void Push(FanTrap fan, bool force = false) {
        if (Net.IsServer && fan != null && NetObjectRegistry.TryGetSyncInfo(fan.gameObject, out var si)) Write(fan, si, force);
    }
    internal static bool Pull(FanTrap fan) {
        return fan != null && !Net.IsServer && NetObjectRegistry.TryGetSyncInfo(fan.gameObject, out var si)
            && si != null && Apply(fan, si.specialdata1.Value, si.specialdata2.Value);
    }
    internal static bool Apply(FanTrap fan, byte raw, float packed) {
        if (fan == null) return false;
        int broken = raw & 3, speed = (raw >> 2) & 63;
        if (speed == 0 && broken == 0 && packed < 1f) return false; // not initialized yet
        int desired = Mathf.FloorToInt(packed / 4096f);
        float spin = Mathf.Max(0, Mathf.RoundToInt(packed - desired * 4096f)) / 100f;
        fan.speedMult = speed / 50f;
        fan.desiredSpinSpeed = desired / 100f;
        var follow = fan.GetOrAddComponent<FanClientFollow>();
        bool announce = follow.HostSpin >= 0f;
        if (follow.HostSpin < 0f || Mathf.Abs(follow.HostSpin - spin) > 0.04f) {
            fan.spinSpeed = spin;
            follow.HostSpin = spin;
        }
        Mirror(fan.fan1, (broken & 1) != 0, fan.transform.position, announce);
        Mirror(fan.fan2, (broken & 2) != 0, fan.transform.position, announce);
        if (fan.speedMult <= 0f && !follow.NotedDead) {
            follow.NotedDead = true;
            var build = fan.GetComponent<BuildingEntity>();
            if (build != null) build.description = Locale.GetBuilding("fandsc2");
        }
        return true;
    }
    static void Mirror(SpriteRenderer blade, bool broken, Vector3 pos, bool announce) {
        if (blade == null) return;
        if (broken && blade.gameObject.activeSelf) {
            BuildingEntity.CreateBreakParticle(blade);
            blade.gameObject.SetActive(false);
            if (announce) Sound.Play("fansnap", pos);
        } else if (!broken && !blade.gameObject.activeSelf) blade.gameObject.SetActive(true);
    }
    static void DrawBlades(FanTrap fan) {
        if (fan == null) return;
        Pull(fan);
        if (fan.speedMult <= 0f) { if (fan.src != null) fan.src.Stop(); return; }
        for (int i = 0; i < 2; i++) {
            var blade = i == 0 ? fan.fan1 : fan.fan2;
            if (blade == null || !blade.gameObject.activeSelf) continue;
            float side = fan.SinForSpin(fan.fanProgress + i / 2f);
            float depth = fan.DepthForSpin(fan.fanProgress + i / 2f);
            blade.transform.localPosition = Vector3.right * 3.375f * side;
            float scale = Mathf.Abs(side) <= 0.07f ? Mathf.Sign(side) * 0.07f : side;
            blade.transform.localScale = new Vector3(scale, 1f + depth * 0.25f, 1f);
            blade.sortingOrder = depth > 0f ? 500 : -500;
            float light = Mathf.Lerp(0.4f, 1f, Mathf.InverseLerp(-1f, 1f, depth));
            blade.color = new Color(light, light, light);
            blade.flipY = side < 0f;
            bool before = fan.front != null && i < fan.front.Length && fan.front[i];
            bool front = depth > 0f;
            if (fan.front != null && i < fan.front.Length) fan.front[i] = front;
            if (before != front) ShoveLocal(fan, blade);
        }
        if (fan.src != null) {
            fan.src.pitch = fan.spinSpeed * 0.62f * fan.speedMult;
            fan.src.volume = Mathf.Clamp01(fan.spinSpeed * 0.25f) * 0.66f;
        }
    }
    static void ShoveLocal(FanTrap fan, SpriteRenderer blade) {
        Vector2 direction = blade.transform.right.normalized * blade.transform.localScale.x;
        if (direction == Vector2.zero) direction = Vector2.right;
        foreach (var hit in Physics2D.RaycastAll(fan.transform.position, direction, 6.5f, LayerMask.GetMask("Body", "Limb"))) {
            if (hit.collider == null || !Util.TryGetBodyFromObject(hit.collider, out var body) || !body.IsBodyLocal()) continue;
            body.DoGoreSound();
            if (fan.spinSpeed >= 2f) body.Ragdoll();
            else {
                body.crouchAmount += 0.3f;
                body.SetVelocity(direction * fan.spinSpeed * 7.5f);
                body.liquidSlipTime += 0.35f;
            }
            Limb limb = body.GetClosestLimb(hit.point);
            if (limb != null && limb.rb != null) {
                limb.rb.velocity = direction * fan.spinSpeed * 5f;
                if (limb.connectedLimbs != null) foreach (var connected in limb.connectedLimbs)
                    if (connected != null && connected.rb != null) connected.rb.velocity = direction * fan.spinSpeed * 5f;
                // Apply the extra shove once to each limb rather than repeatedly accelerating the closest limb.
                if (body.limbs != null) foreach (var part in body.limbs)
                    if (part != null && part.rb != null) part.rb.velocity += direction * fan.spinSpeed * 2.5f;
            }
            if (fan.spinSpeed > 2.25f) Sound.Play("loudstab", hit.point);
            break;
        }
    }
}

[HarmonyPatch(typeof(FanTrap), "Start")]
internal static class FanTrap_Start_PublishSpeed_MultiplayerPatch {
    static void Postfix(FanTrap __instance) {
        if (!Net.IsRunning || __instance == null) return;
        if (Net.IsServer) FanTrap_OnWillRenderObject_HostSim_MultiplayerPatch.Push(__instance, true);
        else if (!FanTrap_OnWillRenderObject_HostSim_MultiplayerPatch.Pull(__instance)) {
            __instance.spinSpeed = 0f; __instance.desiredSpinSpeed = 0f;
        }
    }
}
[HarmonyPatch(typeof(FanTrap), "Update")]
internal static class FanTrap_Update_SpeedFollow_MultiplayerPatch {
    static void Prefix(FanTrap __instance) { if (Net.IsClient) FanTrap_OnWillRenderObject_HostSim_MultiplayerPatch.Pull(__instance); }
    static void Postfix(FanTrap __instance) { if (Net.IsServer) FanTrap_OnWillRenderObject_HostSim_MultiplayerPatch.Push(__instance); }
}
[HarmonyPatch(typeof(FanTrap), "BreakFan")]
internal static class FanTrap_BreakFan_Publish_MultiplayerPatch {
    static void Postfix(FanTrap __instance) { if (Net.IsServer) FanTrap_OnWillRenderObject_HostSim_MultiplayerPatch.Push(__instance, true); }
}
