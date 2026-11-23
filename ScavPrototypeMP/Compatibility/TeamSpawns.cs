using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Together;

// Server-side first-layer team spawns, used by the "Descend at Different
// Positions by Team" rule. The rule only takes effect while Teams is on. The host's normal starting pod
// stays where vanilla placed it and becomes the host team's pod; every other
// team gets an identical carved pod room at the same height, Spacing apart.
internal static class TeamSpawns {
    const float Spacing = 160f;
    const int RoomRadius = 30;   // same carving values as WorldGeneration.WorldPlacePlayer
    const int RingRadius = 36;
    const float EdgeMargin = 80f;

    sealed class Team {
        internal Color24 Color;
        internal readonly List<ScavPlayer> Players = new();
        internal int Count => Players.Count;
    }

    static readonly Dictionary<Color24, Vector2> sites = new();
    static WorldGeneration preparedWorld;
    static int preparedTraveled = -1;
    static bool ready;
    static bool initialFanOutUsed;

    internal static bool RuleEnabled => Multiplayer.rules.Teams && Multiplayer.rules.TeamSpawnPositions;

    internal static bool InFirstLayerContext() {
        var world = WorldGeneration.world;
        if (world == null) return false;
        if (world.biomeDepth != 0 || world.biomeOverride != WorldGeneration.OverrideSceneType.None) return false;
        if (world.totalTraveled > 0 || SaveSystem.loadedRun) return false;
#if FULLGAME
        if (WorldSnapshotTransfer.SnapshotMode) return false;
#endif
        return true;
    }

    // The spawn request loop waits on this after world generation and before
    // the server has built the team sites.
    internal static bool WaitingForSites() {
        if (!Net.IsServer || !RuleEnabled || !InFirstLayerContext()) return false;
        var world = WorldGeneration.world;
        return !ready || preparedWorld != world || preparedTraveled != world.totalTraveled;
    }

    internal static void BeginLayer() {
        ready = false;
        initialFanOutUsed = false;
        sites.Clear();
        preparedWorld = null;
        preparedTraveled = -1;
    }

    // The host body may be placed again after a mid-layer respawn. Only the
    // first placement of the layer is the starting fan-out; later ones must
    // keep the vanilla "spawn on the host" behaviour.
    internal static bool BeginInitialFanOut() {
        if (initialFanOutUsed) return false;
        if (!ready || !RuleEnabled || !InFirstLayerContext()) return false;
        initialFanOutUsed = true;
        return true;
    }

    // Called once per first-layer world placement on the server, after vanilla
    // placement and EnsureLayerSpawn. generateBaseSite is true when vanilla did
    // not create a starting pod (dedicated/dead-host path).
    internal static void TryPrepare(Vector2 baseSite, bool generateBaseSite) {
        if (!Net.IsServer || ready) return;
        if (!RuleEnabled || !InFirstLayerContext()) return;
        var world = WorldGeneration.world;
        try {
            if (!float.IsNaN(baseSite.x) && !float.IsInfinity(baseSite.x) &&
                !float.IsNaN(baseSite.y) && !float.IsInfinity(baseSite.y) &&
                Mathf.Abs(baseSite.x) <= (float)world.halfWidth &&
                Mathf.Abs(baseSite.y) <= (float)world.halfHeight) {
                Prepare(world, baseSite, generateBaseSite);
            } else {
                log.error("[teamspawn] invalid base spawn: " + baseSite);
            }
        } catch (Exception e) {
            log.error("[teamspawn] preparation failed: " + e);
        } finally {
            // Never leave spawn requests waiting on a half-built setup.
            ready = true;
            preparedWorld = world;
            preparedTraveled = world != null ? world.totalTraveled : -1;
        }
    }

    static void Prepare(WorldGeneration world, Vector2 baseSite, bool generateBaseSite) {
        // No vanilla host body ran the starting fan-out. Guests are placed by
        // their spawn requests instead; a later host body placement must not
        // pull them back to the team sites.
        if (generateBaseSite) initialFanOutUsed = true;
        var players = ScavPlayer.AllPlayers.Where(p => p != null).ToList();
        var teams = GroupTeams(players);
        EnforceMaxTeams(teams);
        teams.RemoveAll(t => t.Count == 0);

        if (teams.Count == 0) {
            sites[new Color24(255, 255, 255)] = baseSite;
            if (generateBaseSite) GenerateSite(world, baseSite);
            return;
        }

        var host = ScavPlayer.LocalPlayer;
        var baseColor = host != null && teams.Any(t => t.Color == host.playerColor)
            ? host.playerColor
            : teams.OrderByDescending(t => t.Count).ThenBy(t => t.Color.Hash()).First().Color;

        sites[baseColor] = baseSite;
        if (generateBaseSite) GenerateSite(world, baseSite);

        var otherTeams = teams
            .Where(t => t.Color != baseColor)
            .OrderBy(t => t.Color.Hash())
            .ToList();
        float[] offsets = { Spacing, -Spacing, Spacing * 2f, -Spacing * 2f };
        var usedX = new List<float> { baseSite.x };
        for (int i = 0; i < otherTeams.Count; i++) {
            var x = PickSiteX(world, baseSite, usedX, offsets[Math.Min(i, offsets.Length - 1)]);
            usedX.Add(x);
            var site = new Vector2(x, baseSite.y);
            sites[otherTeams[i].Color] = site;
            GenerateSite(world, site);
        }

        log.l($"[teamspawn] layer=0 teams={teams.Count} players={players.Count} base={baseSite:F3} sites={sites.Count}");
    }

