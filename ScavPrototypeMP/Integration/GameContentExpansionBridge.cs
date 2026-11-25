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
    public const int ApiVersion = 6;

    public const int ShardCatNeedle = 0;
    public const int ShardCatGlass = 1;
    public const int ShardCatExplosion = 2;
    public const int ShardCatGuard = 3;
    public const int ShardCatPvP = 4;
    public const int ShardCatGunmine = 5;
    public const int ShardCatTurret = 6;

    // GCE shard module hooks. All are optional; null means the module is not
    // installed/enabled and the vanilla shrapnel path must run.
    public static Func<Limb, int, bool> ShardHit;
    public static Func<Limb, bool> ShardGlass;
    public static Func<Limb, float, float, bool> ShardExplosion;
    public static Func<Limb, bool> ShardNeedle;
    public static Func<Limb, int> ShardTotal;
    public static Func<Limb, int[]> ShardCategories;
    public static Func<Limb, int, bool, bool> ShardRemoved;
    public static Func<Limb, int, bool> ShardAdded;
    public static Func<Limb, bool> ShardHasNeedle;

    public static int GetShardTotal(Limb limb) =>
        ShardTotal != null ? ShardTotal(limb) : Mathf.Clamp(limb != null ? limb.shrapnel : 0, 0, 5);

    public static int[] GetShardCategories(Limb limb) {
        var hook = ShardCategories;
        if (hook != null) return hook(limb);
        int total = GetShardTotal(limb);
        var result = new int[total];
        for (int i = 0; i < total; i++) result[i] = 3; // legacy generic metal
        return result;
    }

    public static bool TryShardHit(Limb limb, int category) =>
        ShardHit != null && ShardHit(limb, category);

    public static bool TryShardGlass(Limb limb) =>
        ShardGlass != null && ShardGlass(limb);

    public static bool TryShardExplosion(Limb limb, float shrapnelChance, float bleedChance) =>
        ShardExplosion != null && ShardExplosion(limb, shrapnelChance, bleedChance);

    public static bool TryShardNeedle(Limb limb) =>
        ShardNeedle != null && ShardNeedle(limb);

    public static bool HasShardNeedle(Limb limb) =>
        ShardHasNeedle != null ? ShardHasNeedle(limb) : limb != null && limb.shrapnel == 1;

    public static bool RemoveShard(Limb limb, int category, bool addBleed) =>
        ShardRemoved != null && ShardRemoved(limb, category, addBleed);

    public static bool AddShard(Limb limb, int category) =>
        ShardAdded != null && ShardAdded(limb, category);

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

    // Spectator/ending cameras can point at a body the local player does not
    // own. GCE checks this before offering inventory gestures on the camera
    // body. Standalone/single-player is always true.
    public static bool IsLocalCameraBody {
        get {
            var cam = PlayerCamera.main;
            return cam != null && cam.body != null && Util.IsBodyLocal(cam.body);
        }
    }

    public static bool WorldSnapshotMode {
        get {
#if FULLGAME
            return WorldSnapshotTransfer.SnapshotMode;
#else
            return false;
#endif
        }
    }

    // GCE module mask. Host writes; clients read it from the synced rules.
    public static int GCEModules {
        get => Multiplayer.rules.GCEModules;
        set {
            if (!Net.IsServer) return;
            byte next = (byte)Mathf.Clamp(value, 0, 255);
            if (Multiplayer.rules.GCEModules == next) return;
            Multiplayer.rules.GCEModules = next;
            Multiplayer.ApplyGameRules();
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

    // GCE quick pickup can consume a middle-click point-finger press when the
    // pointer is over an item it actually picks up or drops. UIBullshit checks
    // this for the current frame; GCE clears it on its next update.
    public static bool SuppressPointFinger;
    public static int SuppressPointFingerFrame = -1;
    public static int PointFingerKey => (int)KeyBinds.GetBind(CoopKeybinds.pointfingerat);

    // GCE quick access moves items through Body/Container directly instead of
    // PlayerCamera.HandleReleaseDragging. Hold the same flag the native release
    // path holds so Container.LoadItem/UnloadItem announce their new parent.
    public static void BeginLocalContainerAction() {
        PlayerCamera_TryPerformInventoryAction_MultiplayerPatch.is_inside_LocalContainerAction = true;
    }

    public static void EndLocalContainerAction() {
        PlayerCamera_TryPerformInventoryAction_MultiplayerPatch.is_inside_LocalContainerAction = false;
    }

    // A GCE batch detaches an item from a body slot and seats it in another
    // slot in the same frame. MoveCommit.Push/EndDrag make that one gesture so
    // the intermediate "on the ground" parent is never announced.
    static bool gceItemGesturePushed;

    public static void BeginItemGesture() {
        // Only the client side needs MoveCommit; the host already owns the
        // move and re-applying it as a local attempt would be redundant.
        if (!Net.IsClient || gceItemGesturePushed) return;
        MoveCommit.Push();
        gceItemGesturePushed = true;
    }

    public static void EndItemGesture(Item item) {
        if (!gceItemGesturePushed) return;
        gceItemGesturePushed = false;
        MoveCommit.EndDrag(item);
    }
}
