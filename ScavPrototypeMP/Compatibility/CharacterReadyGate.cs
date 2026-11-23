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

    internal static int LobbyMissing => CharacterLobby.MissingReadyCount;

    internal static bool Allow(string action, bool checkLobbyReady = true) {
        int waiting = WaitingCount;
        int lobbyMissing = checkLobbyReady ? LobbyMissing : 0;
        log.l($"CHARACTER: start gate {action} waiting={waiting} lobbyMissing={lobbyMissing}");
        if (checkLobbyReady && Util.GameIsFull() && Net.IsRunning && Net.IsServer && !Net.IsDedicatedServer && !Util.IsInWorld() && !CharacterLobby.HostChosen) {
            log.warn($"CHARACTER: {action} blocked; the host has not chosen a character.");
            RemindHostCharacter();
            return false;
        }
        if (lobbyMissing > 0) {
            log.warn($"CHARACTER: {action} blocked; {lobbyMissing} player(s) are not ready.");
            RemindReady(lobbyMissing);
            return false;
        }
        if (waiting <= 0) return true;
        log.warn($"CHARACTER: {action} blocked; {waiting} player(s) are still choosing a character.");
        Remind(waiting);
        return false;
    }

    internal static void Remind(int waiting) {
        try {
            log.l($"CHARACTER: waiting for {waiting} player(s) to choose a character");
            Multiplayer.DoMultiplayerStatusMessageLog(WaitingText(waiting));
        } catch (Exception ex) {
            log.error("CHARACTER: could not show the waiting message: " + ex.Message);
        }
    }

    static void RemindReady(int missing) {
        try {
            log.l($"CHARACTER: waiting for {missing} player(s) to choose and ready up");
            Multiplayer.DoMultiplayerStatusMessageLog(ReadyText(missing));
        } catch (Exception ex) {
            log.error("CHARACTER: could not show the ready message: " + ex.Message);
        }
    }

    static void RemindHostCharacter() {
        try {
            Multiplayer.DoMultiplayerStatusMessageLog(CharacterSelection.Text("character_start_need_character", "Choose a character before starting."));
        } catch (Exception ex) {
            log.error("CHARACTER: could not show the host message: " + ex.Message);
        }
    }

    internal static string WaitingText(int waiting) =>
        CharacterSelection.Text("character_waiting_start", "Waiting for {0} player(s) to choose a character.").Replace("{0}", waiting.ToString());

    internal static string ReadyText(int missing) =>
        CharacterSelection.Text("character_waiting_ready", "Waiting for {0} player(s) to choose and ready up.").Replace("{0}", missing.ToString());

    // Called from Plugin.Update. Shows a reminder every few seconds rather than
    // spamming the chat every frame.
    internal static void Tick() {
        if (!Net.IsRunning || !Net.IsServer || Util.IsInWorld()) LoadQueued = false;
        if (LoadQueued && !Blocked && PreRunScript.instance != null &&
            (Net.IsDedicatedServer || (CharacterLobby.HostChosen && CharacterLobby.MissingReadyCount == 0))) {
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
        // Single-player never participates in the lobby readiness gate.
        if (!Net.IsRunning) return true;
        // The character page's own Start is a Confirm; the higher-priority
        // StockCharacterSelectUI patch consumes it.
        if (CharacterLobby.ShouldShowPanel || CharacterLobby.StartClickInProgress) return true;
        if (Net.IsClient) return true;
        if (!LobbyHostTransfer.AllowRun(false)) return false;
        if (SaveSlots.Enabled && (SaveSlotMenu.IsOpen || SaveSlots.LoadPrepared || SaveSlots.Busy)) return false;
        if (WorldMP.IsMenuLoadPending || CharacterReadyGate.LoadQueued || !CharacterReadyGate.Allow("StartRun")) return false;
        EndingFlow.ResetRun();
        return true;
    }
}

[HarmonyPatch(typeof(PreRunScript), nameof(PreRunScript.LoadRun))]
internal static class PreRunScript_LoadRun_CharacterReadyPatch {
    [HarmonyPriority(Priority.First)]
    static bool Prefix() => !Net.IsRunning || (LobbyHostTransfer.AllowRun(true) && CharacterReadyGate.RequestLoad());
}

[HarmonyPatch(typeof(PreRunScript), nameof(PreRunScript.StartTutorial))]
internal static class PreRunScript_StartTutorial_CharacterReadyPatch {
    [HarmonyPriority(Priority.First)]
    static bool Prefix() => !Net.IsRunning || (!(SaveSlots.Enabled && (SaveSlotMenu.IsOpen || SaveSlots.LoadPrepared || SaveSlots.Busy)) &&
        LobbyHostTransfer.AllowRun(false) &&
        !WorldMP.IsMenuLoadPending && !CharacterReadyGate.LoadQueued && CharacterReadyGate.Allow("StartTutorial"));
}

// The vanilla load coroutine is the third path into a run (the mod announces the
// start from its own WaitLoad prefix). Blocking here stops the host even when the
// click never reaches StartRun/LoadRun.
[HarmonyPatch(typeof(PreRunScript), nameof(PreRunScript.WaitLoad))]
internal static class PreRunScript_WaitLoad_CharacterReadyPatch {
    [HarmonyPriority(Priority.First)]
    static bool Prefix(PreRunScript __instance, ref System.Collections.IEnumerator __result) {
        if (!Net.IsRunning) return true;
        if (!LobbyHostTransfer.AllowRun(SaveSystem.loadedRun)) return false;
        // Start/load already checked the lobby readiness. Re-checking here can
        // deadlock the load coroutine if a guest toggles ready in between.
        if (CharacterReadyGate.Allow("WaitLoad", false)) {
            // Ready flags are per-lobby. Clear them before the world starts so
            // returning to the lobby always needs a fresh ready-up.
            if (Util.GameIsFull() && Net.IsServer) CharacterLobby.ServerResetReady();
            return true;
        }
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
    static bool Prefix() => !Net.IsRunning || (LobbyHostTransfer.AllowRun(false) && CharacterReadyGate.Allow("ContinueRun"));
}
#endif
