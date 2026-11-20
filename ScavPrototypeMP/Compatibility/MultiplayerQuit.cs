using CasualtiesTogetherUtils;
using HarmonyLib;
using System;
using System.Collections;
using UnityEngine;

namespace Together;

// Tear down live transports before Unity/Steam destroys their owners. Do not
// use ShutdownNetwork here: it starts a scene transition and deletes bodies
// while the quit fade and scene Update callbacks are still using them.
internal static class MultiplayerQuit {
    internal static bool Quitting { get; private set; }
    static bool transportStopped;

    internal static bool Request() {
        // The host-transfer dialog and the receive-slot selector are modal. The
        // native save/exit controls live behind them, so a single click can
        // otherwise reach PreRunScript.Quit and close the game mid-transfer.
        if (HostTransferUI.IsModalOpen
#if FULLGAME
            || SaveSlotMenu.IsTransferOpen
#endif
            ) {
            Multiplayer.DoMultiplayerStatusMessageLog("请先完成主机移交，再退出游戏。");
            return false;
        }
        if (!Net.IsRunning && !Quitting) return true;
        if (!Quitting) {
            Quitting = true;
            log.l($"[quit] requested role={(Net.IsServer ? "host" : "client")} inWorld={Util.IsInWorld()} transport={Net.Transport?.GetType().Name}");
            Util.StartCoroutine(Quit());
        }
        return false;
    }

    static IEnumerator Quit() {
        if (GlobalDark.main != null) {
            GlobalDark.main.SetStayDark(false);
            GlobalDark.main.SetSpeedMultiplier(1f);
            GlobalDark.main.Darken();
            float deadline = Time.realtimeSinceStartup + 2f;
            while (GlobalDark.main.IsDarkening() && Time.realtimeSinceStartup < deadline) yield return null;
        }
        StopTransport();
        log.l("[quit] transport stopped; handing exit to Unity");
        Application.Quit();
    }

    internal static void StopTransport() {
        if (transportStopped) return;
        Quitting = true;
        transportStopped = true;
        Voicechat.current_microphone_mode = 0;
        try { Voicechat.StopRecording(false); }
        catch (Exception ex) { log.error($"[quit] microphone shutdown failed: {ex}"); }
        var transport = Net.Transport;
        Net.QuiesceForApplicationQuit(); // callbacks must not return to a scene during exit
        Net.Transport = null;
        try { transport?.Shutdown(); }
        catch (Exception ex) { log.error($"[quit] transport shutdown failed: {ex}"); }
    }
}

[HarmonyPatch(typeof(PreRunScript), nameof(PreRunScript.Quit))]
internal static class Multiplayer_QuitPatch {
    static bool Prefix() => MultiplayerQuit.Request();
}
