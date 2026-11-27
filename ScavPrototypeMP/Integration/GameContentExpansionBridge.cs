using CasualtiesTogetherUtils;
using LiteNetLib;
using LiteNetLib.Utils;
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
    public const int ShardCatBulb = 7;

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

    // A non-null hook that returns false means the hook declined this event;
    // the caller should run the vanilla path. GCE's glass hook returns true
    // even at the shard cap, so its vanilla shrapnel = 5 fallback must never
    // run while the hook is present.
    public static int GetShardTotal(Limb limb) {
        var hook = ShardTotal;
        return hook != null ? hook(limb) : Mathf.Clamp(limb != null ? limb.shrapnel : 0, 0, 5);
    }

    public static int[] GetShardCategories(Limb limb) {
        var hook = ShardCategories;
        if (hook != null) return hook(limb);
        int total = GetShardTotal(limb);
        var result = new int[total];
        for (int i = 0; i < total; i++) result[i] = 3; // legacy generic metal
        return result;
    }

    public static bool TryShardHit(Limb limb, int category) {
        var hook = ShardHit;
        return hook != null && hook(limb, category);
    }

    public static bool TryShardGlass(Limb limb) {
        var hook = ShardGlass;
        return hook != null && hook(limb);
    }

    public static bool TryShardExplosion(Limb limb, float shrapnelChance, float bleedChance) {
        var hook = ShardExplosion;
        return hook != null && hook(limb, shrapnelChance, bleedChance);
    }

    public static bool TryShardNeedle(Limb limb) {
        var hook = ShardNeedle;
        return hook != null && hook(limb);
    }

    public static bool HasShardNeedle(Limb limb) {
        var hook = ShardHasNeedle;
        return hook != null ? hook(limb) : limb != null && limb.shrapnel == 1;
    }

    public static bool RemoveShard(Limb limb, int category, bool addBleed) {
        var hook = ShardRemoved;
        return hook != null && hook(limb, category, addBleed);
    }

    public static bool AddShard(Limb limb, int category) {
        var hook = ShardAdded;
        return hook != null && hook(limb, category);
    }

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

    // GCE team-spawn sites generate a lifepod/background on the host after
    // world generation. The client's seeded world does not contain it, so
    // register that exact instance with the live entity stream; the next
    // world snapshot then captures/restores it normally.
    public static void RegisterGeneratedBackgrounds(float x, float y, float radius) {
#if FULLGAME
        BackgroundSnapshot.RegisterGenerated(new Vector2(x, y), radius);
#endif
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

    // ---- Generic GCE state messages ---------------------------------------
    // GCE owns the payload layout. Together only carries a named byte[]:
    //   client -> host: handler(senderPlayerId, payload), then relayed to the
    //                   other clients;
    //   host -> clients: SendState broadcasts to every client.
    // Keep the key stable and < 64 bytes; payload is capped at 8 KiB.
    const int GceStateMaxBytes = 8192;
    static readonly Dictionary<string, Action<int, byte[]>> gceStateHandlers = new();

    public static void RegisterStateHandler(string key, Action<int, byte[]> handler) {
        if (string.IsNullOrEmpty(key) || key.Length > 64 || handler == null) return;
        if (gceStateHandlers.ContainsKey(key)) {
            gceStateHandlers[key] = handler;
            return;
        }
        gceStateHandlers[key] = handler;
        Multiplayer.RegisterCustomServerReceiver(key, (plr, reader) => {
            byte[] data = ReadStatePayload(reader);
            if (data == null) return;
            int sender = plr != null ? plr.playerId.id : 0;
            DispatchState(key, sender, data);
            // Host receives a client's state and fans it out to the others.
            if (Net.IsServer && Net.IsRunning && plr != null) {
                var writer = Multiplayer.CreateNamedWriter(key);
                writer.Put(data);
                Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer,
                    ServerMain.GetListOfClientIdsExceptThis(plr.playerId));
            }
        });
        Multiplayer.RegisterCustomClientReceiver(key, reader => {
            byte[] data = ReadStatePayload(reader);
            if (data != null) DispatchState(key, 0, data);
        });
    }

    public static void SendState(string key, byte[] data) {
        if (string.IsNullOrEmpty(key) || key.Length > 64 || data == null ||
            data.Length > GceStateMaxBytes || !Net.IsRunning) return;
        var writer = Multiplayer.CreateNamedWriter(key);
        writer.Put(data);
        if (Net.IsClient) {
            Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        } else if (Net.IsServer) {
            Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, ServerMain.AllClientIds);
        }
    }

    static byte[] ReadStatePayload(NetDataReader reader) {
        try {
            byte[] data = reader.GetBytesWithLength();
            return data != null && data.Length <= GceStateMaxBytes ? data : null;
        } catch {
            return null;
        }
    }

    static void DispatchState(string key, int sender, byte[] data) {
        if (gceStateHandlers.TryGetValue(key, out var handler) && handler != null) {
            try { handler(sender, data); } catch (Exception ex) {
                log.warn("[gce-bridge] state handler " + key + " failed: " + ex.Message);
            }
        }
    }

    // GCE firearm state uses the network sync id of the gun item so a client
    // can find its own copy of the same Item on the other machines.
    public static int GetItemSyncId(Item item) {
        if (item == null) return 0;
        try {
            return NetObjectRegistry.TryGetSyncInfo(item.gameObject, out SyncInfo si) && si != null
                ? si.syncId.id
                : 0;
        } catch {
            return 0;
        }
    }

    public static Item FindItemBySyncId(int syncId) {
        if (syncId <= 0 || syncId > ushort.MaxValue) return null;
        try {
            if (!NetObjectRegistry.TryGetSyncInfo((knetid)(ushort)syncId, out SyncInfo si) ||
                si == null || si.go == null) return null;
            return si.go.GetComponent<Item>();
        } catch {
            return null;
        }
    }
}
