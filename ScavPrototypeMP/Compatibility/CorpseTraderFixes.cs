#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Unity.VisualScripting;
using UnityEngine;

namespace Together;

internal static class CorpseCombat {
    internal static bool IsHumanCorpse(BuildingEntity build) =>
        build != null && build.TryGetComponent<CorpseScript>(out var corpse) && !corpse.animalCorpse;

    internal static bool CanHit(BuildingEntity build, Body attacker) =>
        !IsHumanCorpse(build) || (attacker != null && attacker.charType == 2);
}

// Per-instance bookkeeping only. Health/skills still use the existing server snapshots.
internal sealed class TraderMoodState : MonoBehaviour {
    internal Body killer;
    internal bool deathApplied, threatApplied;
}

internal static class TraderMoodEffects {
    // Threat and death use the same witness range on both host and guest.
    const float WitnessRadius = 20f;
    const string OcclusionLayer = "Ground";
    const float VisibilityWarningInterval = 30f;
    static float nextVisibilityWarningTime;

    internal static void RecordHit(BuildingEntity build, Body attacker, float nextHealth) {
        if (!Net.IsRunning || build == null || attacker == null ||
            build.GetHealth() < 200f || nextHealth >= 200f || !build.TryGetComponent<TraderScript>(out var trader)) return;
        trader.GetOrAddComponent<TraderMoodState>().killer = attacker;
    }

    static bool Witness(TraderScript trader, Body body) =>
        body != null && body.alive && body.conscious &&
        ((Vector2)(body.transform.position - trader.transform.position)).sqrMagnitude < WitnessRadius * WitnessRadius &&
        HasLineOfSight(trader, body);

    static bool HasLineOfSight(TraderScript trader, Body body) {
        try {
            int mask = LayerMask.GetMask(OcclusionLayer);
            if (mask == 0) throw new InvalidOperationException("Ground collision layer is missing.");

            // Same Ground-layer approach as Util.QuickGroundLinecastCheck and
            // SoundCannon.CanSeePos. A filtered query is needed here: those
            // helpers also count triggers and cannot ignore the trader itself.
            // WorldGeneration creates terrain Tilemap/CompositeCollider2D on
            // layer 6 (Ground); Body/Limb/Item/Descriptor are not queried.
            var filter = new ContactFilter2D();
            filter.SetLayerMask(mask);
            filter.useTriggers = false;
            var hits = new List<RaycastHit2D>();
            Physics2D.Linecast(body.transform.position, trader.transform.position, filter, hits);
            foreach (var hit in hits) {
                var collider = hit.collider;
                if (collider == null || collider.isTrigger) continue;
                var t = collider.transform;
                if (t.IsChildOf(body.transform) || t.IsChildOf(trader.transform)) continue;
                // Also exclude ragdolls, corpses and dropped items even if a
                // prefab happens to put one of their colliders on Ground.
                if (collider.GetComponentInParent<Body>() != null ||
                    collider.GetComponentInParent<Limb>() != null ||
                    collider.GetComponentInParent<Item>() != null ||
                    collider.GetComponentInParent<CorpseScript>() != null ||
                    collider.GetComponentInParent<TraderScript>() != null) continue;
                return false;
            }
            return true;
        } catch (Exception ex) {
            float now = Time.realtimeSinceStartup;
            if (now >= nextVisibilityWarningTime) {
                nextVisibilityWarningTime = now + VisibilityWarningInterval;
                log.warn($"[trader-mood] visibility check failed; treating trader as visible: {ex.GetType().Name}: {ex.Message}");
            }
            return true;
        }
    }

    internal static void Threat(TraderScript trader) {
        if (!Net.IsRunning) { PlayerCamera.main.body.happiness -= 2f; return; }
        if (!Net.IsServer) return;
        var state = trader.GetOrAddComponent<TraderMoodState>();
        if (state.threatApplied) return;
        state.threatApplied = true;
        foreach (var player in ScavPlayer.AllLivingPlayers) {
            var body = player.body;
            if (!Witness(trader, body)) continue;
            body.happiness -= 2f;
            MedicalSync.Server_QueueSendCharacterHealth(player.netBody, true);
            log.l($"[trader-mood] threat player={player.playerName} delta=-2 distance={Vector2.Distance(body.transform.position, trader.transform.position):0.##}");
        }
    }

