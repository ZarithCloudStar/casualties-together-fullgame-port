#if FULLGAME
using BepInEx.Configuration;
using CasualtiesTogetherUtils;
using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Together;

// The vanilla pre-run screen gates Milky/Dune behind the "wonchar" progress flags
// and disables their buttons in PreRunScript.Awake. Writing the flags as unlocked
// satisfies every existing check (button state, tooltip, grey-out) without
// touching the vanilla methods, and the button pass is a fallback for the case
// where PlayerPrefs cannot be written (locked-down registry).
internal static class UnlockAllCharacters {
    internal static ConfigEntry<bool> Enabled;

    internal static void Init() {
        Enabled = Plugin.main.Config.Bind(
            "Character",
            "UnlockAllCharacters",
            true,
            "Mark Milky and Dune as unlocked so the vanilla pre-run character screen lets you pick them without story progress.");
        Apply();
    }

    static bool Active => Util.GameIsFull() && Enabled != null && Enabled.Value;

    internal static int PortraitUnlock(string key) => Active ? 1 : PlayerPrefs.GetInt(key);

    internal static void Apply() {
        if (!Active) return;
        int updated = 0;
        for (int i = 1; i <= 2; i++) {
            string key = "wonchar" + (i - 1);
            if (PlayerPrefs.GetInt(key, 0) <= 0) {
                PlayerPrefs.SetInt(key, 1);
                updated++;
            }
        }
        if (updated > 0) {
            try {
                PlayerPrefs.Save();
            } catch (System.Exception e) {
                log.error("UNLOCK: PlayerPrefs.Save failed, relying on the button fallback: " + e.Message);
            }
        }
        log.l($"UNLOCK: pre-run characters unlocked (updated {updated} wonchar flag(s), enabled={Enabled.Value})");
    }

    internal static void ForceButtons(PreRunScript inst) {
        if (!Active || inst == null) return;
        try {
            var images = inst.charImages;
            int total = images?.Count ?? 0;
            int reopened = 0;
            for (int i = 0; i < total; i++) {
                var image = images[i];
                if (image == null) continue;
                var button = image.GetComponent<Button>();
                if (button != null && !button.interactable) {
                    button.interactable = true;
                    reopened++;
                }
            }
            log.l($"UNLOCK: PreRunScript character buttons interactable ({reopened} reopened of {total})");
        } catch (System.Exception e) {
            log.error("UNLOCK: ForceButtons failed: " + e.ToString());
        }
    }
}

[HarmonyPatch(typeof(PreRunScript), "Awake")]
internal static class PreRunScript_Awake_UnlockAllCharacters {
    static void Postfix(PreRunScript __instance) => UnlockAllCharacters.ForceButtons(__instance);
}

[HarmonyPatch(typeof(PreRunScript), "HandleCharacterSelect")]
internal static class PreRunScript_HandleCharacterSelect_UnlockAllCharacters {
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code) =>
        code.MethodReplacer(
            AccessTools.Method(typeof(PlayerPrefs), nameof(PlayerPrefs.GetInt), new[] { typeof(string) }),
            AccessTools.Method(typeof(UnlockAllCharacters), nameof(UnlockAllCharacters.PortraitUnlock)));
}
#endif
