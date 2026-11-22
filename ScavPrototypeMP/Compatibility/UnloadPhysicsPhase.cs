#if FULLGAME
using System.Collections.Generic;
using CasualtiesTogetherUtils;
using HarmonyLib;
using UnityEngine;

namespace Together;

// Active items can keep their Collider2D enabled while parented to a belt slot.
// BagSleep deliberately leaves belt contents and light-emitting items active.
// Vanilla then unparents and turns the rigidbody on in the same call, so the
// first physics step sees the collider already overlapping the player/bag.
// Stop colliders and simulation for the unparent/move, then restore them after
// the object is in the world. Sleeping bag contents had colliders off already,
// which is why the same operation looks item-dependent.
internal static class UnloadPhysicsPhase {
    internal sealed class State {
        internal Item item;
        internal Collider2D[] colliders;
        internal bool[] wasEnabled;
        internal bool wasSimulated;
    }

    static State Capture(Item item) {
        if (!Net.IsRunning || item == null) return null;
        Collider2D[] colliders = item.GetComponentsInChildren<Collider2D>(true);
        bool[] wasEnabled = new bool[colliders.Length];
        for (int i = 0; i < colliders.Length; i++) {
            if (colliders[i] == null) continue;
            wasEnabled[i] = colliders[i].enabled;
            colliders[i].enabled = false;
        }
        bool simulated = item.rb != null && item.rb.simulated;
        if (item.rb != null) item.rb.simulated = false;
        return new State { item = item, colliders = colliders, wasEnabled = wasEnabled, wasSimulated = simulated };
    }

    static void Restore(State state, bool forceWorld) {
        if (state == null || state.item == null) return;
        bool free = state.item.transform.parent == null;
        for (int i = 0; i < state.colliders.Length; i++) {
            if (state.colliders[i] == null) continue;
            state.colliders[i].enabled = free || forceWorld ? true : state.wasEnabled[i];
        }
        if (state.item.rb != null) {
            if (free || forceWorld) {
                state.item.rb.bodyType = RigidbodyType2D.Dynamic;
                state.item.rb.simulated = true;
            } else {
                state.item.rb.simulated = state.wasSimulated;
            }
        }
    }

    [HarmonyPatch(typeof(Body), "DropItem", new[] { typeof(int) })]
    internal static class Body_DropItemSlot_PhasePatch {
        static void Prefix(Body __instance, int slot, out State __state) {
            __state = null;
            if (__instance == null) return;
            try { __state = Capture(__instance.GetItem(slot)); } catch { }
        }
        static void Postfix(State __state) => Restore(__state, false);
    }

    [HarmonyPatch(typeof(Body), "DropItem", new[] { typeof(Item) })]
    internal static class Body_DropItemObject_PhasePatch {
        static void Prefix(Item item, out State __state) => __state = Capture(item);
        static void Postfix(State __state) => Restore(__state, false);
    }

    [HarmonyPatch(typeof(Body), "DropWearable", new[] { typeof(Item) })]
    internal static class Body_DropWearable_PhasePatch {
        static void Prefix(Item item, out State __state) => __state = Capture(item);
        static void Postfix(State __state) => Restore(__state, false);
    }

    [HarmonyPatch(typeof(Container), nameof(Container.UnloadItem))]
    internal static class Container_UnloadItem_PhasePatch {
        static void Prefix(Item item, out State __state) => __state = Capture(item);
        static void Postfix(State __state) => Restore(__state, true);
    }

    [HarmonyPatch(typeof(Container), nameof(Container.UnloadAllItems))]
    internal static class Container_UnloadAllItems_PhasePatch {
        static void Prefix(Container __instance, out List<State> __state) {
            __state = null;
            if (!Net.IsRunning || __instance == null) return;
            __state = new List<State>();
            foreach (Transform child in __instance.transform) {
                Item item = child.GetComponent<Item>();
                if (item != null) __state.Add(Capture(item));
            }
        }
        static void Postfix(List<State> __state) {
            if (__state == null) return;
            for (int i = 0; i < __state.Count; i++) Restore(__state[i], true);
        }
    }
}
#endif
