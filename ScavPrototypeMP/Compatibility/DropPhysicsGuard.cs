using System;
using HarmonyLib;
using UnityEngine;

namespace Together;

// When a player drops an item, vanilla can leave its Collider2D disabled. The
// item then falls through the floor while the network tries to replicate it.
// Re-enable only a free item's own disabled colliders, and only when it is not
// already embedded in a wall/floor. Never move it or change its Rigidbody2D.
internal static class DropPhysicsGuard {
    static readonly Collider2D[] Buffer = new Collider2D[32];

    [HarmonyPatch(typeof(Body), "DropItem", new[] { typeof(int) })]
    internal static class Body_DropItem_GuardPatch {
        static void Prefix(Body __instance, int slot, out Item __state) {
            __state = null;
            if (!Net.IsRunning || __instance == null) return;
            try { __state = __instance.GetItem(slot); } catch { __state = null; }
        }
        static void Postfix(Item __state) => Guard(__state, "DropItem(int)");
    }

    [HarmonyPatch(typeof(Body), "DropItem", new[] { typeof(Item) })]
    internal static class Body_DropItemObject_GuardPatch {
        static void Postfix(Item item) => Guard(item, "DropItem(Item)");
    }

    [HarmonyPatch(typeof(Body), "DropWearable", new[] { typeof(Item) })]
    internal static class Body_DropWearable_GuardPatch {
        static void Postfix(Item item) => Guard(item, "DropWearable");
    }

    static bool IsFree(Item item) {
        try {
            if (item == null || item.transform == null || item.transform.parent != null) return false;
            if (item.TryGetParentContainer(out var container) && container != null) return false;
            return true;
        } catch { return false; }
    }

    static bool OverlappingSolid(Item item) {
        try {
            Vector2 pos = item.transform.position;
            int count = Physics2D.OverlapCircleNonAlloc(pos, 0.12f, Buffer);
            if (count >= Buffer.Length) return true;
            int ground = LayerMask.NameToLayer("Ground");
            for (int i = 0; i < count; i++) {
                Collider2D hit = Buffer[i];
                Buffer[i] = null;
                if (hit == null || hit.isTrigger || hit.transform == null) continue;
                if (hit.gameObject.layer == ground) continue;
                if (hit.transform == item.transform || hit.transform.IsChildOf(item.transform)) continue;
                return true;
            }
            return false;
        } catch { return true; }
        finally { Array.Clear(Buffer, 0, Buffer.Length); }
    }

    internal static void Guard(Item item, string reason) {
        if (!Net.IsRunning || item == null) return;
        try {
            if (!IsFree(item) || OverlappingSolid(item)) return;
            var colliders = item.GetComponentsInChildren<Collider2D>(true);
            bool fixedAny = false;
            foreach (var collider in colliders) {
                if (collider == null || collider.enabled) continue;
                collider.enabled = true;
                fixedAny = true;
            }
            if (fixedAny && Plugin.Logger != null)
                Plugin.Logger.LogInfo($"[drop] re-enabled collider id={item.id} reason={reason}");
        } catch (Exception ex) {
            Plugin.Logger?.LogWarning("[drop] collider guard failed: " + ex.Message);
        }
    }
}
