#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using System;
using UnityEngine;

namespace Together;

// Blocks the host from starting/continuing a run while any connected player is
// still choosing a character, and keeps a visible reminder on the host screen.
internal static class CharacterReadyGate {
    static float lastReminder;
    internal static bool LoadQueued { get; private set; }
    internal static bool RequestLoad() {
        if (WorldMP.IsMenuLoadPending) return false;
        if (SaveSlots.Enabled && Net.IsRunning && Net.IsServer && !SaveSlots.LoadPrepared) {
            SaveSlots.RequestLoad();
            return false;
        }
        if (CharacterReadyGate.Allow("LoadRun")) return true;
        LoadQueued = true;
        log.l("LOAD: queued menu load; retaining save intent until character admission settles");
        return false;
    }

    internal static int WaitingCount {
        get {
            if (!Util.GameIsFull() || !Net.IsServer) return 0;
            return PendingCharacterAdmission.Count;
        }
    }

    internal static bool Blocked => WaitingCount > 0;

    internal static bool Allow(string action) {
        int waiting = WaitingCount;
        log.l($"CHARACTER: start gate {action} waiting={waiting}");
        if (waiting <= 0) return true;
        log.warn($"CHARACTER: {action} blocked; {waiting} player(s) are still choosing a character.");
        Remind(waiting);
        return false;
    }

    internal static void Remind(int waiting) {
        try {
            log.l($"CHARACTER: waiting for {waiting} player(s) to choose a character");
            Multiplayer.DoMultiplayerStatusMessageLog(
                CharacterSelection.Text("character_waiting_start", "Waiting for {0} player(s) to choose a character.").Replace("{0}", waiting.ToString()));
        } catch (Exception ex) {
            log.error("CHARACTER: could not show the waiting message: " + ex.Message);
        }
    }

    // Called from Plugin.Update. Shows a reminder every few seconds rather than
    // spamming the chat every frame.
    internal static void Tick() {
        if (!Net.IsRunning || !Net.IsServer || Util.IsInWorld()) LoadQueued = false;
        if (LoadQueued && !Blocked && PreRunScript.instance != null) {
            LoadQueued = false;
            PreRunScript.instance.LoadRun();
        }
        if (!Blocked) return;
        float now = Time.unscaledTime;
        if (now - lastReminder < 5f) return;
        lastReminder = now;
        Remind(WaitingCount);
    }
}

[HarmonyPatch(typeof(PreRunScript), nameof(PreRunScript.StartRun))]
internal static class PreRunScript_StartRun_CharacterReadyPatch {
    // The mod's own StartRun prefix returns false to replace the vanilla flow.
    // Run first so the gate still sees the click.
    [HarmonyPriority(Priority.First)]
    static bool Prefix() {
        if (SaveSlots.Enabled && (SaveSlotMenu.IsOpen || SaveSlots.LoadPrepared || SaveSlots.Busy)) return false;
        if (WorldMP.IsMenuLoadPending || CharacterReadyGate.LoadQueued || !CharacterReadyGate.Allow("StartRun")) return false;
        EndingFlow.ResetRun();
        return true;
    }
}

[HarmonyPatch(typeof(PreRunScript), nameof(PreRunScript.LoadRun))]
internal static class PreRunScript_LoadRun_CharacterReadyPatch {
    [HarmonyPriority(Priority.First)]
    static bool Prefix() => CharacterReadyGate.RequestLoad();
}

[HarmonyPatch(typeof(PreRunScript), nameof(PreRunScript.StartTutorial))]
internal static class PreRunScript_StartTutorial_CharacterReadyPatch {
    [HarmonyPriority(Priority.First)]
    static bool Prefix() => !(SaveSlots.Enabled && (SaveSlotMenu.IsOpen || SaveSlots.LoadPrepared || SaveSlots.Busy)) &&
        !WorldMP.IsMenuLoadPending && !CharacterReadyGate.LoadQueued && CharacterReadyGate.Allow("StartTutorial");
}

// The vanilla load coroutine is the third path into a run (the mod announces the
// start from its own WaitLoad prefix). Blocking here stops the host even when the
// click never reaches StartRun/LoadRun.
[HarmonyPatch(typeof(PreRunScript), nameof(PreRunScript.WaitLoad))]
internal static class PreRunScript_WaitLoad_CharacterReadyPatch {
    [HarmonyPriority(Priority.First)]
    static bool Prefix(PreRunScript __instance, ref System.Collections.IEnumerator __result) {
        if (CharacterReadyGate.Allow("WaitLoad")) return true;
        // Keep the latch and the original load/new/tutorial intent throughout
        // a connection arriving between the menu click and this coroutine.
        __result = WaitForAdmission(__instance, SaveSystem.loadedRun);
        return false;
    }
    static System.Collections.IEnumerator WaitForAdmission(PreRunScript menu, bool load) {
        var session = Net.ServerEntityManager;
        while (CharacterReadyGate.Blocked) yield return null;
        if (menu == null || !Net.IsServer || !Net.IsRunning || Util.IsInWorld() ||
            !ReferenceEquals(session, Net.ServerEntityManager)) yield break;
        SaveSystem.loadedRun = load;
        yield return menu.WaitLoad();
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.ContinueRun))]
internal static class WorldGeneration_ContinueRun_CharacterReadyPatch {
    [HarmonyPriority(Priority.First)]
    static bool Prefix() => CharacterReadyGate.Allow("ContinueRun");
}
#endif
