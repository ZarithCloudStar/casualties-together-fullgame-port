#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Together;

internal sealed class EndingGuardSave {
    public float x, y, scale, health;
    public bool dead;
    public float? shootTime;
    public bool? hurtByPlayer, noMelee, noShootRandomize;
}

// Guards are BuildingEntity objects: existing registry creation, health and gun
// damage are reused. Only their single-player AI and pose need special handling.
// Remote animation is intentionally a pose approximation; remote AI never fires.
internal static class EndingGuardSync {
    // Optional fields keep the 1.0 whitelist usable with a 1.0.1-built mod.
    static readonly FieldInfo hurtByPlayer = AccessTools.Field(typeof(GuardScript), "hurtByPlayer");
    static readonly FieldInfo noMelee = AccessTools.Field(typeof(GuardScript), "noMelee");
    static readonly FieldInfo noShootRandomize = AccessTools.Field(typeof(GuardScript), "noShootRandomize");
    internal static void PlayerHit(BuildingEntity building, Body attacker) {
        if (!Net.IsServer || attacker == null || !attacker.TryGetNetBody(out var body) || !body.IsPlayer) return;
        if (building.TryGetComponent<GuardScript>(out var guard)) hurtByPlayer?.SetValue(guard, true);
    }
    internal static bool Active => EndingFlow.Active && WorldGeneration.world != null && WorldGeneration.world.biomeDepth == 11;
    internal static Body NearestLivingPlayer(Vector2 position) => ScavPlayer.ClientIdToPlayerDict.Values
        .Where(p => p != null && p.IsAlive())
        .OrderBy(p => (p.pos - position).sqrMagnitude).ThenBy(p => (ushort)p.playerId)
        .Select(p => p.body).FirstOrDefault();
    static float nextSnapshot;
    // Keep the original 30-byte records intact; old receivers ignore the tail.
    const byte VisualsMarker = 0x47, VisualsVersion = 1;
    const int PoseBytes = 30, LaserBytes = 13;
    static float nextProtocolWarning;
    static bool receivedVisualsVersion;
    static readonly HashSet<Body> shotVictims = new();
    static List<EndingGuardSave> pendingRestore;
    internal static bool Restoring => pendingRestore != null;
    internal static void QueueRestore(List<EndingGuardSave> saved) => pendingRestore = saved?.Count > 0 ? saved : null;
    internal static List<EndingGuardSave> Capture() => Object.FindObjectsOfType<GuardScript>().Take(128)
        .Where(g => g.bld != null).Select(g => new EndingGuardSave { x = g.transform.position.x, y = g.transform.position.y,
            scale = g.transform.localScale.x, health = g.bld.health, dead = g.didDie,
            shootTime = g.shootTime, hurtByPlayer = (bool?)hurtByPlayer?.GetValue(g),
            noMelee = (bool?)noMelee?.GetValue(g), noShootRandomize = (bool?)noShootRandomize?.GetValue(g) }).ToList();

