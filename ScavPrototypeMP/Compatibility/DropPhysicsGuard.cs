using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Together;

// When an item is freed from a container or a hand, vanilla can leave its
// Collider2D disabled. The item then falls through the floor while the network
// tries to replicate it. A free item must get its own colliders back; other
// loose items in the same pile are not solid geometry.
internal static class DropPhysicsGuard {
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

    // Bag/container unload does not go through Body.DropItem. It leaked the
    // exact same disabled-collider state until this was hooked here.
    [HarmonyPatch(typeof(Container), nameof(Container.UnloadItem))]
    internal static class Container_UnloadItem_GuardPatch {
        static void Postfix(Item item) => Guard(item, "Container.UnloadItem", true);
    }

    // ContainerBroke and RecipeItem.UseItem both end up in UnloadAllItems.
    [HarmonyPatch(typeof(Container), nameof(Container.UnloadAllItems))]
    internal static class Container_UnloadAllItems_GuardPatch {
        static void Prefix(Container __instance, out List<Item> __state) {
            __state = new List<Item>();
            if (__instance == null) return;
            foreach (Transform child in __instance.transform) {
                Item item = child.GetComponent<Item>();
                if (item != null) __state.Add(item);
            }
        }
        static void Postfix(List<Item> __state) {
            if (__state == null) return;
            for (int i = 0; i < __state.Count; i++) {
                Item item = __state[i];
                // UnloadAllItems does not call HeldSeat.Release. Without this the
                // HeldQuiet component remains and its later OnDestroy restores the
                // colliders to the saved off state.
                HeldSeat.Release(item);
                Guard(item, "Container.UnloadAllItems", true);
            }
        }
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
            Collider2D[] hits = Physics2D.OverlapCircleAll(pos, 0.12f);
            int ground = LayerMask.NameToLayer("Ground");
            for (int i = 0; i < hits.Length; i++) {
                Collider2D hit = hits[i];
                if (hit == null || hit.isTrigger || hit.transform == null) continue;
                // Ground itself is where a loose item is supposed to rest.
                if (hit.gameObject.layer == ground) continue;
                if (hit.transform == item.transform || hit.transform.IsChildOf(item.transform)) continue;
                // Another loose item is not a wall. This used to make the second
                // item in a pile skip the repair and fall through the floor.
                if (hit.GetComponentInParent<Item>() != null) continue;
                return true;
            }
            return false;
        } catch { return true; }
    }

    internal static void Guard(Item item, string reason, bool force = false) {
        if (!Net.IsRunning || item == null) return;
        try {
            if (!IsFree(item)) return;
            if (!force && OverlappingSolid(item)) return;
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
