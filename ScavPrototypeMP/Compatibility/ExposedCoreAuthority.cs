#if FULLGAME
using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;

namespace Together;

// Exposed cores are normally converted by CustomItemBehaviour.Update into two
// manifestations. That Update stops running while the item is tucked into a
// container (the container hides the child object), so the vanilla host never
// performs the conversion. The client can also run its local conversion and
// then have the item re-created from the still-live container data.
// Keep this one authoritative on the host, including for inactive/container
// children, and suppress the local conversion on clients.
internal static class ExposedCoreAuthority {
    const string CoreId = "exposedcore";
    static float nextScan;
    static readonly HashSet<int> handled = new();

    internal static void Tick() {
        if (!Net.IsServer || !Net.IsRunning || Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .35f;
        var items = UnityEngine.Object.FindObjectsOfType<Item>(true);
        foreach (var item in items) {
            if (item == null || item.id != CoreId || item.condition > 0f) continue;
            Handle(item);
        }
        if (handled.Count > 64) handled.Clear();
    }

    internal static bool Handle(Item item) {
        if (!Net.IsServer || item == null || item.id != CoreId || item.condition > 0f) return false;
        if (WorldGeneration.world == null || WorldGeneration.world.biomeDepth == 11) return false;
        int id = item.GetInstanceID();
        if (!handled.Add(id)) return false;

        Vector3 position = item.transform.position;
        try {
            // Remove the bag/slot/container link before destroying the entity.
            // Otherwise the next container snapshot can rebuild it forever.
            ItemSync.SafeUnloadItem(item);
        } catch (System.Exception e) {
            log.warn($"[crystal-core] unload failed id={id}: {e.Message}");
        }
        if (item != null) {
            item.transform.SetParent(null, true);
            UnityEngine.Object.Destroy(item.gameObject);
        }

        SpawnManifestation(position + Vector3.right * .5f);
        SpawnManifestation(position + Vector3.left * .5f);
        log.l($"[crystal-core] converted exposed core id={id} pos={position}");
        return true;
    }

    static void SpawnManifestation(Vector3 position) {
        try {
            var go = Utils.Create("crystalenemy", position, 0f);
            if (go == null) return;
            if (go.TryGetComponent(out CrystalEnemy enemy))
                enemy.SetColor(new Color(Random.Range(.5f, 1f), Random.Range(.5f, 1f), Random.Range(.5f, 1f)));
            NetObjectRegistry.Server_EnsureItemIsNetworkRegistered(go);
        } catch (System.Exception e) {
            log.error("[crystal-core] manifestation spawn failed: " + e);
        }
    }
}

[HarmonyPatch(typeof(CustomItemBehaviour), "Update")]
internal static class CustomItemBehaviour_ExposedCorePatch {
    static bool Prefix(CustomItemBehaviour __instance) {
        if (__instance == null) return true;
        var item = __instance.GetComponent<Item>();
        if (item == null || item.id != "exposedcore" || item.condition > 0f) return true;
        if (Net.IsServer) ExposedCoreAuthority.Handle(item);
        // The host's authoritative conversion/sync is the only one that may run.
        return false;
    }
}

[HarmonyPatch(typeof(Plugin), "Update")]
internal static class ExposedCoreAuthorityTick {
    static void Postfix() => ExposedCoreAuthority.Tick();
}
#endif
