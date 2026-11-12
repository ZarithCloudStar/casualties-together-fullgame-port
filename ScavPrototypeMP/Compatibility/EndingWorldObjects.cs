#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Together;

// BlueHeart lives under a DOSPAWN scene object, outside the item/building
// registry. Terrain and registered drill-pod entities still use world sync.
internal static class EndingWorldObjects {
    static GameObject clientScene;
    static int clientTravel = -1;
    static object clientSession;

    internal static bool Generate(Vector2 position, GameObject prefab) {
        if (!EndingFlow.Active || prefab == null || prefab.name != "BlueHeartCont") return true;
        if (Net.IsClient) return false;
        var state = EndingFlow.State;
        state.heartSceneReady = true;
        state.heartSceneTravel = WorldGeneration.world.totalTraveled;
        state.heartSceneX = position.x;
        state.heartSceneY = position.y;
        log.l($"[cargo] server BlueHeartCont origin={position} travel={state.heartSceneTravel}");
        return true;
    }

    internal static void Apply() {
        if (!EndingFlow.Active || !Net.IsClient) return;
        var world = WorldGeneration.world;
        var state = EndingFlow.State;
        if (!ReferenceEquals(clientSession, Net.ClientEntityManager) || (world != null && world.totalTraveled != clientTravel)) {
            if (clientScene != null) Object.Destroy(clientScene);
            clientScene = null;
            clientSession = Net.ClientEntityManager;
            clientTravel = world != null ? world.totalTraveled : -1;
        }
        if (world == null || world.biomeDepth != 9 || world.generatingWorld || !Util.IsWorldGenerated() ||
            !state.heartSceneReady || state.heartSceneTravel != world.totalTraveled || (state.cargoTaken && !state.cargoAcquiring) || clientScene != null) return;
        var prefab = Resources.Load<GameObject>("Structures/BlueHeartCont");
        foreach (Transform child in prefab.transform) {
            if (child.GetComponentInChildren<BlueHeart>(true) == null) continue;
            // Exactly the vanilla GenerateEntityAtPos offset (1705-1710).
            Vector2 origin = new Vector2(state.heartSceneX, state.heartSceneY) - Vector2.one * .5f;
            clientScene = Object.Instantiate(child.gameObject, origin + (Vector2)child.localPosition, child.localRotation);
            log.l($"[cargo] client BlueHeart scene={clientScene.name} origin={origin} heart={BlueHeart.main?.transform.position} travel={clientTravel}");
            break;
        }
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.GenerateEntityAtPos))]
internal static class Ending_AuthoritativeHeartScenePatch {
    [HarmonyPriority(Priority.First)]
    static bool Prefix(Vector2 pos, GameObject basObj) => EndingWorldObjects.Generate(pos, basObj);
}
#endif
