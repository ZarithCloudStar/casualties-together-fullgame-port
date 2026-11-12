#if FULLGAME
using CasualtiesTogetherUtils;
using System;

namespace Together;

// Per-save choice and the vanilla per-player gameplay state share one source.
internal static class ChipState {
    internal static bool LocalUnchipped { get; private set; }

    internal static string Label(bool unchipped) => CharacterSelection.Text(
        unchipped ? "chip_unchipped" : "chip_chipped", unchipped ? "Unchipped" : "Chipped");

    internal static void AdoptLocal(bool unchipped) => LocalUnchipped = unchipped;

    internal static void BeginSession() => LocalUnchipped = false;

    internal static void ApplyGameplay(ScavPlayer player) {
        if (!Util.GameIsFull() || !Net.IsRunning || player == null) return;
        Multiplayer.rules.UnchippedIsIndividual = true;
        player.SetUnchipped(player.modUnchipped);
        if (player.IsLocal) AdoptLocal(player.modUnchipped);
    }

    // Call only on the server. A disconnected/null player is not a chipped
    // player; throw so future ending rules cannot accidentally admit one.
    internal static bool IsUnchipped(ScavPlayer player) {
        if (!Net.IsServer) throw new InvalidOperationException("ChipState queries require the authoritative server.");
        if (player == null) throw new ArgumentNullException(nameof(player));
        return player.modUnchipped;
    }

    internal static bool HostIsUnchipped {
        get {
            if (!Net.IsServer) throw new InvalidOperationException("ChipState queries require the authoritative server.");
            foreach (var player in ScavPlayer.ClientIdToPlayerDict.Values)
                if (player != null && player.IsHost) return IsUnchipped(player);
            throw new InvalidOperationException("Host is not registered.");
        }
    }

    internal static void ApplyServer(ScavPlayer player, bool unchipped, bool persist) {
        if (!Net.IsServer || player == null) return;
        player.modUnchipped = unchipped;
        if (player.BasePlayer != null) player.BasePlayer.modUnchipped.Value = unchipped;
        ApplyGameplay(player);
        if (persist) CharacterRunMemory.SetChip(CharacterRunMemory.PlayerKey(player.SteamId, player.playerName), unchipped);
        log.l($"[chip] server player={player.playerName} unchipped={unchipped} save={CharacterRunMemory.CurrentSeed} persistRequested={persist}");
    }

    // Settings UI also gives the host a choice without changing vanilla UI.
    internal static bool Choose(bool unchipped) {
        var player = ScavPlayer.LocalPlayer;
        if (CharacterLobby.LockedBySave || player?.body != null) return false;
        AdoptLocal(unchipped);
        return CharacterSelection.Choose(CharacterSelection.Local);
    }
}
#endif