    internal static void Tick() {
        if (!Active || !Net.IsServer || Time.unscaledTime < nextSnapshot) return;
        if (pendingRestore != null) {
            var saved = pendingRestore; pendingRestore = null;
            foreach (var guard in Object.FindObjectsOfType<GuardScript>()) Object.Destroy(guard.gameObject);
            foreach (var entry in saved.Take(128)) {
                var go = Utils.Create("guard", new Vector2(entry.x, entry.y), 0f);
                go.transform.localScale = new Vector3(entry.scale, 1, 1);
                var building = go.GetComponent<BuildingEntity>(); building.health = entry.health;
                var restored = go.GetComponent<GuardScript>();
                if (entry.shootTime.HasValue) restored.shootTime = entry.shootTime.Value;
                if (entry.hurtByPlayer.HasValue) hurtByPlayer?.SetValue(restored, entry.hurtByPlayer.Value);
                if (entry.noMelee.HasValue) noMelee?.SetValue(restored, entry.noMelee.Value);
                if (entry.noShootRandomize.HasValue) noShootRandomize?.SetValue(restored, entry.noShootRandomize.Value);
                // Start initializes the death animator/collider; Update then sees
                // health <= 0. Dead guards must never drop another set of loot.
                if (entry.dead) { go.GetComponent<GuardScript>().didDie = true; foreach (var col in go.GetComponents<Collider2D>()) col.enabled = false; }
            }
            log.l($"[ending] restored authoritative lab guards count={saved.Count}");
        }
        nextSnapshot = Time.unscaledTime + .1f;
        var guards = Object.FindObjectsOfType<GuardScript>().Take(128).ToArray();
        var w = Net.CreateWriter(NetmsgId.CLIENT_EndingGuards);
        var entries = new List<(GuardScript guard, SyncInfo info)>();
        foreach (var guard in guards) {
            var si = NetObjectRegistry.Server_EnsureItemIsNetworkRegistered(guard.gameObject);
            if (si != null && guard.bld != null) entries.Add((guard, si));
        }
        w.Put((byte)entries.Count);
        foreach (var entry in entries) {
            var g = entry.guard;
            w.Put(entry.info.syncId); w.Put(entry.info.Version);
            w.Put(g.transform.position.x); w.Put(g.transform.position.y); w.Put(g.transform.localScale.x);
            w.Put(g.arms.eulerAngles.z); w.Put(g.head.eulerAngles.z); w.Put(g.bld.health);
            w.Put(g.didDie); w.Put(g.doingMelee);
            w.Put((sbyte)Array.IndexOf(g.bodyMoveSprites, g.myBody.sprite));
        }
        w.Put(VisualsMarker); w.Put(VisualsVersion); w.Put((byte)entries.Count);
        foreach (var entry in entries) {
            var g = entry.guard;
            w.Put(g.laserLine != null ? g.laserLine.transform.localScale.y : 0f);
            Vector2 dot = g.laserDot != null ? g.laserDot.transform.localPosition : Vector2.zero;
            w.Put(dot.x); w.Put(dot.y);
            byte flags = 0;
            if (g.laserLine != null && g.laserLine.enabled) flags |= 1;
            if (g.laserDot != null && g.laserDot.enabled) flags |= 2;
            if (g.stunned) flags |= 4;
            w.Put(flags);
        }
        Net.Server_SendToClients(DeliveryMethod.Sequenced, w, ServerMain.AllClientIdsExceptHost);
    }
    [ClientReceiver((byte)NetmsgId.CLIENT_EndingGuards)]
    static void Receive(knetid _, NetDataReader r) {
        if (!Net.IsClient || !Active) return;
        if (r.AvailableBytes < 1) return;
        int count = r.GetByte();
        if (count > 128 || r.AvailableBytes < count * PoseBytes) { WarnProtocol(); return; }
        var visuals = new EndingGuardPresentation[count];
        for (int i = 0; i < count; i++) {
            r.Get(out knetid id); byte version = r.GetByte();
            var pos = new Vector2(r.GetFloat(), r.GetFloat()); float scale = r.GetFloat();
            float arms = r.GetFloat(), head = r.GetFloat(), health = r.GetFloat();
            bool dead = r.GetBool(), melee = r.GetBool(); int sprite = r.GetSByte();
            if (!Active || !NetObjectRegistry.TryGetSyncInfo(id, out var si) || si.Version != version || si.go == null ||
                !si.go.TryGetComponent<GuardScript>(out var g)) continue;
            if (!g.TryGetComponent<EndingGuardPresentation>(out var visual))
                visual = g.gameObject.AddComponent<EndingGuardPresentation>();
            visuals[i] = visual;
            visuals[i].ReceivePose(g, pos, scale, arms, head, dead, melee, sprite);
            if (g.bld != null) g.bld.health = health;
        }
        if (r.AvailableBytes == 0) return; // legacy host: pose-only packet
        if (r.AvailableBytes < 3 || r.GetByte() != VisualsMarker || r.GetByte() != VisualsVersion
            || r.GetByte() != count || r.AvailableBytes != count * LaserBytes) { WarnProtocol(); return; }
        if (!receivedVisualsVersion) {
            receivedVisualsVersion = true;
            log.l("[guard-visual] received laser snapshot extension v1; client interpolation active");
        }
        for (int i = 0; i < count; i++) {
            float length = r.GetFloat();
            var dot = new Vector2(r.GetFloat(), r.GetFloat());
            byte flags = r.GetByte();
            if (!Util.IsFinite(length) || !Util.IsFinite(dot.x) || !Util.IsFinite(dot.y) || length < 0f) continue;
            if (visuals[i] != null) visuals[i].ReceiveLaser(length, dot, flags);
        }
    }

