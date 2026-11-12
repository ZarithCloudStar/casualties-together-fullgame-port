#if FULLGAME
using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Together;

internal sealed class EndingLabBakedObject : MonoBehaviour { internal bool critical; }

// EndLabScript holds direct references into this prefab. Replacing a baked
// scientist/device with a loose Resources clone severs those references (some
// fullgame lab objects do not even have a standalone Resources prefab).
internal static class EndingLabObjects {
    static readonly List<GameObject> baked = new();
    internal static void Capture(EndLabScript lab) {
        baked.Clear();
        var root = lab.transform.root;
        var references = new List<Transform>();
        foreach (var component in new Component[] { lab.scientistAnim, lab.scientist, lab.surgicalDevice, lab.injector,
            lab.surgeryDoor, lab.doorJoint, lab.endGuardSpawner, lab.turret })
            if (component != null) references.Add(component.transform);
        if (lab.removedHeart != null) references.Add(lab.removedHeart.transform);
        references.AddRange(root.GetComponentsInChildren<LampScript>(true).Select(l => l.transform));
        foreach (var go in root.GetComponentsInChildren<BuildingEntity>(true).Select(b => b.gameObject)
            .Concat(root.GetComponentsInChildren<Item>(true).Select(i => i.gameObject)).Distinct()) {
            var marker = go.GetComponent<EndingLabBakedObject>() ?? go.AddComponent<EndingLabBakedObject>();
            marker.critical = go.GetComponent<GuardScript>() == null && references.Any(t => t == go.transform || t.IsChildOf(go.transform));
            baked.Add(go);
        }
        log.l($"[ending] retained laboratory scene bindings root={root.name} objects={baked.Count}");
    }
    internal static bool Keep(GameObject go) => EndingFlow.Active && go != null && go.GetComponent<EndingLabBakedObject>()?.critical == true;
    internal static bool SceneCritical(GameObject go) => Keep(go);
    internal static GameObject Bind(string resource, Vector2 position) {
        if (!EndingFlow.Active || !Net.IsClient || WorldGeneration.world == null || WorldGeneration.world.biomeDepth != 11) return null;
        return baked.FirstOrDefault(go => Keep(go) && !NetObjectRegistry.IsRegistered(go) &&
            SyncInfo.GetItemID(go) == resource && Vector2.Distance(go.transform.position, position) < .5f);
    }
}
[HarmonyPatch(typeof(EndLabScript), "Awake")]
internal static class Ending_LabBindingsPatch {
    static void Postfix(EndLabScript __instance) { if (EndingFlow.Active) EndingLabObjects.Capture(__instance); }
}
#endif
