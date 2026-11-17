using System;
using HarmonyLib;
using UnityEngine;
namespace Together;
internal sealed class GlassSpent : MonoBehaviour { }
[HarmonyPatch(typeof(Utils), nameof(Utils.Create), new Type[] { typeof(string), typeof(Vector2), typeof(float) })]
internal static class Utils_Create_NoClientGlass {
    static bool Prefix(string id, ref GameObject __result) {
        if (!Net.IsClient || id != "glassshards") return true;
        __result = null;
        return false;
    }
}
[HarmonyPatch(typeof(Utils), nameof(Utils.Create), new Type[] { typeof(string), typeof(Transform) })]
internal static class Utils_CreateParent_NoClientGlass {
    static bool Prefix(string id, ref GameObject __result) {
        if (!Net.IsClient || id != "glassshards") return true;
        __result = null;
        return false;
    }
}
