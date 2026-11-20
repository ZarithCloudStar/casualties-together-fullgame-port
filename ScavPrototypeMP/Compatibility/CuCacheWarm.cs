using System;
using System.Collections;
using HarmonyLib;

namespace Together;

internal static class CuCacheWarm {
    internal static IEnumerator BeforeWorldGeneration() {
        // CUCoreLib may finish loading after Together. Let all plugin Start methods run.
        yield return null;
        if (WorldGeneration.world != null && WorldGeneration.world.generatingWorld) yield break;

        var cache = AccessTools.TypeByName("CUCoreLib.Helpers.ResourceCache");
        var init = cache == null ? null : AccessTools.Method(cache, "TryInitialize", Type.EmptyTypes);
        if (init == null) yield break;

        try {
            init.Invoke(null, null);
        } catch (Exception e) {
            log.warn($"CUCoreLib cache warmup failed: {e.GetBaseException().Message}");
        }
    }
}
