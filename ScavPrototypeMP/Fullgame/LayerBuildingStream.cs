#if FULLGAME
using UnityEngine;

namespace Together;

internal static class LayerBuildingStream {
    internal const string TurretId = "ceilingturret";
    internal const string BloodsuckerId = "bloodsuckerden";

    internal static void RegisterNearby() {
        var world = WorldGeneration.world;
        if (!Net.IsRunning || !Net.IsServer || world == null || !world.worldExists) return;

        foreach (var turret in Object.FindObjectsOfType<CeilingTurret>(true))
            Register(turret, TurretId);
        foreach (var bloodsucker in Object.FindObjectsOfType<BloodSuckerScript>(true))
            Register(bloodsucker, BloodsuckerId);

        // Our ending state owns the cargo scene and its removal after pickup.
    }

    private static void Register(Component source, string id) {
        if (source == null) return;
        var build = source.GetComponentInParent<BuildingEntity>(true);
        if (build == null) return;
        if (string.IsNullOrEmpty(build.id)) build.id = id;
        if (NetObjectRegistry.IsRegistered(build) || !SharedMain.WorldInSimRange(build.transform.position)) return;
        NetObjectRegistry.SafeNewGO(build.gameObject);
    }
}
#endif
