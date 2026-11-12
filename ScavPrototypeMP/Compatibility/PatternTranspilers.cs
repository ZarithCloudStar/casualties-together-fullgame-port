using CasualtiesTogetherUtils;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace Together;

// Validate every anchor before changing IL. A new game layout must leave the
// original method intact, including its labels and exception boundaries.
internal static class PatternTranspilers {
    static List<CodeInstruction> Skip(List<CodeInstruction> codes, string method, string pattern) {
        log.warn($"TRANSPILER: {method} safely skipped: missing/ambiguous {pattern} ({(Util.GameIsFull() ? "fullgame" : "demo")})");
        return codes;
    }

    static bool Field(CodeInstruction code, Type owner, string name) =>
        code.opcode == OpCodes.Ldfld && code.operand is FieldInfo field && field.DeclaringType == owner && field.Name == name;

    static bool Call(CodeInstruction code, Type owner, string name) =>
        (code.opcode == OpCodes.Call || code.opcode == OpCodes.Callvirt) &&
        code.operand is MethodInfo method && method.DeclaringType == owner && method.Name == name;

    static bool Brfalse(CodeInstruction code) => code.opcode == OpCodes.Brfalse || code.opcode == OpCodes.Brfalse_S;
    static bool Brtrue(CodeInstruction code) => code.opcode == OpCodes.Brtrue || code.opcode == OpCodes.Brtrue_S;

    // Recover locals from the producer, never from the demo's local slot numbers.
    static CodeInstruction LoadStoredLocal(CodeInstruction store) {
        if (store.opcode == OpCodes.Stloc_0) return new CodeInstruction(OpCodes.Ldloc_0);
        if (store.opcode == OpCodes.Stloc_1) return new CodeInstruction(OpCodes.Ldloc_1);
        if (store.opcode == OpCodes.Stloc_2) return new CodeInstruction(OpCodes.Ldloc_2);
        if (store.opcode == OpCodes.Stloc_3) return new CodeInstruction(OpCodes.Ldloc_3);
        if (store.opcode == OpCodes.Stloc || store.opcode == OpCodes.Stloc_S)
            return new CodeInstruction(OpCodes.Ldloc, store.operand);
        return null;
    }

    static bool PlainRange(List<CodeInstruction> codes, int start, int end) {
        for (int i = start; i < end; i++)
            if (codes[i].blocks.Count != 0 || (i > start && codes[i].labels.Count != 0)) return false;
        return true;
    }

    internal static IEnumerable<CodeInstruction> BuildingUpdate(IEnumerable<CodeInstruction> instructions) {
        var codes = new List<CodeInstruction>(instructions);
        var anchors = new List<int>();
        for (int i = 1; i + 1 < codes.Count; i++)
            if (codes[i - 1].opcode == OpCodes.Ldarg_0 && Field(codes[i], typeof(BuildingEntity), "ignoreBodyOptimize") && Brtrue(codes[i + 1]))
                anchors.Add(i);
        if (anchors.Count != 1) return Skip(codes, "BuildingEntity.Update", "ignoreBodyOptimize branch");
        int field = anchors[0];
        if (!(codes[field + 1].operand is Label end)) return Skip(codes, "BuildingEntity.Update", "optimizer end label");
        int endIndex = codes.FindIndex(field + 2, c => c.labels.Contains(end));
        if (endIndex < 0 || !codes.GetRange(field + 2, endIndex - field - 2).Exists(c => Call(c, typeof(Rigidbody2D), "set_bodyType")))
            return Skip(codes, "BuildingEntity.Update", "rigidbody optimizer scope");

        int distanceStart = -1, distanceStore = -1;
        if (!Util.GameIsFull()) {
            // Demo Update also contains death/drop handling. Keep it, only make
            // its drop-distance flag true as before. Full 1.0 moved it elsewhere.
            int sound = codes.FindIndex(endIndex, c => c.opcode == OpCodes.Ldstr && (string)c.operand == "footstep/Rock/11");
            if (sound >= 0) distanceStore = codes.FindIndex(sound, c => LoadStoredLocal(c) != null);
            if (distanceStore >= 0)
                for (int i = distanceStore - 1; i > sound; i--)
                    if (codes[i].opcode == OpCodes.Ldarg_0) { distanceStart = i; break; }
            if (distanceStart < 0 || !PlainRange(codes, distanceStart, distanceStore))
                return Skip(codes, "BuildingEntity.Update", "demo drop-distance expression");
        }

        // Force the existing skip branch. No removal of branch targets or locals.
        codes[field - 1].opcode = OpCodes.Ldc_I4_1;
        codes[field].opcode = OpCodes.Nop;
        codes[field].operand = null;
        if (distanceStart >= 0) {
            codes[distanceStart].opcode = OpCodes.Ldc_I4_1;
            for (int i = distanceStart + 1; i < distanceStore; i++) { codes[i].opcode = OpCodes.Nop; codes[i].operand = null; }
        }
        log.l("TRANSPILER: BuildingEntity.Update applied (optimizer delegated to multiplayer Postfix)");
        return codes;
    }

