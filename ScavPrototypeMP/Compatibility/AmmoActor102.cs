#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Together;

internal static class AmmoActor102 {
    internal static Body Resolve(Component source) {
        var actor = Body_CombineItems_MultiplayerPatch.GunLoadingBody;
        if (actor != null) return actor;
        if (source == null) return null;
        // Walk inactive bags and mounted guns too. Do not guess the host from
        // PlayerCamera or the nearest player when there is no owner.
        for (var parent = source.transform; parent != null; parent = parent.parent) {
            if (parent.TryGetComponent<InventorySlot>(out var slot)) return slot.body;
            if (parent.TryGetComponent<Limb>(out var limb)) return limb.body;
            if (parent.TryGetComponent<Body>(out var body)) return body;
        }
        return null;
    }

    static bool attachmentPatchesFinished, waitingReported;
    static float nextAttachmentCheck, nextActorWarning;

    internal static void WarnUnknownActor(Component source) {
        if (Time.realtimeSinceStartup < nextActorWarning) return;
        nextActorWarning = Time.realtimeSinceStartup + 10f;
        log.warn($"[ammo] actor unavailable for {source?.name}; preserving the original ammo operation");
    }

    // FirstInit runs inside Together's Awake, before later plugins are loaded.
    // Poll from Update instead; keep waiting after the one-time missing warning.
    internal static void TickAttachmentPatches() {
        if (attachmentPatchesFinished || Time.realtimeSinceStartup < nextAttachmentCheck) return;
        nextAttachmentCheck = Time.realtimeSinceStartup + 1f;
        if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue("com.gouxi.gouxismoreattachment", out var plugin)
            || plugin.Instance == null) {
            if (!waitingReported) {
                waitingReported = true;
                log.warn("[ammo] optional attachment plugin com.gouxi.gouxismoreattachment is not loaded yet; retrying once per second");
            }
            return;
        }
        if (Plugin.main?.harmony == null) return;
        attachmentPatchesFinished = true;
        var type = plugin.Instance.GetType().Assembly.GetType("gouxismoreattachments.Modules.Attachments.MagazineUtil");
        if (type == null) { log.warn("[ammo] optional attachment MagazineUtil was not found; compatibility patches were not installed"); return; }
        foreach (string name in new[] { "EjectTrackedMag", "EjectMountedMag" }) {
            try {
                var method = AccessTools.DeclaredMethod(type, name);
                if (method == null || !method.IsStatic || method.GetParameters().Length == 0
                    || method.GetParameters()[0].ParameterType != typeof(GunScript)) {
                    log.warn($"[ammo] optional attachment {name} was not found or has an unsupported signature");
                    continue;
                }
                Plugin.main.harmony.Patch(method, transpiler: new HarmonyMethod(
                    AccessTools.Method(typeof(GunLoadActor102), "Transpiler")));
                log.l($"[ammo] attachment {name} patched; using the gun's actor");
            } catch (Exception e) {
                log.warn($"[ammo] attachment {name} patch failed: {e}");
            }
        }
    }
}

// Preserve the actor across third-party prefixes, skipped originals, nested
// loads and exceptions. The combine receiver already authenticates its user.
[HarmonyPatch]
internal static class GunAmmoActorScope102 {
    static IEnumerable<MethodBase> TargetMethods() {
        yield return AccessTools.Method(typeof(GunScript), nameof(GunScript.LoadMag));
        yield return AccessTools.Method(typeof(GunScript), nameof(GunScript.UnloadMag));
    }
    [HarmonyPriority(Priority.First)]
    static bool Prefix(GunScript __instance, object[] __args, out Body __state) {
        __state = Body_CombineItems_MultiplayerPatch.GunLoadingBody;
        if (!Net.IsRunning) return true;
        var actor = AmmoActor102.Resolve(__instance);
        if (actor == null && __args.Length > 0 && __args[0] is AmmoScript ammo) actor = AmmoActor102.Resolve(ammo);
        Body_CombineItems_MultiplayerPatch.GunLoadingBody = actor;
        if (actor == null) AmmoActor102.WarnUnknownActor(__instance);
        return true;
    }
    static void Finalizer(Body __state) => Body_CombineItems_MultiplayerPatch.GunLoadingBody = __state;
}

// Custom useAction delegates may discard their Body argument before calling
// AmmoScript.UnloadRound, including uses of a magazine lying on the ground.
[HarmonyPatch(typeof(Body), nameof(Body.UseItem))]
internal static class AmmoUseActorScope102 {
    [HarmonyPriority(Priority.First)]
    static void Prefix(Body __instance, out Body __state) {
        __state = Body_CombineItems_MultiplayerPatch.GunLoadingBody;
        if (Net.IsRunning) Body_CombineItems_MultiplayerPatch.GunLoadingBody = __instance;
    }
    static void Finalizer(Body __state) => Body_CombineItems_MultiplayerPatch.GunLoadingBody = __state;
}

[HarmonyPatch(typeof(AmmoScript), nameof(AmmoScript.UnloadRound))]
internal static class AmmoUnloadActor102 {
    static bool Prefix(AmmoScript __instance) {
        if (!Net.IsRunning) return true;
        var actor = AmmoActor102.Resolve(__instance);
        if (actor == null) {
            AmmoActor102.WarnUnknownActor(__instance);
            return true;
        }
        ItemSync.UnloadAmmoRound(actor, __instance);
        return false;
    }
}
#endif
