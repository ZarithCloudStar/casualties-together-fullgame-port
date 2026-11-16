#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Together;

// CUCoreLib 1.1 replaces CheckDamage for built-in drop pools with a Prefix
// that returns false. FreshDropRelevance's vanilla transpiler cannot run there.
// Both its built-in pools and custom buildings use this shared spawn helper.
[HarmonyPatch]
internal static class CuCoreFreshDropCompatibility {
    static Type RegistryType() {
        // Together may initialize before CUCoreLib's plugin. Resolve through
        // its assembly directly; AccessTools' type cache can predate that load.
        try {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "CUCoreLib")
                ?? Assembly.Load("CUCoreLib");
            return assembly.GetType("CUCoreLib.Registries.BuildingEntityRegistry", false);
        } catch (FileNotFoundException) { return null; }
          catch (FileLoadException) { return null; }
          catch (BadImageFormatException) { return null; }
    }

    internal static MethodInfo FindTarget() {
        var type = RegistryType();
        if (type == null) return null;
        return AccessTools.DeclaredMethod(type, "SpawnDrop", new[] {
            typeof(Vector3), typeof(string), typeof(Quaternion), typeof(float?),
            typeof(float), typeof(float), typeof(bool)
        });
    }

    static bool Prepare() {
        if (!Util.GameIsFull()) return false;
        var type = RegistryType();
        if (type == null) return false; // optional dependency absent
        if (FindTarget() != null) return true;
        log.warn("[drop] CUCoreLib SpawnDrop signature not supported; multiplayer floating compatibility was not installed");
        return false;
    }

    static MethodBase TargetMethod() {
        var method = FindTarget();
        log.l("[drop] CUCoreLib SpawnDrop uses nearest living player (native radius/lifetime preserved)");
        return method;
    }

#if TESTING
    internal static int ServerDropsSeen;
#endif

    // Positional bindings avoid depending on CUCoreLib's parameter names.
    static void Prefix(Vector3 __0, ref bool __6) {
        if (!Net.IsRunning || !Net.IsServer) return;
#if TESTING
        ServerDropsSeen++;
#endif
        var nearest = ScavPlayer.GetDistanceToNearestLivingPlayer(__0);
        bool validatedCapsule = FreshDropRelevance.IsValidatedCapsuleDrop(__0);
        bool near = nearest.Item2 < 8f * 8f || validatedCapsule;
        if (log.verbose && near != __6)
            log.l($"[drop] CUCoreLib source={(Vector2)__0} nearestLiving={Mathf.Sqrt(nearest.Item2):0.00} hostFlag={__6} multiplayerFlag={near} validatedCapsule={validatedCapsule}");
        __6 = near;
        // Leave loot rolls, network registration, Item.itemFloating and the
        // native FreshItemDrop component's ten-second lifetime to the producer.
    }
}
#endif
