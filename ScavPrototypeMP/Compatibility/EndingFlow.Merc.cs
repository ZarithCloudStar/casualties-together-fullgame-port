#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using System.Linq;
using UnityEngine;

namespace Together;

internal static partial class EndingFlow {
#if TESTING
    internal static bool TestTryMerc() => TryMercEnding();
#endif
    static bool mercDenied;
    internal static bool HostMercUnlocked => PlayerPrefs.GetInt("didbiopod") == 1 || PlayerPrefs.GetInt("didescape") == 1;
    internal static bool HostCanStartMerc => Net.IsServer && HostMercUnlocked && WorldGeneration.world != null &&
        WorldGeneration.world.biomeDepth == 0 && !WorldGeneration.unchipped && !WorldGeneration.GetRunSettingBool("endless") &&
        WorldGeneration.world.biomeOverride == WorldGeneration.OverrideSceneType.None;

    static partial void CheckMercEnding(ref bool handled) {
        var scarfHolder = Players().FirstOrDefault(p => p.body != null && p.body.FindByIdThorough("mercscarf", out _));
        if (scarfHolder == null) return;
        if (!HostMercUnlocked || WorldGeneration.GetRunSettingBool("endless")) {
            if (!mercDenied) { mercDenied = true; Notice(ScavPlayer.LocalPlayer, "ending_merc_host_locked"); }
            log.l("[ending] merc rejected: host prerequisite/endless; continuing to laboratory");
            return;
        }
        handled = true;
        WorldGeneration.world.isMercRun = true;
        log.l($"[ending] merc qualified hostProgress=true carrier={State.cargoOwner} scarfHolder={Key(scarfHolder)}");
        BeginNarrative(3, "host prerequisite, team scarf and cargo carrier at layer10 breach");
    }

    internal static void ApplyMercWorld() {
        var world = WorldGeneration.world;
        if (!Active || world == null) return;
        if (Net.IsClient) {
            world.isMercRun = State.isMercRun;
            world.canStartMercRun = State.hostMercUnlocked && !State.hostUnchipped && world.biomeDepth == 0 &&
                !WorldGeneration.GetRunSettingBool("endless") && !State.isMercRun;
            if (world.isMercRun) {
                var scarf = Recipes.recipes?.FirstOrDefault(r => r.result.id == "mercscarf");
                if (scarf != null) scarf.specialKnown = true;
            }
        }
    }
}

[HarmonyPatch(typeof(WorldGeneration), "Start")]
internal static class Ending_MercStartPatch {
    static void Postfix(WorldGeneration __instance) {
        if (EndingFlow.Active) __instance.canStartMercRun = EndingFlow.HostCanStartMerc;
    }
}
[HarmonyPatch(typeof(WorldGeneration), "Update")]
internal static class Ending_MercWorldPatch {
    // Only the host may spawn the techball. Outside this Update, clients retain
    // the host's eligibility for the scanner/use UI and recipes.
    static void Prefix(WorldGeneration __instance, out bool __state) {
        __state = __instance.canStartMercRun;
        if (EndingFlow.Active && Net.IsClient) __instance.canStartMercRun = false;
    }
    static void Postfix(WorldGeneration __instance, bool __state) {
        if (EndingFlow.Active && Net.IsClient) { __instance.canStartMercRun = __state; EndingFlow.ApplyMercWorld(); }
    }
}
#endif
