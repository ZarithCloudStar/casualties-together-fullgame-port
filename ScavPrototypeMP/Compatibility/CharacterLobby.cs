#if FULLGAME
using CasualtiesTogetherUtils;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Together;

// Room confirmation is separate from the remembered character and from Body
// lifetime. All lobby state lives on BasePlayer, which exists without a Body.
internal static class CharacterLobby {
    internal static bool ClientSession { get; private set; }
    internal static bool PanelRequested { get; private set; }
    internal static bool LocalChosen { get; private set; }
    // Kept for old call sites/tests; a save binding no longer hides the panel.
    // The server still reapplies the save binding before world start.
    internal static bool LockedBySave { get; private set; }
    internal static byte DraftChoice { get; private set; }
    internal static bool DraftUnchipped { get; private set; }
    internal static int SessionVersion { get; private set; }
    static bool playerReady;
    static bool replayWhenReady;
    static bool finishing;
    static bool worldStarting;
    static readonly Dictionary<knetid, string> rosterLog = new();

    internal static bool ShouldShowPanel => Util.GameIsFull() && ClientSession && Net.IsClient && Net.IsConnected && PanelRequested && !worldStarting;

    internal static void OnTransportConnected() {
        if (!Util.GameIsFull()) return;
        Reset();
        if (!Net.IsClient || !Net.IsRunning) return;
        CharacterSelection.RestoreRememberedChoice();
        ChipState.BeginSession();
        ClientSession = true;
        DraftChoice = CharacterSelection.Local;
        DraftUnchipped = ChipState.LocalUnchipped;
        replayWhenReady = true;
        // The server may already know this player's pick for the current save and
        // admit before the page is ready. That must not hide the page: the save
        // binding is authoritative on the server, but the client may still look
        // at/choose a different preview until the host starts the world.
        PanelRequested = false;
        LockedBySave = false;
        CharacterSelection.SendRememberedChoice(false);
        Util.StartCoroutine(ShowPanelAfterConnect());
    }

    static IEnumerator ShowPanelAfterConnect() {
        int session = SessionVersion;
        float started = Time.unscaledTime;
        while (Time.unscaledTime - started < 1.2f) {
            if (session != SessionVersion || !ClientSession || !Net.IsClient || !Net.IsConnected || worldStarting) yield break;
            yield return null;
        }
        if (session != SessionVersion || !ClientSession || !Net.IsClient || !Net.IsConnected || worldStarting) yield break;
        PanelRequested = true;
        log.l("CHARACTER: stock character page opened");
        StockCharacterSelectUI.Show();
    }

    internal static void OnPlayerReady(ScavPlayer player) {
        if (!Util.GameIsFull()) return;
        LogRoster(player);
        if (!ClientSession || !Net.IsClient || player == null || !player.IsLocal) return;
        ChipState.AdoptLocal(player.modUnchipped);
        SessionJoinLog.Success();
        playerReady = true;
        // Server registration can finish after OnTransportConnected. Replay
        // once after our player snapshot, preserving confirm/dismiss decisions.
        if (replayWhenReady) {
            replayWhenReady = false;
            CharacterSelection.SendRememberedChoice(LocalChosen);
        }
    }

    internal static void OnLocalChoiceSent(bool chosen) {
        LocalChosen = chosen;
        replayWhenReady = ClientSession && !playerReady;
        if (!chosen) PanelRequested = false;
    }

    internal static bool Preview(int chr) {
        if (!ShouldShowPanel || chr < 0 || chr > 2) return false;
        if (CharacterSelection.CommandLineOverride.HasValue) chr = CharacterSelection.Local;
        DraftChoice = (byte)chr;
        return true;
    }

    internal static bool PreviewChip(bool unchipped) {
        if (!ShouldShowPanel) return false;
        DraftUnchipped = unchipped;
        return true;
    }

    internal static byte SafeDraftChoice() {
        if (DraftChoice > 2) {
            log.warn($"CHARACTER: invalid panel draft {DraftChoice}; clamped to 2.");
            DraftChoice = 2;
        }
        return DraftChoice;
    }

    internal static bool ReopenPanel() {
        if (!Util.GameIsFull()) return false;
        if (!Net.IsClient || !Net.IsConnected || !ClientSession) {
            log.warn("CHARACTER: panel reopen requires a connected client.");
            Multiplayer.DoMultiplayerStatusMessageLog(CharacterSelection.Text("character_need_connection", "Connect to a server first."));
            return false;
        }
        if (finishing || worldStarting) return false;
        var localPlayer = ScavPlayer.LocalPlayer;
        if (localPlayer != null && localPlayer.body != null) {
            log.warn("CHARACTER: panel reopen refused; the local player already has a body in this world.");
            Multiplayer.DoMultiplayerStatusMessageLog(CharacterSelection.Text("character_locked_in_game", "You cannot change your character after entering the world."));
            return false;
        }
        PanelRequested = true;
        log.l("CHARACTER: stock character page reopened from mod menu");
        StockCharacterSelectUI.Show();
        return true;
    }

