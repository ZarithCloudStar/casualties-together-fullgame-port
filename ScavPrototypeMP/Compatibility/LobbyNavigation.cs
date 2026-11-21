#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using UnityEngine;

namespace Together;

// Back/ESC from the vanilla run-settings screen (opened from the room) or the
// character page returns to the multiplayer room, not the vanilla main menu.
internal static class LobbyNavigation {
    static bool returnToRoomAfterRunSettings;
    static bool returnToRoomAfterGameSettings;

    internal static void OpenRunSettingsFromRoom() {
        returnToRoomAfterRunSettings = true;
        UIMainMenu.SetOpen(false);
        if (PreRunScript.instance != null) PreRunScript.instance.runSettingsScreen.SetActive(true);
    }

    internal static void OpenGameSettingsFromRoom() {
        returnToRoomAfterGameSettings = true;
        UIMainMenu.SetOpen(false);
        Util.OpenSettingsPanel(true);
    }

    internal static void OnGameSettingsClosed() {
        if (!returnToRoomAfterGameSettings) return;
        returnToRoomAfterGameSettings = false;
        if (!Util.GameIsFull() || Util.IsInWorld() || !Util.IsInMainMenu()) return;
        if (CharacterLobby.InLobby) {
            ReturnToRoom();
        } else {
            UIMainMenu.current_tab = UIMainMenu.menutab_settings;
            UIMainMenu.SetOpen(true);
        }
    }

    internal static void ReturnToRoom() {
        if (CharacterLobby.InLobby) UIMainMenu.OpenConnectedRoom();
    }

    internal static void Tick() {
        if (returnToRoomAfterGameSettings && SettingsMenu.instance == null) {
            OnGameSettingsClosed();
        }
        if (!returnToRoomAfterRunSettings) return;
        if (!Util.GameIsFull() || !Net.IsRunning || Util.IsInWorld() || !Util.IsInMainMenu() || PreRunScript.instance == null) {
            returnToRoomAfterRunSettings = false;
            return;
        }
        var screen = PreRunScript.instance.runSettingsScreen;
        if (screen == null || screen.activeSelf) return;
        returnToRoomAfterRunSettings = false;
        if (!CharacterLobby.ShouldShowPanel) ReturnToRoom();
    }
}

// The vanilla settings menu's Back/Close returns to the multiplayer room when
// it was opened from the room's Settings tab.
[HarmonyPatch(typeof(SettingsMenu), nameof(SettingsMenu.Close))]
internal static class SettingsMenu_Close_LobbyNavigationPatch {
    static void Postfix() {
        if (Util.GameIsFull()) LobbyNavigation.OnGameSettingsClosed();
    }
}
#endif