    static void WarnProtocol() {
        if (Time.unscaledTime < nextProtocolWarning) return;
        nextProtocolWarning = Time.unscaledTime + 5f;
        log.warn("[guard-visual] malformed or unsupported snapshot extension; keeping the base pose");
    }

    internal static bool ServerShotHit(Body victim) {
        var source = TurretScript_Shoot_MultiplayerPatch.last_fire_info.ignoreTrans;
        if (!Active || !Net.IsServer || source == null || source.GetComponentInParent<GuardScript>() == null) return false;
        shotVictims.Add(victim);
        return true;
    }
    internal static void BeginAttack() => shotVictims.Clear();
    internal static void EndAttack() {
        foreach (var body in shotVictims) {
            if (body == null) continue;
            if (body.TryGetNetBody(out var nb)) MedicalSync.Server_QueueSendCharacterHealth(nb, true);
            if (EndingFlow.KeyPlayer?.body == body && (!body.standing || !body.conscious)) EndingFlow.GuardKnockedDownCarrier();
        }
        shotVictims.Clear();
    }
    internal static IEnumerator Melee(IEnumerator original) {
        bool standing = EndingFlow.KeyPlayer?.body?.standing == true;
        while (original.MoveNext()) {
            var key = EndingFlow.KeyPlayer;
            if (Active && Net.IsServer && standing && key?.body != null && !key.body.standing) {
                EndingFlow.GuardKnockedDownCarrier(); standing = false;
            }
            yield return original.Current;
        }
        if (Net.IsServer) foreach (var p in ScavPlayer.ClientIdToPlayerDict.Values)
            if (p?.body != null && p.body.TryGetNetBody(out var nb)) MedicalSync.Server_QueueSendCharacterHealth(nb, true);
    }
}

internal static partial class EndingFlow {
    internal static void GuardKnockedDownCarrier() {
        if (!Net.IsServer || !DuringEscape || State.guardDown) return;
        State.guardDown = true; State.downSeconds = 0;
        Changed("[escape] guard knocked down cargo carrier; vanilla injury only, failure requires brain dying/death");
    }
}
[HarmonyPatch(typeof(GuardScript), nameof(GuardScript.Die))]
internal static class Ending_GuardDeathPatch {
    static void Postfix() {
        // Persist vanilla's attribution immediately, including saves before Finish.
        // Friendly fire leaves anyGuardKilled false in 1.0.1.
        if (EndingGuardSync.Active && Net.IsServer) EndingFlow.State.guardKilled |= GuardScript.anyGuardKilled;
    }
}
[HarmonyPatch(typeof(GuardScript), "Update")]
internal static class Ending_GuardUpdatePatch { static bool Prefix() => !EndingGuardSync.Active || (Net.IsServer && !EndingFlow.SettlementLocked); }
[HarmonyPatch(typeof(GuardScript), nameof(GuardScript.Fire))]
internal static class Ending_GuardFirePatch {
    static bool Prefix() { if (EndingGuardSync.Active && Net.IsClient) return false; EndingGuardSync.BeginAttack(); return true; }
    static void Postfix(GuardScript __instance, bool __runOriginal) {
        EndingGuardSync.EndAttack();
        if (__runOriginal) GuardShotVisual.ServerSend(__instance);
    }
}
[HarmonyPatch(typeof(GuardScript), "MeleeAttack")]
internal static class Ending_GuardMeleePatch {
    static void Postfix(ref IEnumerator __result) {
        if (EndingGuardSync.Active) __result = Net.IsServer ? EndingGuardSync.Melee(__result) : EndingFlow.Empty();
    }
}
#endif