    internal static bool ConfirmPanel() {
        if (!CommitDraft()) return false;
        PanelRequested = false;
        int session = SessionVersion;
        CharacterSelection.DeferUiAction("return to room after ready", () => {
            if (session != SessionVersion || !Net.IsConnected || !Net.IsClient ||
                worldStarting || PanelRequested || !Util.IsInMainMenu()) return;
            StockCharacterSelectUI.Hide();
            UIMainMenu.OpenConnectedRoom();
        });
        return true;
    }

    internal static bool CommitDraft() {
        if (!ShouldShowPanel || finishing) return false;
        finishing = true;
        try {
            byte chr = SafeDraftChoice();
            log.l($"CHARACTER: lobby panel confirm begin chr={chr}");
            ChipState.AdoptLocal(DraftUnchipped);
            if (!CharacterSelection.Choose(chr)) return false;
            log.l($"CHARACTER: stock page confirmed chr={CharacterSelection.Local}");
            return true;
        } finally { finishing = false; }
    }

    internal static void DismissPanel() {
        if (!ShouldShowPanel || finishing) return;
        finishing = true;
        try {
            PanelRequested = false;
            log.l($"CHARACTER: lobby panel dismissed chosen={LocalChosen}");
        } finally { finishing = false; }
    }

    internal static void CloseForWorldStart() {
        worldStarting = true;
        PanelRequested = false;
        StockCharacterSelectUI.HideForReset();
    }

    internal static void Publish(ScavPlayer player) {
        if (!Util.GameIsFull() || !Net.IsServer || player == null) return;
        // The host picks through the vanilla pre-run screen, which never calls the
        // mod's Choose(), so its player would stay "unset" in the roster forever.
        if (player.IsLocal && !player.characterChosen) {
            player.characterSelected = CharacterSelection.Local;
            player.characterChosen = true;
        }
        var entity = player.BasePlayer;
        // A second player object (re-creation/sync) starts with empty fields. The
        // entity already carries the granted character, so never let that copy
        // wipe the roster back to "unset".
        if (!player.characterChosen && entity != null && entity.characterChosen.Value && entity.lobbyCharacterSelected.Value <= 2) {
            player.characterSelected = entity.lobbyCharacterSelected.Value;
            player.characterChosen = true;
        }
        if (entity != null) {
            entity.lobbyCharacterSelected.Value = player.characterSelected;
            entity.characterChosen.Value = player.characterChosen;
            entity.modUnchipped.Value = player.modUnchipped;
        }
        LogRoster(player);
    }

    internal static void ApplySynced(BasePlayer entity) {
        if (!Util.GameIsFull() || !Net.IsClient || entity == null) return;
        var player = entity.player;
        if (player == null) return;
        player.characterSelected = entity.lobbyCharacterSelected.Value;
        player.characterChosen = entity.characterChosen.Value;
        player.modUnchipped = entity.modUnchipped.Value;
        ChipState.ApplyGameplay(player);
        // The server may override a lobby pick with the save's binding. Follow it
        // locally too, but only for our own player: adopting a remote player's
        // character would make the local body spawn as theirs.
        if (entity.characterChosen.Value && player.IsLocal) CharacterSelection.AdoptBound(entity.lobbyCharacterSelected.Value);
        LogRoster(player);
    }

    internal static string RosterLabel(ScavPlayer player) {
        var value = player != null && player.characterChosen && player.characterSelected <= 2
            ? LobbyCharacterSelection.CleanName(player.characterSelected)
            : CharacterSelection.Text("character_unset", "Not selected");
        return string.Format(CharacterSelection.Text("character_roster", "Character: {0}"), value);
    }

    internal static void LogRoster(ScavPlayer player) {
        if (!Util.GameIsFull() || player == null) return;
        string value = player.characterChosen ? player.characterSelected.ToString() : "unset";
        string state = $"{player.playerName}={value} chosen={(player.characterChosen ? "true" : "false")} unchipped={player.modUnchipped}";
        if (rosterLog.TryGetValue(player.playerId, out var previous) && previous == state) return;
        rosterLog[player.playerId] = state;
        log.l("CHARACTER: roster " + state);
    }

    internal static void Forget(ScavPlayer player) {
        if (player != null) rosterLog.Remove(player.playerId);
    }

    internal static void Reset() {
        SessionVersion++;
        ClientSession = PanelRequested = LocalChosen = playerReady = replayWhenReady = false;
        LockedBySave = false;
        finishing = worldStarting = false;
        DraftChoice = 0;
        DraftUnchipped = false;
        rosterLog.Clear();
        StockCharacterSelectUI.HideForReset();
    }
}
#endif
