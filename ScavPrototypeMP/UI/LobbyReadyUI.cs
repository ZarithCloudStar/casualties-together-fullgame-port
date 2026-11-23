#if FULLGAME
using CasualtiesTogetherUtils;
using UnityEngine;

namespace Together;

// Host: Start adventure / Continue adventure, gated on a chosen character and
// every guest being ready. Client: a single Ready / Cancel ready toggle.
internal static class LobbyReadyUI {
    static bool hasSave;
    static float nextSaveCheck;

    static string Text(string key, string fallback) => CharacterSelection.Text(key, fallback);

    static bool HasSaveCached {
        get {
            if (Time.unscaledTime >= nextSaveCheck) {
                nextSaveCheck = Time.unscaledTime + 2f;
                try { hasSave = SaveSlots.HasSave; } catch { hasSave = false; }
            }
            return hasSave;
        }
    }

    internal static void DrawHostStartButtons(float height) {
        if (!Util.GameIsFull() || !Net.IsServer || Net.IsDedicatedServer || Util.IsInWorld() || !Net.IsRunning) return;

        bool previous = GUI.enabled;
        bool chosen = CharacterLobby.HostChosen;
        int missing = CharacterLobby.MissingReadyCount;
        bool canStart = chosen && missing == 0;

        string startTip = !chosen
            ? Text("character_start_need_character", "Choose a character before starting.")
            : missing > 0
                ? CharacterReadyGate.ReadyText(missing)
                : "";
        GUI.enabled = previous && canStart;
        if (GUILayout.Button(new GUIContent(Text("start_adventure", "Start adventure"), startTip), GUILayout.Height(height))) {
            if (PreRunScript.instance != null) PreRunScript.instance.StartRun();
        }
        GUI.enabled = previous;

        bool hasSaveNow = HasSaveCached;
        string continueTip = !chosen
            ? Text("character_start_need_character", "Choose a character before starting.")
            : missing > 0 ? CharacterReadyGate.ReadyText(missing)
            : !hasSaveNow ? Text("continue_no_save", "No saved run to continue.")
            : "";
        GUI.enabled = previous && canStart && hasSaveNow;
        if (GUILayout.Button(new GUIContent(Text("continue_adventure", "Continue adventure"), continueTip), GUILayout.Height(height))) {
            CharacterReadyGate.RequestLoad();
        }
        GUI.enabled = previous;
    }

    // Keep the ready summary below the disconnect / invite row so it cannot
    // push those actions down, and above the last-status message.
    internal static void DrawReadySummary() {
        if (!Util.GameIsFull() || !Net.IsServer || Net.IsDedicatedServer || Util.IsInWorld() || !Net.IsRunning) return;

        GUILayout.Space(4 * UIMainMenu.GetMenuUIScale());
        int ready = CharacterLobby.ReadySummary(out int guests);
        if (guests > 0) {
            GUILayout.Label(string.Format(Text("lobby_ready_summary", "{0}/{1} players ready"), ready, guests));
        }
        // Per-player status directly in the room panel; the separate players
        // tab still shows the same information with ping and admin controls.
        foreach (var player in ScavPlayer.AllPlayers) {
            if (player == null || player.IsLocal) continue;
            GUILayout.Label(player.playerName + "  " + CharacterLobby.ReadyLabel(player));
        }
    }

    internal static void DrawClientReadyButton(float height) {
        if (!Util.GameIsFull() || !Net.IsClient || Util.IsInWorld() || !Net.IsRunning || !Net.IsConnected) return;

        bool previous = GUI.enabled;
        bool chosen = CharacterLobby.LocalPlayerChosen;
        bool ready = CharacterLobby.LocalPlayerReady;
        string text = ready
            ? Text("character_cancel_ready", "Cancel ready")
            : Text("character_ready", "Ready");
        string tip = chosen ? "" : Text("character_ready_need_character", "Choose a character first.");
        GUI.enabled = previous && chosen;
        if (GUILayout.Button(new GUIContent(text, tip), GUILayout.Height(height))) {
            CharacterLobby.ToggleReady();
        }
        GUI.enabled = previous;
    }
}
#endif