    internal static bool TryResolve(ScavPlayer player, out Vector2 position) {
        position = default;
        if (!ready || player == null || !RuleEnabled || !InFirstLayerContext()) return false;
        var world = WorldGeneration.world;
        if (world == null || preparedWorld != world || preparedTraveled != world.totalTraveled) return false;
        return sites.TryGetValue(player.playerColor, out position);
    }

    internal static bool TryTeleport(ScavPlayer player) {
        if (!TryResolve(player, out var position)) return false;
        player.Server_TeleportCharacter(position);
        log.l($"[teamspawn] teleport player={player.playerName} team={player.playerColor.ToHex()} at={position:F3}");
        return true;
    }

    internal static bool TrySetBodyPosition(NetBody body) {
        if (body == null || body.plr == null || !TryResolve(body.plr, out var position)) return false;
        body.SetBodyPosition(position);
        log.l($"[teamspawn] body player={body.plr.playerName} team={body.plr.playerColor.ToHex()} at={position:F3}");
        return true;
    }

    static List<Team> GroupTeams(List<ScavPlayer> players) {
        var teams = new List<Team>();
        foreach (var player in players) {
            var team = teams.FirstOrDefault(t => t.Color == player.playerColor);
            if (team == null) {
                team = new Team { Color = player.playerColor };
                teams.Add(team);
            }
            team.Players.Add(player);
        }
        return teams;
    }

    static void SetTeamColor(ScavPlayer player, Color24 color) {
        try {
            player.ApplyNameAndColor(player.playerName, color);
        } catch (Exception e) {
            // Keep the server-side team mapping correct even if a visual
            // component for this player has not been created yet.
            player.playerColor = color;
            log.warn($"[teamspawn] color sync failed for {player.playerName}: {e.Message}");
        }
    }

    // "At most 4 teams" still applies. Extra colours are merged into the
    // smallest remaining team; team size itself is not capped.
    static void EnforceMaxTeams(List<Team> teams) {
        while (teams.Count > 4) {
            var extra = teams.OrderBy(t => t.Count).ThenBy(t => t.Color.Hash()).First();
            teams.Remove(extra);
            var target = teams.OrderBy(t => t.Count).ThenBy(t => t.Color.Hash()).First();
            log.l($"[teamspawn] max 4 teams: merging {extra.Count} player(s) from {extra.Color.ToHex()} into {target.Color.ToHex()}");
            foreach (var player in extra.Players) {
                SetTeamColor(player, target.Color);
                target.Players.Add(player);
            }
        }
    }

    static float PickSiteX(WorldGeneration world, Vector2 baseSite, List<float> usedX, float desiredOffset) {
        float limit = Mathf.Max(0f, (float)world.halfWidth - EdgeMargin);
        float x = Mathf.Clamp(baseSite.x + desiredOffset, -limit, limit);
        float minGap = RoomRadius * 2.2f;
        for (int i = 0; i < 12 && usedX.Any(u => Mathf.Abs(u - x) < minGap); i++) {
            float away = x >= baseSite.x ? -1f : 1f;
            x = Mathf.Clamp(x + away * RoomRadius * 2.5f, -limit, limit);
        }
        return x;
    }

    static void GenerateSite(WorldGeneration world, Vector2 site) {
        try {
            world.GenerateBlockCircle(site, RoomRadius, 3, 0.8f, 0f, autoUpdateChunk: true);
            world.GenerateBlockCircle(site, RingRadius, 4, 0.3f, 0f, autoUpdateChunk: true);
            world.GenerateBlockCircle(site, RoomRadius, 0, 0.15f, 0f, autoUpdateChunk: true);

            var podPos = site + Vector2.up * 4f;
            var startPrefab = Resources.Load<GameObject>("LifepodStart");
            if (startPrefab == null) {
                log.error("[teamspawn] LifepodStart prefab missing");
            } else {
                var podLayer = startPrefab.transform.childCount > 0 ? startPrefab.transform.GetChild(0) : null;
                var podTiles = podLayer != null ? podLayer.GetComponent<Tilemap>() : null;
                if (podTiles != null) {
                    world.GenerateObjectAtPos(world.WorldToBlockPos(podPos), podTiles, 0.97f);
                }
                if (podLayer != null && podLayer.childCount > 0) {
                    UnityEngine.Object.Instantiate(podLayer.GetChild(0).gameObject, podPos, Quaternion.identity);
                }
            }

            var podEntity = Resources.Load<GameObject>("Lifepod");
            if (podEntity == null) {
                log.error("[teamspawn] Lifepod prefab missing");
            } else {
                world.GenerateEntityAtPos(world.BlockToWorldPos(world.WorldToBlockPos(podPos)), podEntity);
            }

            ClearLiquids(world, site);
            log.l($"[teamspawn] generated site={site:F3}");
        } catch (Exception e) {
            log.error("[teamspawn] site generation failed at " + site + ": " + e);
        }
    }

    static void ClearLiquids(WorldGeneration world, Vector2 site) {
        if (FluidManager.main == null) return;
        var center = world.WorldToBlockPos(site);
        int maxX = (int)world.width - 1;
        int maxY = (int)world.height - 2;
        for (int x = Mathf.Max(0, center.x - 16); x <= Mathf.Min(maxX, center.x + 16); x++) {
            for (int y = Mathf.Max(1, center.y - 16); y <= Mathf.Min(maxY, center.y + 16); y++) {
                FluidManager.main.SetLiquid(x, y, 0);
            }
        }
    }
}