    internal static void Death(TraderScript trader) {
        if (!Net.IsRunning) {
            PlayerCamera.main.body.skills.AddExp(1, 60f);
            ApplyDeath(PlayerCamera.main.body, true);
            return;
        }
        var state = trader.GetOrAddComponent<TraderMoodState>();
        if (state.deathApplied) return;
        state.deathApplied = true;
        if (Net.IsServer) {
            // The stock body getter is the camera body, even when forced to tick
            // for a distant guest. Never use it (or focused_body) as the victim.
            foreach (var player in ScavPlayer.AllLivingPlayers) {
                var body = player.body;
                bool killer = body == state.killer;
                bool witness = Witness(trader, body);
                if (!killer && !witness) continue;
                // XP belongs to the recorded killer independently of visibility.
                // Mood, cry and dialogue require witnessing, including for killers.
                if (killer) body.skills.AddExp(1, 60f);
                if (witness) ApplyDeath(body, true);
                MedicalSync.Server_QueueSendCharacterHealth(player.netBody, true);
                log.l($"[trader-mood] death player={player.playerName} killer={killer} witness={witness} dune={body.charType == 2} distance={Vector2.Distance(body.transform.position, trader.transform.position):0.##}");
            }
        } else {
            // Cry is cosmetic and is absent from the health packet. Numerical
            // penalties and XP arrive from the host; do not subtract them again.
            Body local = Util.GetLocalBody();
            if (Witness(trader, local)) ApplyDeath(local, false);
        }
    }

    static void ApplyDeath(Body body, bool numbers) {
        if (body == null) return;
        if (body.charType != 2) {
            if (numbers) {
                body.happiness -= 10f;
                body.traumaAmount += 30f;
                body.sicknessAmount += 30f * (body.mindWipe ? 0.5f : 1f);
            }
            body.StartCoroutine(body.Cry());
        }
        body.talker.Talk(Locale.GetCharacter("murderregret"));
    }

    // Replace only the stock personal effects. Inventory, the didDeath latch,
    // death visuals, description and trader AI remain stock.
    internal static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions) {
        var codes = instructions.ToList();
        bool Call(CodeInstruction c, string name) => c.operand is MethodInfo m && m.Name == name &&
            (c.opcode == OpCodes.Call || c.opcode == OpCodes.Callvirt);
        bool BodyLoad(int i) => i >= 0 && i + 1 < codes.Count && codes[i].opcode == OpCodes.Ldarg_0 && Call(codes[i + 1], "get_body");
        int exp = codes.FindIndex(c => Call(c, "AddExp"));
        int deathStart = -1;
        for (int i = exp - 1; i >= 0; i--) if (BodyLoad(i)) { deathStart = i; break; }
        int description = codes.FindIndex(c => c.opcode == OpCodes.Ldstr && Equals(c.operand, "traderdscdead"));
        int deathEnd = description - 2;
        int threatEnd = codes.FindIndex(c => c.opcode == OpCodes.Stfld && c.operand is FieldInfo f && f.Name == "happiness");
        int threatStart = -1;
        for (int i = threatEnd - 1; i >= 0; i--) if (BodyLoad(i)) { threatStart = i; break; }
        if (deathStart < 0 || deathEnd <= deathStart || threatStart < 0 || threatEnd <= threatStart ||
            codes[deathEnd].opcode != OpCodes.Ldarg_0 ||
            !(codes[deathEnd + 1].opcode == OpCodes.Ldfld && codes[deathEnd + 1].operand is FieldInfo build && build.Name == "build") ||
            !codes.Skip(deathStart).Take(deathEnd - deathStart).Any(c => c.opcode == OpCodes.Ldstr && Equals(c.operand, "murderregret")) ||
            !codes.Skip(threatStart).Take(threatEnd - threatStart).Any(c => c.opcode == OpCodes.Ldc_R4 && Equals(c.operand, 2f)))
            throw new InvalidOperationException("Trader mood IL anchors changed; refusing an unscoped personal penalty patch.");
        void Replace(int start, int end, string method) {
            for (int i = start; i < end; i++) { codes[i].opcode = OpCodes.Nop; codes[i].operand = null; }
            codes[start].opcode = OpCodes.Ldarg_0;
            codes[start + 1].opcode = OpCodes.Call;
            codes[start + 1].operand = AccessTools.Method(typeof(TraderMoodEffects), method);
        }
        Replace(deathStart, deathEnd, nameof(Death));
        Replace(threatStart, threatEnd + 1, nameof(Threat));
        log.l("TRANSPILER: TraderScript.OnWillRenderObject scoped threat/death effects applied");
        return codes;
    }
}
#endif
