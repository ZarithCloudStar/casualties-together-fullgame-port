#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace Together;

// Fullgame moved the loot producer from Update to CheckDamage. Patching only
// FreshItemDrop cannot help when the host-only distance check never adds it.
[HarmonyPatch(typeof(BuildingEntity), "CheckDamage")]
internal static class FreshDropRelevance {
    static bool Prepare() => Util.GameIsFull();

    // A remote player may legitimately finish a capsule keypad farther than
    // the native eight-metre loot radius (the keypad's reach is normally 14m).
    // Scope this exception to the validated opening and its own spawned loot.
    static Vector2? validatedCapsuleSource;
    static readonly HashSet<FreshItemDrop> validatedCapsuleDrops = new();

    internal static bool IsValidatedCapsuleDrop(Vector2 position) =>
        validatedCapsuleSource.HasValue && (position - validatedCapsuleSource.Value).sqrMagnitude < .01f;

    internal static void OpenValidatedCapsule(BuildingEntity source, ScavPlayer opener) {
        if (!Net.IsRunning || !Net.IsServer || source.id != "dropcapsule") {
            source.SetHealth(0f);
            return;
        }
        var previous = validatedCapsuleSource;
        validatedCapsuleSource = source.transform.position;
        try {
            if (log.verbose)
                log.l($"[drop] validated capsule open player={opener.playerId} source={validatedCapsuleSource.Value} openerDistance={Vector2.Distance(source.transform.position, opener.body.transform.position):0.00}");
            source.SetHealth(0f);
        } finally {
            validatedCapsuleSource = previous;
        }
    }

    internal static void MarkValidatedCapsuleDrop(FreshItemDrop drop) {
        if (Net.IsRunning && Net.IsServer && IsValidatedCapsuleDrop(drop.transform.position))
            validatedCapsuleDrops.Add(drop);
    }

    internal static bool TakeValidatedCapsuleDrop(FreshItemDrop drop) => validatedCapsuleDrops.Remove(drop);

    static float DistanceToPlayer(Vector2 source, Vector2 vanillaPlayer) {
        if (!Net.IsRunning || !Net.IsServer) return Vector2.Distance(source, vanillaPlayer);
        if (IsValidatedCapsuleDrop(source)) return 0f;
        var nearest = ScavPlayer.GetDistanceToNearestLivingPlayer(source);
        float distance = Mathf.Sqrt(nearest.Item2);
        if (log.verbose) log.l($"[drop] source={source} nearestLiving={distance:0.00} hostDistance={Vector2.Distance(source, vanillaPlayer):0.00}");
        return distance;
    }

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        var codes = instructions.ToList();
        var distance = AccessTools.Method(typeof(Vector2), nameof(Vector2.Distance), new[] { typeof(Vector2), typeof(Vector2) });
        var matches = codes.Select((c, i) => (c, i)).Where(p => p.c.Calls(distance)).Select(p => p.i).ToArray();
        // Keep the native '< 8' condition and every loot branch. Validate the
        // unique call, threshold and host-body dependency before replacing it.
        if (matches.Length != 1 || matches[0] + 2 >= codes.Count ||
            codes[matches[0] + 1].opcode != OpCodes.Ldc_R4 ||
            !(codes[matches[0] + 1].operand is float radius) || radius != 8f ||
            codes[matches[0] + 2].opcode != OpCodes.Clt ||
            !codes.Take(matches[0]).Any(c => c.operand is FieldInfo field && field.DeclaringType == typeof(PlayerCamera) && field.Name == "body")) {
            log.error("[drop] CheckDamage distance patch skipped: unexpected loot-distance IL; remote drops may not float");
            return codes;
        }
        codes[matches[0]].operand = AccessTools.Method(typeof(FreshDropRelevance), nameof(DistanceToPlayer));
        log.l("[drop] CheckDamage uses nearest living player (native radius/lifetime preserved)");
        return codes;
    }
}
#endif