    internal static IEnumerable<CodeInstruction> BodyAttack(IEnumerable<CodeInstruction> instructions, ILGenerator il) {
        var codes = new List<CodeInstruction>(instructions);
        if (!Util.GameIsFull()) return DemoAttack(codes, il);

        // Full 1.0 has two hit loops (physical BoxCastAll, then RaycastAll).
        // Each loop stores an array element, tests piercing after setting didHit,
        // and has an existing conditional branch to its increment/continue block.
        var edits = new List<(int insert, CodeInstruction hit, CodeInstruction store, Label next, Label end)>();
        for (int i = 0; i + 2 < codes.Count; i++) {
            if (codes[i].opcode != OpCodes.Ldelem || !Equals(codes[i].operand, typeof(RaycastHit2D))) continue;
            var hit = LoadStoredLocal(codes[i + 1]);
            if (hit == null) return Skip(codes, "Body.Attack", "ray hit local store");
            int nextLoop = codes.FindIndex(i + 2, c => c.opcode == OpCodes.Ldelem && Equals(c.operand, typeof(RaycastHit2D)));
            int limit = nextLoop < 0 ? codes.Count : nextLoop;
            int piercing = codes.FindIndex(i + 2, limit - i - 2, c => Field(c, typeof(AttackInfo), "piercing"));
            int next = codes.FindIndex(i + 2, limit - i - 2, c => Brfalse(c) || Brtrue(c));
            if (piercing < i + 5 || piercing + 1 >= limit || next < 0 ||
                codes[piercing - 3].opcode != OpCodes.Ldc_I4_1 || LoadStoredLocal(codes[piercing - 2]) == null ||
                codes[piercing - 1].opcode != OpCodes.Ldarg_1 || !Brfalse(codes[piercing + 1]) ||
                !(codes[piercing + 1].operand is Label end) || !(codes[next].operand is Label cont))
                return Skip(codes, "Body.Attack", "hit/piercing/continue sequence");
            int endIndex = codes.FindIndex(piercing + 2, c => c.labels.Contains(end));
            int nextIndex = codes.FindIndex(piercing + 2, c => c.labels.Contains(cont));
            if (nextIndex < 0 || endIndex <= nextIndex || endIndex >= limit ||
                !PlainRange(codes, i + 2, i + 3)) return Skip(codes, "Body.Attack", "loop branch targets");
            edits.Add((i + 2, hit, new CodeInstruction(codes[piercing - 2]), cont, end));
        }
        if (edits.Count != 2) return Skip(codes, "Body.Attack", "two fullgame ray hit loops");
        var handler = AccessTools.Method(typeof(Body_Attack_MultiplayerPatch), nameof(Body_Attack_MultiplayerPatch.BodyAttack_DidHitSomething));
        var breakFlag = AccessTools.Field(typeof(Body_Attack_MultiplayerPatch), nameof(Body_Attack_MultiplayerPatch.breakTheAttackLoop));
        for (int i = edits.Count - 1; i >= 0; i--) {
            var edit = edits[i];
            var original = codes[edit.insert];
            var resume = il.DefineLabel();
            edit.hit.MoveLabelsFrom(original);
            original.labels.Add(resume);
            // On a handled hit, preserve didHit for return value/stamina/effects.
            codes.InsertRange(edit.insert, new[] {
                edit.hit, new CodeInstruction(OpCodes.Call, handler), new CodeInstruction(OpCodes.Brfalse, resume),
                new CodeInstruction(OpCodes.Ldc_I4_1), edit.store,
                new CodeInstruction(OpCodes.Ldsfld, breakFlag), new CodeInstruction(OpCodes.Brtrue, edit.end),
                new CodeInstruction(OpCodes.Br, edit.next)
            });
        }
        log.l("TRANSPILER: Body.Attack applied (2 fullgame hit loops)");
        return codes;
    }

