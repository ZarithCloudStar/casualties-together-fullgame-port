using CasualtiesTogetherUtils;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Together.Integration;

// Public, primitive-only bridge for the optional "Game Content Expansion &
// Optimization" plugin. The other assembly must not reference CasualtiesMP, so
// it reflects into this class by name. Keep signatures free of Together types.
//
// This bridge deliberately contains no OtherMods manager and no team-spawn
// behavior. GCE owns those; Together only provides the synced rule field, the
// UI hooks and primitive player/team access.
public static class GameContentExpansionBridge {
    public const string ModGuid = "com.cloudawolf.gamecontentexpansion";
    public const int ApiVersion = 2;

    // Assigned by the optional plugin. Called inside the rules scroll view.
    public static Action DrawRulesBottomSection;
    public static Action<string, float, bool> DrawRuleRowAfter;

    public static bool IsReady => Plugin.SUCCESFULLY_INITIALIZED && !Plugin.FORCE_DISABLE_MP_MOD;
    public static int BridgeApiVersion => ApiVersion;
    public static string TogetherVersion => Plugin.FULL_MOD_VERSION ?? "";
    public static bool TeamsEnabled => Multiplayer.rules.Teams;
    public static bool CanEditRules => Con.CanExecuteAdminCommands();
    public static bool IsServer => Net.IsServer;
    public static bool IsClient => Net.IsClient;
    public static bool IsRunning => Net.IsRunning;

    public static bool WorldSnapshotMode {
        get {
#if FULLGAME
            return WorldSnapshotTransfer.SnapshotMode;
#else
            return false;
#endif
        }
    }

    public static bool TeamSpawnPositions {
        get => Multiplayer.rules.TeamSpawnPositions;
        set {
            if (Multiplayer.rules.TeamSpawnPositions == value) return;
            if (Net.IsClient) {
                if (Con.client_isadmin) Con.ExecuteCommandAdminNoLog($"rule TeamSpawnPositions {value.ToStringInvariant()}");
                return;
            }
            Multiplayer.rules.TeamSpawnPositions = value;
            Multiplayer.ApplyGameRules();
        }
    }

    // ---- Primitive player/team surface ------------------------------------

    // Flat triples: [playerId, packed RGB, flags].
    // flags: 1 has body, 2 alive, 4 local player.
    public static int[] PlayerSnapshot() {
        var players = ScavPlayer.AllPlayers;
        if (players == null) return Array.Empty<int>();
        var result = new List<int>(players.Count() * 3);
        foreach (var player in players) {
            if (player == null) continue;
            int flags = 0;
            if (player.body != null) {
                flags |= 1;
                if (player.body.alive) flags |= 2;
            }
            if (ReferenceEquals(player, ScavPlayer.LocalPlayer)) flags |= 4;
            result.Add(player.playerId.id);
            result.Add(player.playerColor.Hash());
            result.Add(flags);
        }
        return result.ToArray();
    }

    public static string PlayerName(int playerId) => FindPlayer(playerId)?.playerName ?? "";

    public static bool TryGetPlayerPosition(int playerId, out float x, out float y) {
        x = y = 0f;
        var player = FindPlayer(playerId);
        if (player == null) return false;
        x = player.pos.x;
        y = player.pos.y;
        return true;
    }

    public static bool TryGetSharedSpawnPosition(out float x, out float y) {
        x = y = 0f;
        if (!Body_PlaceBody_MultiplayerPatch.has_spawn_location) return false;
        var position = Body_PlaceBody_MultiplayerPatch.SpawnLocation;
        x = position.x;
        y = position.y;
        return true;
    }

    public static bool TeleportPlayer(int playerId, float x, float y) {
        if (!Net.IsServer) return false;
        var player = FindPlayer(playerId);
        if (player == null) return false;
        player.Server_TeleportCharacter(new Vector2(x, y));
        return true;
    }

    public static bool SetPlayerColor(int playerId, int rgb) {
        if (!Net.IsServer) return false;
        var player = FindPlayer(playerId);
        if (player == null) return false;
        var color = new Color24((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        try {
            player.ApplyNameAndColor(player.playerName, color);
        } catch {
            player.playerColor = color;
        }
        return true;
    }

    static ScavPlayer FindPlayer(int playerId) {
        var players = ScavPlayer.AllPlayers;
        if (players == null) return null;
        foreach (var player in players) {
            if (player != null && player.playerId.id == playerId) return player;
        }
        return null;
    }

    // ---- UI helpers --------------------------------------------------------

    public static void SetTooltip(string name, string desc) => UIBullshit._GUI_SetTooltip(name, desc);
    public static bool CheckCursorOverlap(Rect screenRect) => UIBullshit.CheckCursorOverlap(screenRect);
    public static void ReportRuleLabelWidth(float width) => UIMainMenu.ReportExternalRuleLabelWidth(width);
}
