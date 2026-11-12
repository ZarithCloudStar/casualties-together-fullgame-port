#if FULLGAME && TESTING
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Together;

// Sandbox-friendly opt-in fixtures: neither preferences nor Discord registration
// should write to HKCU. The shipping assembly does not contain these patches.
[HarmonyPatch]
internal static class EndingTestPreferences {
    internal static bool Enabled => Environment.GetCommandLineArgs().Contains("--mp-ending-test");
    static readonly Dictionary<string, object> Values = new();
    static IEnumerable<MethodBase> TargetMethods() {
        foreach (string name in new[] { "SetString", "SetInt", "SetFloat", "Save", "DeleteKey", "DeleteAll" })
            yield return AccessTools.Method(typeof(PlayerPrefs), name);
    }
    static bool Prefix(MethodBase __originalMethod, object[] __args) {
        if (!Enabled) return true;
        string name = __originalMethod.Name;
        if (name.StartsWith("Set")) Values[(string)__args[0]] = __args[1];
        else if (name == "DeleteKey") Values.Remove((string)__args[0]);
        else if (name == "DeleteAll") Values.Clear();
        return false;
    }
    internal static bool TryGet<T>(string key, ref T result) {
        if (!Enabled || !Values.TryGetValue(key, out var value) || !(value is T typed)) return true;
        result = typed; return false;
    }
}
[HarmonyPatch(typeof(PlayerPrefs), nameof(PlayerPrefs.GetInt), new[] { typeof(string), typeof(int) })]
internal static class EndingTestGetInt {
    static bool Prefix(string key, ref int __result) {
        if (EndingTestPreferences.Enabled && key == CharacterSelection.ConfirmedPrefsKey) { __result = 1; return false; }
        return EndingTestPreferences.TryGet(key, ref __result);
    }
}
[HarmonyPatch(typeof(PlayerPrefs), nameof(PlayerPrefs.GetString), new[] { typeof(string), typeof(string) })]
internal static class EndingTestGetString { static bool Prefix(string key, ref string __result) => EndingTestPreferences.TryGet(key, ref __result); }
[HarmonyPatch(typeof(PlayerPrefs), nameof(PlayerPrefs.GetFloat), new[] { typeof(string), typeof(float) })]
internal static class EndingTestGetFloat { static bool Prefix(string key, ref float __result) => EndingTestPreferences.TryGet(key, ref __result); }
[HarmonyPatch(typeof(RPCManager), "Update")]
internal static class EndingTestNoDiscord { static bool Prefix() => !EndingTestPreferences.Enabled; }
#endif