    static IEnumerable<CodeInstruction> DemoAttack(List<CodeInstruction> codes, ILGenerator il) {
        int branch = -1;
        for (int i = 5; i < codes.Count; i++) {
            if (Call(codes[i - 5], typeof(Body), "CreateCloudSmall") && codes[i - 4].opcode == OpCodes.Ldc_I4_1 &&
                LoadStoredLocal(codes[i - 3]) != null && codes[i - 2].opcode == OpCodes.Ldarg_1 &&
                Field(codes[i - 1], typeof(AttackInfo), "piercing") && Brfalse(codes[i])) {
                if (branch >= 0) return Skip(codes, "Body.Attack", "unique demo block-hit sequence");
                branch = i;
            }
        }
        if (branch < 0 || branch + 1 >= codes.Count) return Skip(codes, "Body.Attack", "demo block-hit sequence");
        int next = codes.FindIndex(branch + 1, c => Brtrue(c) || Brfalse(c));
        int ray = codes.FindIndex(branch + 1, c => c.opcode == OpCodes.Ldloca || c.opcode == OpCodes.Ldloca_S);
        if (next < 0 || ray < 0 || !(codes[next].operand is Label) || !(codes[branch].operand is Label))
            return Skip(codes, "Body.Attack", "demo ray local/continue labels");
        var first = new CodeInstruction(OpCodes.Ldloc, codes[ray].operand);
        first.MoveLabelsFrom(codes[branch + 1]);
        var resume = il.DefineLabel();
        codes[branch + 1].labels.Add(resume);
        codes.InsertRange(branch + 1, new[] {
            first, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Body_Attack_MultiplayerPatch), nameof(Body_Attack_MultiplayerPatch.BodyAttack_DidHitSomething))),
            new CodeInstruction(OpCodes.Brfalse, resume),
            new CodeInstruction(OpCodes.Ldsfld, AccessTools.Field(typeof(Body_Attack_MultiplayerPatch), nameof(Body_Attack_MultiplayerPatch.breakTheAttackLoop))),
            new CodeInstruction(OpCodes.Brfalse, codes[next].operand), new CodeInstruction(OpCodes.Br, codes[branch].operand)
        });
        log.l("TRANSPILER: Body.Attack applied (demo)");
        return codes;
    }

    internal static IEnumerable<CodeInstruction> CrateCollision(IEnumerable<CodeInstruction> instructions, ILGenerator il) {
        var codes = new List<CodeInstruction>(instructions);
        int limbCall = codes.FindIndex(c => Call(c, typeof(Body), "LimbFromObject"));
        if (limbCall < 0 || limbCall + 1 >= codes.Count ||
            codes.FindIndex(limbCall + 1, c => Call(c, typeof(Body), "LimbFromObject")) >= 0)
            return Skip(codes, "DamagingCrate.OnCollisionEnter2D", "unique LimbFromObject call");
        var limb = LoadStoredLocal(codes[limbCall + 1]);
        int type = codes.FindIndex(limbCall + 2, c => Field(c, typeof(DamagingCrate), "type"));
        var speed = AccessTools.Field(typeof(DamagingCrate), "prevFrameSpeed");
        if (limb == null || type < 1 || codes[type - 1].opcode != OpCodes.Ldarg_0 || speed == null)
            return Skip(codes, "DamagingCrate.OnCollisionEnter2D", "limb local/damage type scope");
        if (Util.GameIsFull() && !codes.GetRange(limbCall + 2, type - limbCall - 2).Exists(c => Call(c, typeof(Collision2D), "get_relativeVelocity")))
            return Skip(codes, "DamagingCrate.OnCollisionEnter2D", "fullgame relative-velocity guard");
        int insert = type - 1;
        if (!PlainRange(codes, insert, insert + 1)) return Skip(codes, "DamagingCrate.OnCollisionEnter2D", "plain damage entry");
        var first = new CodeInstruction(OpCodes.Ldarg_0);
        first.MoveLabelsFrom(codes[insert]);
        var resume = il.DefineLabel();
        codes[insert].labels.Add(resume);
        codes.InsertRange(insert, new[] {
            first, limb, new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldfld, speed),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ScavTraps), nameof(ScavTraps.DamagingCrate_OnCollisionEnter2D_StolenWithFirstCheck))),
            new CodeInstruction(OpCodes.Brfalse, resume), new CodeInstruction(OpCodes.Ret)
        });
        log.l("TRANSPILER: DamagingCrate.OnCollisionEnter2D applied (limb local inferred from producer)");
        return codes;
    }
}
