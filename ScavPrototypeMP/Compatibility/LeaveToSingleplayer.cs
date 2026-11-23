using CasualtiesTogetherUtils;
using HarmonyLib;
using System;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Together;

// The main-menu Play/Tutorial icons are the vanilla "descend / tutorial"
// entries. In a room they are replaced by a single confirm: leave the room and
// return to single-player. A Steam host hands hosting to the next teammate
// automatically before leaving; IP hosts close the room instead.
internal static class LeaveToSingleplayer {
    static bool leaveAfterTransfer;

    internal static bool Intercept(AdaptiveButton.MenuAction action) {
        if (!Net.IsRunning || Util.IsInWorld() || Net.IsDedicatedServer) return true;
        if (ReturnToSingleplayerUI.IsOpen) return false;
        if (action != AdaptiveButton.MenuAction.Play && action != AdaptiveButton.MenuAction.Tutorial) return true;
        bool blocked = HostTransferUI.IsModalOpen || LobbyHostTransfer.HasRequest || LobbyHostTransfer.Busy
            || SkinMenuUI.IsPopupOpen;
#if FULLGAME
        blocked |= SaveSlotMenu.IsOpen || JoinModNotice.IsOpen || UpdateChecker.PopupOpen || LobbyCharacterPanel.BlocksVanillaMenuInput;
#endif
        if (blocked) {
            try { Multiplayer.DoMultiplayerStatusMessageLog(Lang.Get("return_sp_blocked", true) ?? "请先完成当前操作。"); } catch { }
            return false;
        }
        ReturnToSingleplayerUI.Open();
        return false;
    }

    internal static void Confirm() {
        if (!Net.IsRunning) return;
        if (Net.IsClient) { DeferFinishLeave(); return; }

        // Host: Steam rooms hand hosting to the next teammate without asking
        // them to accept. Everything else (IP rooms, no admitted teammate)
        // closes the room; connected clients fall back to the main menu.
        if (!Net.IsRunningSteam) { DeferFinishLeave(); return; }
        var target = ScavPlayer.AllPlayers.FirstOrDefault(p => p != null && !p.IsLocal && p.SteamId != 0);
        if (target == null) { DeferFinishLeave(); return; }

        try {
            leaveAfterTransfer = true;
            HostTransferUI.OpenAutoProgress();
            LobbyHostTransfer.RequestAutoLeave(target, (success, reason) => {
                leaveAfterTransfer = false;
                // Complete arrives from a network receive callback; leave on the
                // next frame so the transport is not torn down under the packet.
                if (success) DeferFinishLeave();
                else CancelLeave(reason);
            });
        } catch (Exception ex) {
            leaveAfterTransfer = false;
            CancelLeave(ex.Message);
        }
    }

    // The confirm button is an OnGUI control; a synchronous scene load there
    // destroys the GUI stack mid-event. Defer the teardown one frame.
    static void DeferFinishLeave() {
        CharacterSelection.DeferUiAction("leave to singleplayer", FinishLeave);
    }

    internal static void FinishLeave() {
        leaveAfterTransfer = false;
        ReturnToSingleplayerUI.Close();
        HostTransferUI.CloseForLeave();
        UIMainMenu.SetOpen(false);
        Multiplayer.ShutdownNetwork();
        SinglePlayerIsolation.Reset();
        // Reload the menu so the Continue preview and vanilla run-settings
        // entries are rebuilt from the single-player path.
        SceneManager.LoadScene("PreGen");
    }

    internal static void CancelLeave(string reason) {
        leaveAfterTransfer = false;
        // A failed handoff may already have left the lobby. In that case the
        // local session is gone; finish the return instead of showing a stale
        // room menu.
        if (!Net.IsRunning) { DeferFinishLeave(); return; }
        ReturnToSingleplayerUI.Close();
        HostTransferUI.CloseForLeave();
        string prefix = Lang.Get("return_sp_transfer_failed", true) ?? "回到单人模式失败：";
        try { Multiplayer.DoMultiplayerStatusMessageLog(prefix + (reason ?? "未知错误")); } catch { }
    }

    internal static void Tick() {
        if (!leaveAfterTransfer) return;
        if (!Net.IsRunning || (!Net.IsHost && !LobbyHostTransfer.HasRequest && !LobbyHostTransfer.Busy))
            FinishLeave();
    }
}

[HarmonyPatch(typeof(AdaptiveButton), "Clicked")]
internal static class AdaptiveButton_Clicked_ReturnToSingleplayerPatch {
    [HarmonyPriority(Priority.First + 10)]
    static bool Prefix(AdaptiveButton __instance, bool __runOriginal) {
        if (!__runOriginal) return false;
        return LeaveToSingleplayer.Intercept(__instance.action);
    }
}

[HarmonyPatch(typeof(Plugin), "Update")]
internal static class LeaveToSingleplayerTick {
    static void Postfix() => LeaveToSingleplayer.Tick();
}
