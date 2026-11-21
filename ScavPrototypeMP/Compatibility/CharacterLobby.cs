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
    static int startClickFrame = -1;
    static readonly Dictionary<knetid, string> rosterLog = new();

    // In a lobby there is no body, so the stock character page is only opened
    // from the "Choose character" button (host and client alike).
    internal static bool InLobby => Util.GameIsFull() && Net.IsRunning && !Util.IsInWorld();
    internal static bool ShouldShowPanel => InLobby && (Net.IsClient ? ClientSession : Net.IsServer) && PanelRequested && !worldStarting;

    internal static void OnTransportConnected() {
        if (!Util.GameIsFull()) return;
        Reset();
        if (!Net.IsRunning) return;
        CharacterSelection.RestoreRememberedChoice();
        ChipState.BeginSession();
        ClientSession = Net.IsClient;
        DraftChoice = CharacterSelection.Local;
        DraftUnchipped = ChipState.LocalUnchipped;
        replayWhenReady = Net.IsClient;
        PanelRequested = false;
        LockedBySave = false;
        // Clients are admitted immediately; the character page is opened by
        // the post-connect coroutine for both host and guest.
        if (Net.IsClient) CharacterSelection.SendRememberedChoice(false);
        Util.StartCoroutine(OpenCharacterPageAfterConnect(SessionVersion));
        log.l($"CHARACTER: lobby session started host={Net.IsServer} client={Net.IsClient}");
    }

    static IEnumerator OpenCharacterPageAfterConnect(int session) {
        if (Net.IsDedicatedServer) yield break;
        // Let the host player / client admission settle before showing the page.
        for (int i = 0; i < 20; i++) {
            if (session != SessionVersion || !InLobby) yield break;
            yield return null;
        }
        if (session != SessionVersion || !InLobby || !Net.IsRunning) yield break;
        ReopenPanel();
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

    internal static void OnLocalChoiceSent(bool chosen, bool userAction) {
        LocalChosen = chosen;
        replayWhenReady = ClientSession && !playerReady;
        // The automatic post-connect replay must not close a page the player
        // already opened; only an explicit choice/dismiss clears it.
        if (!chosen && userAction) PanelRequested = false;
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
        if (!InLobby || !Net.IsConnected) {
            log.warn("CHARACTER: panel reopen requires an active lobby session.");
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
        DraftChoice = CharacterSelection.Local;
        DraftUnchipped = localPlayer != null ? localPlayer.modUnchipped : ChipState.LocalUnchipped;
        PanelRequested = true;
        log.l($"CHARACTER: stock character page reopened from mod menu host={Net.IsServer}");
        StockCharacterSelectUI.Show();
        return true;
    }

    // The stock page's Start is a Confirm. Mark the frame so the start gate
    // recognizes this click and does not report a blocked run start.
    internal static void NoteStartClick() => startClickFrame = Time.frameCount;
    internal static bool StartClickInProgress => startClickFrame == Time.frameCount;

    internal static bool ConfirmPanel() {
        if (!CommitDraft()) return false;
        PanelRequested = false;
        int session = SessionVersion;
        CharacterSelection.DeferUiAction("return to room after ready", () => {
            if (session != SessionVersion || !Net.IsConnected || !Net.IsRunning ||
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
        ServerResetReady();
        StockCharacterSelectUI.HideForReset();
    }

    internal static void Publish(ScavPlayer player) {
        if (!Util.GameIsFull() || !Net.IsServer || player == null) return;
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
            entity.lobbyReady.Value = player.lobbyReady;
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
        player.lobbyReady = entity.lobbyReady.Value;
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

    internal static string ReadyLabel(ScavPlayer player) {
        if (player == null) return "";
        if (player.IsHost) return CharacterSelection.Text("character_host_label", "Host");
        if (!player.characterChosen) return CharacterSelection.Text("character_unset", "Not selected");
        return player.lobbyReady
            ? CharacterSelection.Text("character_ready", "Ready")
            : CharacterSelection.Text("character_not_ready", "Not ready");
    }

    internal static void LogRoster(ScavPlayer player) {
        if (!Util.GameIsFull() || player == null) return;
        string value = player.characterChosen ? player.characterSelected.ToString() : "unset";
        string state = $"{player.playerName}={value} chosen={(player.characterChosen ? "true" : "false")} ready={player.lobbyReady} unchipped={player.modUnchipped}";
        if (rosterLog.TryGetValue(player.playerId, out var previous) && previous == state) return;
        rosterLog[player.playerId] = state;
        log.l("CHARACTER: roster " + state);
    }

    internal static bool LocalPlayerChosen {
        get {
            var player = ScavPlayer.LocalPlayer;
            return player != null && player.characterChosen;
        }
    }

    internal static bool LocalPlayerReady {
        get {
            var player = ScavPlayer.LocalPlayer;
            return player != null && player.lobbyReady;
        }
    }

    internal static bool HostChosen {
        get {
            var player = ScavPlayer.LocalPlayer;
            return player != null && player.characterChosen;
        }
    }

    // Players who still have to pick or ready up. The host never readies.
    internal static int MissingReadyCount {
        get {
            if (!Util.GameIsFull() || !Net.IsServer || Net.IsDedicatedServer || Util.IsInWorld()) return 0;
            int missing = 0;
            foreach (var player in ScavPlayer.ClientIdToPlayerDict.Values) {
                if (player == null || player.IsHost) continue;
                if (!player.characterChosen || !player.lobbyReady) missing++;
            }
            return missing;
        }
    }

    internal static int ReadySummary(out int totalGuests) {
        totalGuests = 0;
        int ready = 0;
        if (!Util.GameIsFull() || !Net.IsServer || Util.IsInWorld()) return 0;
        foreach (var player in ScavPlayer.ClientIdToPlayerDict.Values) {
            if (player == null || player.IsHost) continue;
            totalGuests++;
            if (player.characterChosen && player.lobbyReady) ready++;
        }
        return ready;
    }

    internal static void ToggleReady() {
        if (!Util.GameIsFull() || !Net.IsClient || Util.IsInWorld() || !Net.IsConnected) return;
        if (!LocalPlayerChosen) {
            Multiplayer.DoMultiplayerStatusMessageLog(CharacterSelection.Text("character_ready_need_character", "Choose a character first."));
            return;
        }
        bool next = !LocalPlayerReady;
        // Flip immediately for the button label; the server's SyncVar confirms.
        var local = ScavPlayer.LocalPlayer;
        if (local != null) local.lobbyReady = next;
        var writer = Net.CreateWriter(NetmsgId.SERVER_SetLobbyReady);
        writer.Put(next);
        Net.Client_Send(LiteNetLib.DeliveryMethod.ReliableOrdered, writer);
        log.l($"CHARACTER: sent lobby ready={next}");
    }

    internal static void ServerSetReady(knetid id, bool ready) {
        if (!Util.GameIsFull() || !Net.IsServer) return;
        if (!ScavPlayer.ClientIdToPlayerDict.TryGetValue(id, out var player) || player == null) return;
        if (player.IsHost) return;
        if (ready && !player.characterChosen) {
            log.warn($"CHARACTER: player={player.playerName} tried to ready without a character");
            return;
        }
        player.lobbyReady = ready;
        var entity = player.BasePlayer;
        if (entity != null) entity.lobbyReady.Value = ready;
        LogRoster(player);
        log.l($"CHARACTER: player={player.playerName} lobbyReady={ready}");
    }

    internal static void ServerResetReady() {
        if (!Util.GameIsFull() || !Net.IsServer) return;
        foreach (var player in ScavPlayer.ClientIdToPlayerDict.Values) {
            if (player == null) continue;
            player.lobbyReady = false;
            var entity = player.BasePlayer;
            if (entity != null) entity.lobbyReady.Value = false;
        }
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
        LobbyVanillaMenu.Restore();
    }
}
#endif
